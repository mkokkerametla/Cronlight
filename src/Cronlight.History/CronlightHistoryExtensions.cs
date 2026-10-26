using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz.Spi;
using Cronlight.History;

namespace Cronlight;

public static class CronlightHistoryExtensions
{
    /// <summary>
    /// Records every job execution of the host's Quartz scheduler, and of schedulers added with
    /// <see cref="CronlightSchedulerExtensions.UseCronlight"/>, and makes it available to the dashboard.
    /// Uses the Quartz database when possible; see <see cref="JobHistoryOptions"/>.
    /// Register your own <see cref="IJobHistoryStore"/> before calling this to replace the built-in stores.
    /// </summary>
    public static IServiceCollection AddCronlightHistory(
        this IServiceCollection services,
        Action<JobHistoryOptions>? configure = null)
    {
        services.AddOptions<JobHistoryOptions>();
        if (configure != null)
            services.Configure(configure);

        services.TryAddSingleton<QuartzDataSource>();
        // Says which runs are going on, so runs left unfinished by a node that stopped can be told apart
        services.TryAddSingleton<RunningJobsReader>();
        services.TryAddSingleton<JobHistoryQueue>();
        services.TryAddSingleton<MisfireLinks>();
        services.TryAddSingleton<JobHistoryListener>();
        services.TryAddSingleton<IJobHistoryStore>(CreateStore);
        services.AddSchedulerAttachment<JobHistoryAttachment>();
        services.AddJobNotCreatedHandler<JobHistoryListener>();
        // Ignores everything logged outside a run, so it costs a check per log call
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RunLogProvider>());
        services.TryAddSingleton<RunLogMasks>();

        // First in the collection: hosted services stop in reverse order, so history is drained after Quartz stops
        if (!services.Any(d => d.ImplementationType == typeof(JobHistoryWriter)))
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService, JobHistoryWriter>());

        return services;
    }

    /// <summary>
    /// For a scheduler built in code: wraps its job factory so that what each run logs is kept with it in history
    /// (<see cref="JobHistoryOptions.CaptureLogs"/>), as it is for the host's own scheduler.
    /// <c>scheduler.JobFactory = jobFactory.WithCronlightRunLogs(services);</c> before starting it.
    /// </summary>
    public static IJobFactory WithCronlightRunLogs(this IJobFactory jobFactory, IServiceProvider services) =>
        jobFactory is RunLogJobFactory || !services.GetRequiredService<IOptions<JobHistoryOptions>>().Value.CaptureLogs
            ? jobFactory
            : new RunLogJobFactory(jobFactory, services.GetRequiredService<IOptions<JobHistoryOptions>>(),
                services.GetRequiredService<RunLogMasks>());

    private static IJobHistoryStore CreateStore(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<JobHistoryOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cronlight.History");

        if (options.Storage == JobHistoryStorage.InMemory)
            return new InMemoryJobHistoryStore(options.InMemoryCapacity);

        var dataSource = services.GetRequiredService<QuartzDataSource>();
        var dialect = SqlDialect.ForProvider(dataSource.ProviderName);
        var tableName = options.TableName ?? $"{dataSource.TablePrefix}CRONLIGHT_HISTORY";

        if (dataSource.Provider != null && dialect != null && QuartzDataSource.IsSafeIdentifier(tableName))
        {
            logger.LogInformation("Cronlight history is stored in {Dialect} table {Table}", dialect.Name, tableName);
            return new AdoJobHistoryStore(dataSource, dialect, tableName, options.CreateTable,
                services.GetRequiredService<ILogger<AdoJobHistoryStore>>());
        }

        var reason = dataSource.Provider == null
            ? "the Quartz job store is not a database the dashboard can open"
            : dialect == null
                ? $"provider '{dataSource.ProviderName}' is not supported (SQL Server, PostgreSQL, MySQL and SQLite are)"
                : $"table name '{tableName}' is not a plain identifier";

        if (options.Storage == JobHistoryStorage.Database)
            throw new InvalidOperationException($"Cronlight history cannot use the database: {reason}.");

        logger.LogWarning("Cronlight history is kept in memory on each node because {Reason}", reason);
        return new InMemoryJobHistoryStore(options.InMemoryCapacity);
    }
}
