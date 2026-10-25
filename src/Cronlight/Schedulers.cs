using System.Collections.Concurrent;
using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cronlight;

/// <summary>Something Cronlight attaches to every scheduler it serves, such as the listener that records history.</summary>
internal interface ISchedulerAttachment
{
    /// <summary>Attaches to the scheduler. Called again for a scheduler already attached, so it must do nothing then.</summary>
    Task Attach(IScheduler scheduler, CancellationToken cancellationToken);
}

/// <summary>
/// The schedulers Cronlight serves: the host's own (from AddQuartz) and any the host adds with
/// <see cref="CronlightSchedulerExtensions.UseCronlight"/>. Quartz keeps every scheduler in the process in one static
/// registry, which can hold other apps' schedulers too, so an extra scheduler is served only once the host adds it.
/// </summary>
internal sealed class CronlightSchedulers(ISchedulerFactory factory, IEnumerable<ISchedulerAttachment> attachments)
{
    private readonly ConcurrentDictionary<string, IScheduler> extra = new(StringComparer.Ordinal);
    // Added schedulers whose database the host described, so their tables can be read directly
    private readonly ConcurrentDictionary<string, QuartzDataSource> dataSources = new(StringComparer.Ordinal);

    public Task<IScheduler> Default(CancellationToken cancellationToken = default) => factory.GetScheduler(cancellationToken);

    /// <summary>
    /// The host's scheduler as it was attached at startup. Quartz forgets a scheduler once it has shut down, and asking
    /// its factory again then creates a new one, so this is how to tell that the host's own has shut down.
    /// </summary>
    public IScheduler? AttachedHost { get; private set; }

    public async Task AttachHost(CancellationToken cancellationToken)
    {
        var host = await Default(cancellationToken);
        await Attach(host, cancellationToken);
        AttachedHost = host;
    }

    /// <summary>The schedulers the host added, without the host's own, leaving out ones that have shut down.</summary>
    public IEnumerable<IScheduler> Added => extra.Values.Where(s => !s.IsShutdown).OrderBy(s => s.SchedulerName, StringComparer.Ordinal);

    public async Task Add(IScheduler scheduler, QuartzDataSource? dataSource, CancellationToken cancellationToken)
    {
        await Attach(scheduler, cancellationToken);
        if (scheduler.SchedulerName == (await Default(cancellationToken)).SchedulerName)
            return;
        extra[scheduler.SchedulerName] = scheduler;
        if (dataSource?.Provider != null)
            dataSources[scheduler.SchedulerName] = dataSource;
    }

    /// <summary>
    /// The database whose Quartz tables hold this scheduler: the host's own, given its data source, or an added
    /// scheduler's when the host gave its properties. Null for in-memory stores and added schedulers without them.
    /// </summary>
    public async Task<QuartzDataSource?> DataSource(IScheduler scheduler, QuartzDataSource hosts, CancellationToken cancellationToken) =>
        scheduler.SchedulerName == (await Default(cancellationToken)).SchedulerName
            ? hosts.Provider != null ? hosts : null
            : dataSources.GetValueOrDefault(scheduler.SchedulerName);

    public async Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        foreach (var attachment in attachments)
            await attachment.Attach(scheduler, cancellationToken);
    }

    /// <summary>Every scheduler served, the host's own first, then by name. Ones that have been shut down are left out.</summary>
    public async Task<IReadOnlyList<IScheduler>> All(CancellationToken cancellationToken = default)
    {
        return [await Default(cancellationToken), .. Added];
    }

    /// <summary>The scheduler with this name, the host's own for null or empty, or null when none is served by that name.</summary>
    public async Task<IScheduler?> Find(string? name, CancellationToken cancellationToken = default)
    {
        var host = await Default(cancellationToken);
        if (string.IsNullOrEmpty(name) || name == host.SchedulerName)
            return host;
        return extra.TryGetValue(name, out var scheduler) && !scheduler.IsShutdown ? scheduler : null;
    }

    /// <summary>
    /// The job's type, or null when it cannot be read, e.g. a type this app cannot load. For records made where the
    /// job store must not be read, such as misfires.
    /// </summary>
    public async Task<string?> JobType(string schedulerName, JobKey job, CancellationToken cancellationToken = default)
    {
        try
        {
            var scheduler = await Find(schedulerName, cancellationToken);
            return scheduler == null ? null : (await scheduler.GetJobDetail(job, cancellationToken))?.JobType.FullName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

public static class CronlightSchedulerExtensions
{
    /// <summary>
    /// Serves a scheduler the host built itself (beyond the one from AddQuartz) in the dashboard, and attaches
    /// Cronlight's history, alerts, retries and secret references to it. Call it before starting the scheduler, so
    /// its first runs are covered too. The dashboard picks a scheduler with its <c>?scheduler=</c> parameter.
    /// </summary>
    public static Task UseCronlight(this IScheduler scheduler, IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<CronlightSchedulers>().Add(scheduler, null, cancellationToken);

    /// <summary>
    /// As <see cref="UseCronlight(IScheduler, IServiceProvider, CancellationToken)"/>, given the Quartz properties the
    /// scheduler was built from. With an ADO job store, the dashboard then reads its tables directly, as it does for
    /// the host's own scheduler: its job list costs two queries however many jobs it has, and its Running tab shows
    /// every node.
    /// </summary>
    public static Task UseCronlight(this IScheduler scheduler, IServiceProvider services, NameValueCollection properties,
        CancellationToken cancellationToken = default) =>
        services.GetRequiredService<CronlightSchedulers>().Add(scheduler, QuartzDataSource.ForProperties(properties, services), cancellationToken);
}

/// <summary>Attaches Cronlight to the host's own scheduler before Quartz starts running jobs.</summary>
internal sealed class SchedulerRegistration(CronlightSchedulers schedulers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => schedulers.AttachHost(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class SchedulerAttachmentExtensions
{
    /// <summary>The scheduler registry, shared by AddCronlight, AddCronlightAlerts and AddCronlightHistory.</summary>
    public static IServiceCollection AddCronlightSchedulers(this IServiceCollection services)
    {
        services.TryAddSingleton<CronlightSchedulers>();
        // First in the collection: hosted services start in registration order, so listeners are attached
        // before Quartz's hosted service starts running jobs
        if (!services.Any(d => d.ImplementationType == typeof(SchedulerRegistration)))
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService, SchedulerRegistration>());
        return services;
    }

    public static IServiceCollection AddSchedulerAttachment<T>(this IServiceCollection services) where T : class, ISchedulerAttachment
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISchedulerAttachment, T>());
        return services.AddCronlightSchedulers();
    }

    /// <summary>Adds a job listener for every job, unless the scheduler already has one by that name.</summary>
    public static void AddJobListenerOnce(this IScheduler scheduler, IJobListener listener)
    {
        // GetJobListener(name) throws for unknown names in Quartz 3, so look through the list instead
        if (scheduler.ListenerManager.GetJobListeners().All(l => l.Name != listener.Name))
            scheduler.ListenerManager.AddJobListener(listener, GroupMatcher<JobKey>.AnyGroup());
    }

    public static void AddTriggerListenerOnce(this IScheduler scheduler, ITriggerListener listener)
    {
        if (scheduler.ListenerManager.GetTriggerListeners().All(l => l.Name != listener.Name))
            scheduler.ListenerManager.AddTriggerListener(listener, GroupMatcher<TriggerKey>.AnyGroup());
    }
}

/// <summary>A request named a scheduler this app does not serve. The API answers it with 404.</summary>
internal sealed class UnknownSchedulerException(string name) : Exception($"No scheduler named '{name}' is served here.");
