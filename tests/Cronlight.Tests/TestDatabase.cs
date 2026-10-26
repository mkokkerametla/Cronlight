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

    // Every SQLite file of a test run stays here until the run ends. A file deleted while a connection to it may still
    // be open lets a later file reuse its inode, and SQLite tracks open files by inode, so a new database could be
    // read through the deleted one's file handle.
    private static readonly Lazy<string> SqliteDirectory = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cronlight-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the system to clear from its temp folder
            }
        };
        return directory;
    });

    public string Name { get; }
    private readonly string connectionString;
    private readonly Action<SchedulerBuilder.PersistentStoreOptions, string, string> useStore;
    private readonly string? schemaScript;
    private readonly Func<string, DbConnection> connect;
    // A database of its own, created with the Quartz tables, rather than one shared by the test run
    private readonly bool perHost;

    private TestDatabase(
        string name,
        string connectionString,
        Action<SchedulerBuilder.PersistentStoreOptions, string, string> useStore,
        string? schemaScript,
        Func<string, DbConnection> connect,
        bool perHost = false)
    {
        Name = name;
        this.connectionString = connectionString;
        this.useStore = useStore;
        this.schemaScript = schemaScript;
        this.connect = connect;
        this.perHost = perHost;
    }

    /// <summary>
    /// A fresh SQLite file per host; always available. Unpooled, so a connection closed by any host, Quartz's or
    /// Cronlight's, closes the file, and a test run does not keep hundreds of files open.
    /// </summary>
    public static TestDatabase Sqlite()
    {
        var path = Path.Combine(SqliteDirectory.Value, $"{Guid.NewGuid():N}.db");
        var database = new TestDatabase("SQLite", $"Data Source={path};Pooling=False",
            (store, cs, ds) => store.UseMicrosoftSQLite(o => o.ConnectionString = cs, ds),
            "tables_sqlite.sql", cs => new SqliteConnection(cs), perHost: true);
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
        perHost || schemaScript == null
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
