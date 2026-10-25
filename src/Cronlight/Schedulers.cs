using System.Collections.Concurrent;
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

    public Task<IScheduler> Default(CancellationToken cancellationToken = default) => factory.GetScheduler(cancellationToken);

    public async Task Add(IScheduler scheduler, CancellationToken cancellationToken)
    {
        await Attach(scheduler, cancellationToken);
        if (scheduler.SchedulerName != (await Default(cancellationToken)).SchedulerName)
            extra[scheduler.SchedulerName] = scheduler;
    }

    public async Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        foreach (var attachment in attachments)
            await attachment.Attach(scheduler, cancellationToken);
    }

    /// <summary>Every scheduler served, the host's own first, then by name. Ones that have been shut down are left out.</summary>
    public async Task<IReadOnlyList<IScheduler>> All(CancellationToken cancellationToken = default)
    {
        var all = new List<IScheduler> { await Default(cancellationToken) };
        all.AddRange(extra.Values.Where(s => !s.IsShutdown).OrderBy(s => s.SchedulerName, StringComparer.Ordinal));
        return all;
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
        services.GetRequiredService<CronlightSchedulers>().Add(scheduler, cancellationToken);
}

/// <summary>Attaches Cronlight to the host's own scheduler before Quartz starts running jobs.</summary>
internal sealed class SchedulerRegistration(CronlightSchedulers schedulers) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await schedulers.Attach(await schedulers.Default(cancellationToken), cancellationToken);

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
