using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;

namespace Cronlight;

public static class CronlightHealthCheckExtensions
{
    /// <summary>
    /// Checks every scheduler Cronlight serves: unhealthy (or <paramref name="failureStatus"/>) when one has shut down
    /// or its job store cannot be read, degraded while one has not started or is in standby (with
    /// <paramref name="checkCluster"/>, while a clustered one has no running nodes).
    /// <c>builder.Services.AddHealthChecks().AddCronlight();</c> then <c>app.MapHealthChecks("/healthz");</c>
    /// </summary>
    /// <param name="timeout">How long reading a job store may take before it counts as failed; 10 seconds by default.</param>
    /// <param name="checkCluster">
    /// For a clustered scheduler this process has not started, check the cluster's nodes instead: healthy while any
    /// node is alive, degraded when none is. For a host that manages schedulers other processes run and never starts
    /// them. It applies to a clustered scheduler whose connection Cronlight has: the host's own, or one added with its
    /// properties through UseCronlight; others are checked per process as before. A host that has started the scheduler
    /// is still checked by its own state.
    /// </param>
    public static IHealthChecksBuilder AddCronlight(this IHealthChecksBuilder builder, string name = "cronlight",
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null, TimeSpan? timeout = null, bool checkCluster = false)
    {
        builder.Services.AddCronlightSchedulers();
        if (checkCluster)
        {
            builder.Services.TryAddSingleton<QuartzDataSource>();
            builder.Services.TryAddSingleton<ClusterNodesReader>();
        }
        return builder.Add(new HealthCheckRegistration(name,
            services => new SchedulerHealthCheck(services.GetRequiredService<CronlightSchedulers>(),
                checkCluster ? services.GetRequiredService<ClusterNodesReader>() : null),
            failureStatus, tags, timeout ?? TimeSpan.FromSeconds(10)));
    }
}

internal sealed class SchedulerHealthCheck(CronlightSchedulers schedulers, ClusterNodesReader? cluster) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var failed = new List<string>();
        var degraded = new List<string>();
        Exception? error = null;
        try
        {
            // Added schedulers that have shut down are left out. The host's own is always checked: the one attached at
            // startup once it has shut down, since asking Quartz for it again would create a new one
            IReadOnlyList<IScheduler> all = schedulers.AttachedHost is { IsShutdown: true } host
                ? [host, .. schedulers.Added]
                : await schedulers.All(cancellationToken);
            foreach (var scheduler in all)
            {
                var (state, healthy) = await State(scheduler, cancellationToken);
                data[scheduler.SchedulerName] = state;
                if (healthy == false)
                    failed.Add($"{scheduler.SchedulerName} {state}");
                else if (healthy == null)
                    degraded.Add($"{scheduler.SchedulerName} {state}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The host's scheduler could not even be created, e.g. its configuration is wrong
            error = ex;
            failed.Add($"the scheduler could not be created: {ex.Message}");
        }

        return failed.Count > 0 ? new HealthCheckResult(context.Registration.FailureStatus, string.Join("; ", failed), error, data)
            : degraded.Count > 0 ? HealthCheckResult.Degraded(string.Join("; ", degraded), data: data)
            : HealthCheckResult.Healthy(data.Count == 1 ? "The scheduler is running" : $"{data.Count} schedulers are running", data);
    }

    /// <summary>What the scheduler is doing, and whether that is healthy: null for degraded.</summary>
    private async Task<(string State, bool? Healthy)> State(IScheduler scheduler, CancellationToken cancellationToken)
    {
        if (scheduler.IsShutdown)
            return ("has shut down", false);
        try
        {
            // Reads the job store: for a database store, a query on the Quartz tables
            await scheduler.GetTriggerGroupNames(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ($"cannot read its job store ({ex.Message})", false);
        }
        // A process that runs the scheduler is judged by its own state, so a node that failed to start is not hidden
        if (cluster != null && !scheduler.IsStarted && await cluster.Read(scheduler, cancellationToken) is { } nodes)
        {
            var alive = nodes.Count(n => n.Alive);
            return alive > 0 ? ($"is running on {alive} {(alive == 1 ? "node" : "nodes")}", true) : ("has no running nodes", null);
        }
        return !scheduler.IsStarted ? ("has not started", null)
            : scheduler.InStandbyMode ? ("is in standby", null)
            : ("is running", true);
    }
}
