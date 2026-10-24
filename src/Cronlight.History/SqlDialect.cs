namespace Cronlight.History;

/// <summary>The few places where SQL differs between the supported databases.</summary>
internal sealed record SqlDialect(string Name, string SchemaResource, bool UsesTop)
{
    public static readonly SqlDialect SqlServer = new("SQL Server", "SqlServer", UsesTop: true);
    public static readonly SqlDialect PostgreSql = new("PostgreSQL", "PostgreSql", UsesTop: false);
    public static readonly SqlDialect MySql = new("MySQL", "MySql", UsesTop: false);
    public static readonly SqlDialect Sqlite = new("SQLite", "Sqlite", UsesTop: false);

    /// <summary>Maps a Quartz ADO provider name (quartz.dataSource.*.provider) to a dialect.</summary>
    public static SqlDialect? ForProvider(string? providerName) => providerName switch
    {
        null => null,
        _ when providerName.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase) => SqlServer,
        _ when providerName.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) => PostgreSql,
        _ when providerName.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) => MySql,
        _ when providerName.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase) => Sqlite,
        _ => null
    };

    /// <summary>SELECT with a row limit; the limit is an integer the caller has already clamped.</summary>
    public string SelectTop(int limit, string columns, string rest) =>
        UsesTop ? $"SELECT TOP ({limit}) {columns} {rest}" : $"SELECT {columns} {rest} LIMIT {limit}";

    /// <summary>
    /// The CREATE TABLE/INDEX statements for this dialect, split into single statements. The table name may
    /// be schema-qualified; index names are built from the part after the schema.
    /// </summary>
    public IEnumerable<string> SchemaStatements(string tableName)
    {
        using var stream = typeof(SqlDialect).Assembly.GetManifestResourceStream($"Cronlight.History.Schema.{SchemaResource}.sql")
            ?? throw new InvalidOperationException($"Missing schema script for {Name}");
        using var reader = new StreamReader(stream);
        var script = string.Join('\n', reader.ReadToEnd().Split('\n').Where(line => !line.TrimStart().StartsWith("--")));

        var dot = tableName.LastIndexOf('.');
        return script
            .Replace("{TABLE}", tableName)
            .Replace("{NAME}", tableName[(dot + 1)..])
            .Replace("{SCHEMA}", tableName[..(dot + 1)])
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
