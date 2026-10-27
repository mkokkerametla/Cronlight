using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cronlight.History;

/// <summary>
/// Stores history in the Quartz database, through the same provider and connection string as the job store.
/// Times are UTC ticks in BIGINT columns, like Quartz's own tables, which keeps the SQL portable.
/// </summary>
internal sealed class AdoJobHistoryStore(
    QuartzDataSource dataSource,
    SqlDialect dialect,
    string tableName,
    bool createTable,
    ILogger<AdoJobHistoryStore> logger) : IJobHistoryStore, IUnfinishedRuns
{
    private const string BaseColumns =
        "SCHED_NAME, ENTRY_ID, INSTANCE_NAME, JOB_NAME, JOB_GROUP, JOB_TYPE, TRIGGER_NAME, TRIGGER_GROUP, " +
        "SCHEDULED_TIME, FIRED_TIME, FINISHED_TIME, DURATION_MS, STATUS, ERROR, REFIRE_COUNT";

    // Added after the first release: ATTEMPT with retries, DATA for repeating a run exactly, RUN_LOG for what runs logged. A
    // table created by an earlier version gets them at startup, unless CreateTable is off; then history is written without them.
    private const string AttemptColumn = "ATTEMPT";
    private const string DataColumn = "DATA";
    private const string LogColumn = "RUN_LOG";
    private static readonly (string Column, string Purpose)[] AddedColumns =
    [
        (AttemptColumn, "retry attempts are not recorded"), (DataColumn, "each run's own data is not kept"),
        (LogColumn, "what runs log is not kept")
    ];
    private volatile string[] addedColumns = AddedColumns.Select(c => c.Column).ToArray();

    private bool Has(string column) => addedColumns.Contains(column);
    private string InsertColumns => string.Join(", ", [BaseColumns, .. addedColumns]);

    // Queries read whether a run has a log, not the log itself, which can be long
    private string Columns => string.Join(", ",
        ["ID", BaseColumns, .. addedColumns.Select(c => c == LogColumn ? $"CASE WHEN {c} IS NULL THEN 0 ELSE 1 END" : c)]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The writer initializes the store in the background at startup. Until then the table may not exist yet,
    // so reads return no history instead of failing; once initialization has failed, reads fail again so the
    // dashboard shows the problem (for example a missing table with CreateTable off).
    private volatile bool ready;
    private volatile bool initializationFailed;

    private bool NotReadyYet => !ready && !initializationFailed;

    /// <summary>A column or index could not be added at the last start (a lock timeout, say): the writer tries again later.</summary>
    public bool UpgradePending { get; private set; }

    public string Description => $"{dialect.Name} table {tableName}";

    public async Task Initialize(CancellationToken cancellationToken)
    {
        try
        {
            await CreateTableIfMissing(cancellationToken);
            ready = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            initializationFailed = true;
            throw;
        }
    }

    private async Task CreateTableIfMissing(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        if (await TableExists(connection, cancellationToken))
        {
            UpgradePending = false;
            await AddMissingColumns(connection, cancellationToken);
            await AddMissingIndexes(connection, cancellationToken);
            return;
        }

        if (!createTable)
            throw new InvalidOperationException(
                $"History table {tableName} does not exist and CreateTable is off. Create it with the {dialect.Name} script in the package's Schema folder.");

        logger.LogInformation("Creating Cronlight history table {Table} ({Dialect})", tableName, dialect.Name);
        // One transaction where DDL is transactional (all but MySQL): a failing statement must not leave a
        // table without its indexes, which the existence check above would accept on every later start
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in dialect.SchemaStatements(tableName))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await Insert(connection, record, cancellationToken);
    }

    public async Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        var log = Has(LogColumn) ? $", {LogColumn} = {P("run_log")}" : "";
        command.CommandText =
            $"UPDATE {tableName} SET FINISHED_TIME = {P("finished")}, DURATION_MS = {P("duration")}, STATUS = {P("status")}, " +
            $"ERROR = {P("error")}{log} WHERE SCHED_NAME = {P("sched")} AND ENTRY_ID = {P("entry")} AND REFIRE_COUNT = {P("refire")}";
        if (Has(LogColumn))
            Add(command, "run_log", LogJson(record));
        Add(command, "finished", record.FinishedTimeUtc?.UtcTicks);
        Add(command, "duration", record.DurationMs);
        Add(command, "status", record.Status.ToString());
        Add(command, "error", record.Error);
        Add(command, "refire", record.RefireCount);
        Add(command, "sched", record.SchedulerName);
        Add(command, "entry", record.FireInstanceId);

        // No start row (vetoed jobs, or a start lost while the store was unavailable): write the whole record
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            await Insert(connection, record, cancellationToken);
    }

    public async Task<bool> FinishIfRunning(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {tableName} SET FINISHED_TIME = {P("finished")}, DURATION_MS = {P("duration")}, STATUS = {P("status")}, " +
            $"ERROR = {P("error")} WHERE SCHED_NAME = {P("sched")} AND ENTRY_ID = {P("entry")} AND REFIRE_COUNT = {P("refire")} " +
            $"AND STATUS = {P("running")}";
        Add(command, "finished", record.FinishedTimeUtc?.UtcTicks);
        Add(command, "duration", record.DurationMs);
        Add(command, "status", record.Status.ToString());
        Add(command, "error", record.Error);
        Add(command, "sched", record.SchedulerName);
        Add(command, "entry", record.FireInstanceId);
        Add(command, "refire", record.RefireCount);
        Add(command, "running", nameof(JobExecutionStatus.Running));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<JobExecutionRecord>> Query(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken)
    {
        if (NotReadyYet)
            return [];

        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();

        var where = new StringBuilder($"WHERE SCHED_NAME = {P("sched")}");
        Add(command, "sched", schedulerName);
        if (query.JobGroup != null)
        {
            where.Append($" AND JOB_GROUP = {P("group")}");
            Add(command, "group", query.JobGroup);
        }
        if (query.JobName != null)
        {
            where.Append($" AND JOB_NAME = {P("name")}");
            Add(command, "name", query.JobName);
        }
        if (query.Status != null)
        {
            where.Append($" AND STATUS = {P("status")}");
            Add(command, "status", query.Status.ToString());
        }
        if (query.BeforeId != null)
        {
            where.Append($" AND ID < {P("before")}");
            Add(command, "before", query.BeforeId);
        }
        if (query.FiredFromUtc != null)
        {
            where.Append($" AND FIRED_TIME >= {P("from")}");
            Add(command, "from", query.FiredFromUtc.Value.UtcTicks);
        }
        if (query.FiredBeforeUtc != null)
        {
            where.Append($" AND FIRED_TIME < {P("to")}");
            Add(command, "to", query.FiredBeforeUtc.Value.UtcTicks);
        }

        command.CommandText = dialect.SelectTop(Math.Clamp(query.Limit, 1, 500), Columns, $"FROM {tableName} {where} ORDER BY ID DESC");

        var records = new List<JobExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(Read(reader));
        return records;
    }

    public async Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        if (NotReadyYet)
            return new JobHistorySummary(sinceUtc, 0, 0, 0, 0);

        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT STATUS, COUNT(*) FROM {tableName} WHERE SCHED_NAME = {P("sched")} AND FIRED_TIME >= {P("since")} GROUP BY STATUS";
        Add(command, "sched", schedulerName);
        Add(command, "since", sinceUtc.UtcTicks);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));

        int Count(JobExecutionStatus status) => counts.GetValueOrDefault(status.ToString());
        return new JobHistorySummary(sinceUtc, Count(JobExecutionStatus.Succeeded), Count(JobExecutionStatus.Failed),
            Count(JobExecutionStatus.Vetoed), Count(JobExecutionStatus.Running));
    }

    public async Task<IReadOnlyList<JobRunLogEntry>?> GetLog(string schedulerName, long id, CancellationToken cancellationToken)
    {
        if (NotReadyYet || !Has(LogColumn))
            return null;

        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {LogColumn} FROM {tableName} WHERE SCHED_NAME = {P("sched")} AND ID = {P("id")}";
        Add(command, "sched", schedulerName);
        Add(command, "id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? JsonSerializer.Deserialize<List<JobRunLogEntry>>(json, Json)
            : null;
    }

    private static string? LogJson(JobExecutionRecord record) =>
        record.Log is { Count: > 0 } log ? JsonSerializer.Serialize(log, Json) : null;

    public async Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {tableName} WHERE SCHED_NAME = {P("sched")} AND FIRED_TIME < {P("cutoff")}";
        Add(command, "sched", schedulerName);
        Add(command, "cutoff", cutoffUtc.UtcTicks);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Columns added after the first release. With CreateTable off a missing column is reported, and history is
    /// written without it, as it is when adding the column fails (the app may not change the table).
    /// </summary>
    private async Task AddMissingColumns(DbConnection connection, CancellationToken cancellationToken)
    {
        var present = new List<string>();
        foreach (var (column, purpose) in AddedColumns)
        {
            if (await HasColumn(connection, column, cancellationToken))
            {
                present.Add(column);
                continue;
            }

            var statement = $"ALTER TABLE {tableName} ADD {column} {ColumnType(column)} NULL";
            if (!createTable)
            {
                logger.LogWarning("History table {Table} has no {Column} column, so {Purpose}. Add it with: {Statement}",
                    tableName, column, purpose, statement);
                continue;
            }
            logger.LogInformation("Adding column {Column} to history table {Table}", column, tableName);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken);
                present.Add(column);
            }
            // Another node starting at the same time may have added it; otherwise history carries on without it
            catch (DbException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (await HasColumn(connection, column, cancellationToken))
                    present.Add(column);
                else
                {
                    UpgradePending = true;
                    logger.LogWarning(ex, "Could not add column {Column} to history table {Table}, so {Purpose} until it is added; " +
                        "it is tried again every CleanupInterval. Add it with: {Statement}", column, tableName, purpose, statement);
                }
            }
        }
        addedColumns = present.ToArray();
    }

    private async Task<bool> HasColumn(DbConnection connection, string column, CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = connection.CreateCommand();
            probe.CommandText = $"SELECT {column} FROM {tableName} WHERE 1 = 0";
            await probe.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private string ColumnType(string column) => (column, dialect) switch
    {
        (AttemptColumn, _) when dialect == SqlDialect.PostgreSql || dialect == SqlDialect.Sqlite => "INTEGER",
        (AttemptColumn, _) => "INT",
        (_, _) when dialect == SqlDialect.SqlServer => "NVARCHAR(MAX)",
        (_, _) when dialect == SqlDialect.MySql => "MEDIUMTEXT",
        _ => "TEXT",
    };

    /// <summary>
    /// Indexes added after the first release, which tables created by an earlier version lack. With CreateTable
    /// off the host owns the schema, so a missing index is only reported, with the statement to add it.
    /// </summary>
    private async Task AddMissingIndexes(DbConnection connection, CancellationToken cancellationToken)
    {
        // Keeps filtering every job's runs by outcome (e.g. all failures) from scanning the whole table
        foreach (var statement in dialect.SchemaStatements(tableName).Where(s => s.Contains("_STATUS ON", StringComparison.Ordinal)))
        {
            var index = Regex.Match(statement, @"CREATE INDEX (?:\S+\.)?(\S+) ON").Groups[1].Value;
            if (await IndexExists(connection, index, cancellationToken))
                continue;
            if (!createTable)
            {
                logger.LogWarning("History table {Table} has no index {Index}, so filtering runs by outcome is slow on large tables. Add it with: {Statement}",
                    tableName, index, statement);
                continue;
            }
            logger.LogInformation("Adding index {Index} to history table {Table}", index, tableName);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            // As for columns: another node may have added it, or the app may not change the table, or a large table takes
            // longer than the command timeout. History works without the index.
            catch (DbException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (!await IndexExists(connection, index, cancellationToken))
                {
                    UpgradePending = true;
                    logger.LogWarning(ex, "Could not add index {Index} to history table {Table}, so filtering runs by outcome is slow on large tables; " +
                        "it is tried again every CleanupInterval. Add it with: {Statement}", index, tableName, statement);
                }
            }
        }
    }

    /// <summary>
    /// Whether this table has the index, looked up in the table's own schema or database when its name gives one, and
    /// in the connection's otherwise: a table of the same name elsewhere must not count.
    /// </summary>
    private async Task<bool> IndexExists(DbConnection connection, string index, CancellationToken cancellationToken)
    {
        var dot = tableName.LastIndexOf('.');
        var table = tableName[(dot + 1)..];
        var schema = dot < 0 ? null : tableName[..dot];
        // SQL Server: a database.schema.table name keeps its indexes in that database's catalog
        var parts = tableName.Split('.');
        var catalog = parts.Length == 3 ? $"{parts[0]}." : "";
        await using var command = connection.CreateCommand();
        command.CommandText = dialect switch
        {
            _ when dialect == SqlDialect.SqlServer =>
                $"SELECT COUNT(*) FROM {catalog}sys.indexes WHERE name = {P("index")} AND object_id = OBJECT_ID({P("table")})",
            // The table the name finds, through the search path when it names no schema. Unquoted names are stored in lower
            // case, and cut to 63 characters.
            _ when dialect == SqlDialect.PostgreSql =>
                "SELECT COUNT(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid " +
                $"WHERE i.indrelid = to_regclass({P("table")}) AND c.relname = left(lower({P("index")}), 63)",
            _ when dialect == SqlDialect.MySql =>
                $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE INDEX_NAME = {P("index")} AND TABLE_NAME = {P("table")} " +
                $"AND TABLE_SCHEMA = {(schema == null ? "DATABASE()" : P("schema"))}",
            _ => $"SELECT COUNT(*) FROM {tableName[..(dot + 1)]}sqlite_master WHERE type = 'index' AND name = {P("index")}",
        };
        Add(command, "index", index);
        Add(command, "table", dialect == SqlDialect.SqlServer || dialect == SqlDialect.PostgreSql ? tableName : table);
        if (schema != null && dialect == SqlDialect.MySql)
            Add(command, "schema", schema);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private async Task<bool> TableExists(DbConnection connection, CancellationToken cancellationToken)
    {
        // Portable existence check: selecting nothing from a missing table fails on every database
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE 1 = 0";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private async Task Insert(DbConnection connection, JobExecutionRecord r, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {tableName} ({InsertColumns}) VALUES ({P("sched")}, {P("entry")}, {P("instance")}, " +
            $"{P("job")}, {P("group")}, {P("type")}, {P("trigger")}, {P("triggerGroup")}, {P("scheduled")}, {P("fired")}, " +
            $"{P("finished")}, {P("duration")}, {P("status")}, {P("error")}, {P("refire")}" +
            string.Concat(addedColumns.Select(c => $", {P(c.ToLowerInvariant())}")) + ")";
        Add(command, "sched", r.SchedulerName);
        Add(command, "entry", r.FireInstanceId);
        Add(command, "instance", r.InstanceName);
        Add(command, "job", r.JobName);
        Add(command, "group", r.JobGroup);
        Add(command, "type", r.JobType);
        Add(command, "trigger", r.TriggerName);
        Add(command, "triggerGroup", r.TriggerGroup);
        Add(command, "scheduled", r.ScheduledFireTimeUtc?.UtcTicks);
        Add(command, "fired", r.FiredTimeUtc.UtcTicks);
        Add(command, "finished", r.FinishedTimeUtc?.UtcTicks);
        Add(command, "duration", r.DurationMs);
        Add(command, "status", r.Status.ToString());
        Add(command, "error", r.Error);
        Add(command, "refire", r.RefireCount);
        if (Has(AttemptColumn))
            Add(command, "attempt", r.Attempt);
        if (Has(DataColumn))
            Add(command, "data", r.Data is { Count: > 0 } data ? JsonSerializer.Serialize(data, Json) : null);
        if (Has(LogColumn))
            Add(command, "run_log", LogJson(r));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private JobExecutionRecord Read(DbDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        long? Long(int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i));
        DateTimeOffset? Time(int i) => Long(i) is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        // Added columns follow the base ones, in the order present
        int? Added(string column) => Array.IndexOf(addedColumns, column) is >= 0 and var i ? 16 + i : null;

        return new JobExecutionRecord
        {
            Id = Long(0)!.Value,
            SchedulerName = reader.GetString(1),
            FireInstanceId = reader.GetString(2),
            InstanceName = reader.GetString(3),
            JobName = reader.GetString(4),
            JobGroup = reader.GetString(5),
            JobType = Text(6),
            TriggerName = Text(7),
            TriggerGroup = Text(8),
            ScheduledFireTimeUtc = Time(9),
            FiredTimeUtc = Time(10)!.Value,
            FinishedTimeUtc = Time(11),
            DurationMs = Long(12),
            Status = Enum.Parse<JobExecutionStatus>(reader.GetString(13)),
            Error = Text(14),
            RefireCount = Convert.ToInt32(reader.GetValue(15)),
            Attempt = Added(AttemptColumn) is { } a && Long(a) is { } attempt ? (int)attempt : 1,
            Data = Added(DataColumn) is { } d && Text(d) is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) : null,
            HasLog = Added(LogColumn) is { } l && Long(l) == 1
        };
    }

    private string P(string name) => dataSource.Parameter(name);

    private void Add(DbCommand command, string name, object? value) => dataSource.AddParameter(command, name, value);
}
