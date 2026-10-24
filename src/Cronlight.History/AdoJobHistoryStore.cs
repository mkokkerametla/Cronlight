using System.Data.Common;
using System.Text;
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
    ILogger<AdoJobHistoryStore> logger) : IJobHistoryStore
{
    private const string InsertColumns =
        "SCHED_NAME, ENTRY_ID, INSTANCE_NAME, JOB_NAME, JOB_GROUP, JOB_TYPE, TRIGGER_NAME, TRIGGER_GROUP, " +
        "SCHEDULED_TIME, FIRED_TIME, FINISHED_TIME, DURATION_MS, STATUS, ERROR, REFIRE_COUNT";

    private const string Columns = "ID, " + InsertColumns;

    public string Description => $"{dialect.Name} table {tableName}";

    public async Task Initialize(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        if (await TableExists(connection, cancellationToken))
            return;

        if (!createTable)
            throw new InvalidOperationException(
                $"History table {tableName} does not exist and CreateTable is off. Create it with the {dialect.Name} script in the package's Schema folder.");

        logger.LogInformation("Creating Cronlight history table {Table} ({Dialect})", tableName, dialect.Name);
        foreach (var statement in dialect.SchemaStatements(tableName))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
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
        command.CommandText =
            $"UPDATE {tableName} SET FINISHED_TIME = {P("finished")}, DURATION_MS = {P("duration")}, STATUS = {P("status")}, " +
            $"ERROR = {P("error")}, REFIRE_COUNT = {P("refire")} WHERE SCHED_NAME = {P("sched")} AND ENTRY_ID = {P("entry")}";
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

    public async Task<IReadOnlyList<JobExecutionRecord>> Query(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken)
    {
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

        command.CommandText = dialect.SelectTop(Math.Clamp(query.Limit, 1, 500), Columns, $"FROM {tableName} {where} ORDER BY ID DESC");

        var records = new List<JobExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(Read(reader));
        return records;
    }

    public async Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
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

    public async Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {tableName} WHERE SCHED_NAME = {P("sched")} AND FIRED_TIME < {P("cutoff")}";
        Add(command, "sched", schedulerName);
        Add(command, "cutoff", cutoffUtc.UtcTicks);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
            $"{P("finished")}, {P("duration")}, {P("status")}, {P("error")}, {P("refire")})";
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
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JobExecutionRecord Read(DbDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        long? Long(int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i));
        DateTimeOffset? Time(int i) => Long(i) is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

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
            RefireCount = Convert.ToInt32(reader.GetValue(15))
        };
    }

    private string P(string name) => dataSource.Parameter(name);

    private void Add(DbCommand command, string name, object? value) => dataSource.AddParameter(command, name, value);
}
