using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Quartz;

namespace Cronlight.Tests;

/// <summary>
/// The database a test host's Quartz store runs on. SQLite needs nothing; the others run when their
/// connection string is in an environment variable (see README), and are skipped otherwise.
/// </summary>
public sealed class TestDatabase
{
    public const string PostgresVariable = "QUARTZ_TEST_POSTGRES";
    public const string SqlServerVariable = "QUARTZ_TEST_SQLSERVER";
    public const string MySqlVariable = "QUARTZ_TEST_MYSQL";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> SchemasCreated = new();

    public string Name { get; }
    private readonly string connectionString;
    private readonly Action<SchedulerBuilder.PersistentStoreOptions, string, string> useStore;
    private readonly string? schemaScript;
    private readonly Func<string, DbConnection> connect;
    private readonly string? temporaryFile;

    private TestDatabase(
        string name,
        string connectionString,
        Action<SchedulerBuilder.PersistentStoreOptions, string, string> useStore,
        string? schemaScript,
        Func<string, DbConnection> connect,
        string? temporaryFile = null)
    {
        Name = name;
        this.connectionString = connectionString;
        this.useStore = useStore;
        this.schemaScript = schemaScript;
        this.connect = connect;
        this.temporaryFile = temporaryFile;
    }

    /// <summary>A fresh SQLite file per host; always available.</summary>
    public static TestDatabase Sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cronlight-{Guid.NewGuid():N}.db");
        var database = new TestDatabase("SQLite", $"Data Source={path}",
            (store, cs, ds) => store.UseMicrosoftSQLite(o => o.ConnectionString = cs, ds),
            "tables_sqlite.sql", cs => new SqliteConnection(cs), path);
        database.RunScript("tables_sqlite.sql").GetAwaiter().GetResult();
        return database;
    }

    public static TestDatabase? FromEnvironment(string name) => name switch
    {
        "PostgreSQL" when Environment.GetEnvironmentVariable(PostgresVariable) is { } cs => new TestDatabase(name, cs,
            (store, c, ds) => store.UsePostgres(o => o.ConnectionString = c, ds),
            "Schemas/tables_postgres.sql", c => new NpgsqlConnection(c)),
        "SQL Server" when Environment.GetEnvironmentVariable(SqlServerVariable) is { } cs => new TestDatabase(name, cs,
            (store, c, ds) => store.UseSqlServer(o => o.ConnectionString = c, ds),
            "Schemas/tables_sqlServer.sql", c => new SqlConnection(c)),
        "MySQL" when Environment.GetEnvironmentVariable(MySqlVariable) is { } cs => new TestDatabase(name, cs,
            (store, c, ds) => store.UseMySqlConnector(o => o.ConnectionString = c, ds),
            "Schemas/tables_mysql_innodb.sql", c => new MySqlConnection(c)),
        _ => null
    };

    public void UseFor(SchedulerBuilder.PersistentStoreOptions store, string dataSourceName) =>
        useStore(store, connectionString, dataSourceName);

    /// <summary>Creates the Quartz tables once per test run. The scripts drop existing tables first.</summary>
    public Task EnsureQuartzSchema() =>
        temporaryFile != null || schemaScript == null
            ? Task.CompletedTask
            : SchemasCreated.GetOrAdd(Name, _ => new Lazy<Task>(() => RunScript(schemaScript))).Value;

    public async Task<int> Execute(string sql)
    {
        await using var connection = connect(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> Scalar(string sql)
    {
        await using var connection = connect(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public void Cleanup()
    {
        if (temporaryFile == null)
            return;
        SqliteConnection.ClearAllPools();
        File.Delete(temporaryFile);
    }

    private async Task RunScript(string relativePath)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, relativePath));
        // SQL Server scripts use GO batch separators and a placeholder USE statement
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Select(b => Regex.Replace(b, @"^\s*USE \[[^\]]*\];?\s*$", "", RegexOptions.Multiline).Trim())
            .Where(b => b.Length > 0);
        foreach (var batch in batches)
            await Execute(batch);
    }

    public override string ToString() => Name;
}
