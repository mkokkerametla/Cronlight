using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.AdoJobStore.Common;

namespace Cronlight;

/// <summary>
/// Opens connections to the same database the host's ADO job store uses. Quartz 3 keeps its data
/// sources inside the job store, so this rebuilds a provider from the settings given to AddQuartz().
/// </summary>
internal sealed partial class QuartzDataSource
{
    private readonly IOptions<QuartzOptions> quartzOptions;
    private readonly IConfiguration configuration;
    private readonly ILogger<QuartzDataSource> logger;
    private readonly Lazy<DbProvider?> provider;

    public QuartzDataSource(IOptions<QuartzOptions> quartzOptions, IConfiguration configuration, ILogger<QuartzDataSource> logger)
    {
        this.quartzOptions = quartzOptions;
        this.configuration = configuration;
        this.logger = logger;
        provider = new Lazy<DbProvider?>(CreateProvider);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.]*$")]
    private static partial Regex SafeIdentifier();

    /// <summary>Quartz's table prefix, e.g. QRTZ_. Validated so it can be concatenated into SQL.</summary>
    public string TablePrefix => quartzOptions.Value.GetValueOrDefault("quartz.jobStore.tablePrefix") ?? "QRTZ_";

    /// <summary>Quartz provider name such as SqlServer, Npgsql, MySql or SQLite-Microsoft.</summary>
    public string? ProviderName => DataSourceSetting("provider");

    /// <summary>Null when the host has no ADO store, or one the dashboard cannot reproduce.</summary>
    public DbProvider? Provider => provider.Value;

    public static bool IsSafeIdentifier(string value) => SafeIdentifier().IsMatch(value);

    public string Parameter(string name) => Provider!.Metadata.GetParameterName(name);

    public void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = Provider!.Metadata.UseParameterNamePrefixInParameterCollection ? Parameter(name) : name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public async Task<DbConnection> OpenConnection(CancellationToken cancellationToken)
    {
        var connection = Provider!.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string? DataSourceSetting(string setting) =>
        quartzOptions.Value.GetValueOrDefault("quartz.jobStore.dataSource") is { } dataSource
            ? quartzOptions.Value.GetValueOrDefault($"quartz.dataSource.{dataSource}.{setting}")
            : null;

    private DbProvider? CreateProvider()
    {
        var dataSource = quartzOptions.Value.GetValueOrDefault("quartz.jobStore.dataSource");
        if (dataSource == null)
            return null; // RAM job store

        if (!IsSafeIdentifier(TablePrefix))
        {
            logger.LogWarning("Quartz table prefix {Prefix} contains unexpected characters; direct database reads are disabled", TablePrefix);
            return null;
        }

        var providerName = ProviderName;
        var connectionString = DataSourceSetting("connectionString")
            ?? (DataSourceSetting("connectionStringName") is { } name ? configuration.GetConnectionString(name) : null);

        if (providerName == null || connectionString == null)
        {
            // e.g. a custom connectionProvider.type: nothing the dashboard can reproduce
            logger.LogInformation("Quartz data source {DataSource} has no provider/connection string the dashboard can use", dataSource);
            return null;
        }

        try
        {
            return new DbProvider(providerName, connectionString);
        }
        catch (Exception ex) when (ex is SchedulerException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not create provider {Provider} for data source {DataSource}", providerName, dataSource);
            return null;
        }
    }
}
