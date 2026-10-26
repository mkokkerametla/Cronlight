using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight;

/// <summary>
/// Cronlight's metrics, for OpenTelemetry or any other <see cref="System.Diagnostics.Metrics"/> listener:
/// <c>services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(CronlightMetrics.MeterName))</c>. Every instrument is
/// tagged with <c>cronlight.scheduler</c>, <c>cronlight.job.group</c> and <c>cronlight.job.name</c>; runs also with
/// <c>cronlight.run.outcome</c> (succeeded, failed, retried, cancelled or vetoed).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>cronlight.job.runs</c>: finished runs, vetoed ones included.</item>
/// <item><c>cronlight.job.run.duration</c>: how long runs took, in seconds. Vetoed runs, which never started, are left out.</item>
/// <item><c>cronlight.job.runs.active</c>: runs in progress on this node.</item>
/// <item><c>cronlight.job.misfires</c>: fire times missed by more than the misfire threshold.</item>
/// </list>
/// Each node reports its own runs, so add up across nodes for a cluster.
/// </remarks>
public static class CronlightMetrics
{
    public const string MeterName = "Cronlight";
}

/// <summary>Records every run and misfire of the schedulers it is attached to on the Cronlight meter.</summary>
internal sealed class MetricsListener : IJobListener, IJobNotCreatedHandler
{
    private readonly RetryPlanner retries;
    private readonly Counter<long> runs;
    private readonly Histogram<double> duration;
    private readonly UpDownCounter<long> active;
    private readonly Counter<long> misfires;

    public MetricsListener(IMeterFactory meters, RetryPlanner retries)
    {
        this.retries = retries;
        var meter = meters.Create(CronlightMetrics.MeterName);
        runs = meter.CreateCounter<long>("cronlight.job.runs", "{run}", "Finished runs, by outcome");
        duration = meter.CreateHistogram<double>("cronlight.job.run.duration", "s", "How long runs took");
        active = meter.CreateUpDownCounter<long>("cronlight.job.runs.active", "{run}", "Runs in progress on this node");
        misfires = meter.CreateCounter<long>("cronlight.job.misfires", "{misfire}", "Fire times missed by more than the misfire threshold");
    }

    public string Name => "Cronlight.Metrics";

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var job = context.JobDetail.Key;
        if (Refires.Begin(context, Name))
        {
            // The attempt before failed and asked to run again: counted as Quartz before 3.17 reports it, and this
            // attempt takes its place among the active runs
            var tags = Tags(context.Scheduler.SchedulerName, job, "failed");
            runs.Add(1, tags);
            duration.Record(context.JobRunTime.TotalSeconds, tags);
        }
        else
        {
            active.Add(1, Tags(context.Scheduler.SchedulerName, job));
        }
        return Task.CompletedTask;
    }

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        runs.Add(1, Tags(context.Scheduler.SchedulerName, context.JobDetail.Key, "vetoed"));
        return Task.CompletedTask;
    }

    public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        Refires.End(context, Name);
        var job = context.JobDetail.Key;
        active.Add(-1, Tags(context.Scheduler.SchedulerName, job));
        // Asks the planner, as history and alerts do, so a failure that is retried counts as retried in all three
        var status = await retries.Retry(context, jobException, cancellationToken) ? JobExecutionStatus.Retried : RunOutcome.Status(context, jobException);
        var tags = Tags(context.Scheduler.SchedulerName, job, status.ToString().ToLowerInvariant());
        runs.Add(1, tags);
        duration.Record(context.JobRunTime.TotalSeconds, tags);
    }

    // Never started, so never active, and without a duration
    public void JobNotCreated(JobNotCreated failure) => runs.Add(1, Tags(failure.SchedulerName, failure.Job, "failed"));

    // A database store whose misfire handling is rolled back reports the misfire again on its next pass, so a counter
    // can count one twice; history keeps one record per misfire
    public void Misfired(Misfire misfire) => misfires.Add(1, Tags(misfire.SchedulerName, misfire.Job));

    private static TagList Tags(string scheduler, JobKey job, string? outcome = null)
    {
        var tags = new TagList
        {
            { "cronlight.scheduler", scheduler },
            { "cronlight.job.group", job.Group },
            { "cronlight.job.name", job.Name }
        };
        if (outcome != null)
            tags.Add("cronlight.run.outcome", outcome);
        return tags;
    }
}

internal sealed class MetricsAttachment(MetricsListener listener, ILogger<MetricsAttachment> logger) : ISchedulerAttachment
{
    public Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        scheduler.AddJobListenerOnce(listener);
        scheduler.AddTriggerListenerOnce(new MisfireListener("Cronlight.Metrics.Misfires", scheduler, listener.Misfired, logger));
        return Task.CompletedTask;
    }
}
