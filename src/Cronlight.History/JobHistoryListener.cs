using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight.History;

internal enum HistoryEvent
{
    Started,
    Finished
}

/// <summary>Records waiting to be written, so listeners never block a job thread on the database.</summary>
internal sealed class JobHistoryQueue(IOptions<JobHistoryOptions> options, ILogger<JobHistoryQueue> logger)
{
    private readonly Channel<(HistoryEvent Event, JobExecutionRecord Record)> channel =
        Channel.CreateBounded<(HistoryEvent, JobExecutionRecord)>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private long dropped;
    private long overflowed;
    private long pending;
    private volatile bool completed;

    public ChannelReader<(HistoryEvent Event, JobExecutionRecord Record)> Reader => channel.Reader;

    /// <summary>Records dropped so far because the queue was full, rather than closed.</summary>
    public long Overflowed => Interlocked.Read(ref overflowed);

    /// <summary>Records queued and not yet written. The writer calls <see cref="Written"/> for each.</summary>
    public long Pending => Interlocked.Read(ref pending);

    public void Enqueue(HistoryEvent historyEvent, JobExecutionRecord record)
    {
        // Counted first, so the writer cannot finish with it before it counts
        Interlocked.Increment(ref pending);
        if (channel.Writer.TryWrite((historyEvent, record)))
            return;
        Interlocked.Decrement(ref pending);

        // Full (the store is down or too slow) or completed (shutting down): drop rather than slow jobs down
        if (!completed)
            Interlocked.Increment(ref overflowed);
        var total = Interlocked.Increment(ref dropped);
        if (total == 1 || total % 1000 == 0)
            logger.LogWarning("Cronlight history queue is full or closed; {Dropped} records dropped so far", total);
    }

    /// <summary>The writer is done with one record, written or not.</summary>
    public void Written() => Interlocked.Decrement(ref pending);

    public void Complete()
    {
        completed = true;
        channel.Writer.TryComplete();
    }
}

/// <summary>
/// Misfires that Quartz handles by running the trigger at once. The run that follows is recorded with the fire time
/// it makes up for as its scheduled time, so history shows how late it really started. Only this node's misfires are
/// known: in a cluster, the run may start on a node that did not handle the misfire, and is then not linked.
/// </summary>
internal sealed class MisfireLinks
{
    private const string ContextKey = "Cronlight.MissedFireTime";
    private readonly ConcurrentDictionary<(string, TriggerKey), DateTimeOffset> missed = new();

    // The first missed time is kept: a catch-up run that misfires again makes up for the same one
    public void Missed(Misfire misfire) => missed.TryAdd((misfire.SchedulerName, misfire.Trigger), misfire.MissedFireTimeUtc);

    /// <summary>The fire time this run makes up for, if it is a catch-up run; asked again, the same answer.</summary>
    public DateTimeOffset? MakesUpFor(IJobExecutionContext context)
    {
        if (context.Get(ContextKey) is DateTimeOffset known)
            return known;
        if (!missed.TryRemove((context.Scheduler.SchedulerName, context.Trigger.Key), out var time))
            return null;
        context.Put(ContextKey, time);
        return time;
    }
}

internal sealed class JobHistoryListener(
    JobHistoryQueue queue,
    MisfireLinks links,
    IOptions<JobHistoryOptions> options,
    IServiceProvider services) : IJobListener, IJobNotCreatedHandler
{
    // Registered by AddCronlight; without it, nothing is retried and no job type says which values are secret
    private readonly RetryPlanner? retries = services.GetService<RetryPlanner>();
    private readonly IOptions<CronlightOptions> cronlight = services.GetRequiredService<IOptions<CronlightOptions>>();

    public string Name => "Cronlight.History";

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (Refires.Begin(context, Name))
            queue.Enqueue(HistoryEvent.Finished, RefiredAttempt(context));
        queue.Enqueue(HistoryEvent.Started, Record(context, JobExecutionStatus.Running));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The attempt before this one, which failed and asked to run again at once, recorded as Quartz before 3.17 reports
    /// it. The context still holds its run time and log; what it threw is known when Cronlight wrapped the job.
    /// </summary>
    private JobExecutionRecord RefiredAttempt(IJobExecutionContext context) => Record(context, JobExecutionStatus.Failed) with
    {
        RefireCount = context.RefireCount - 1,
        FinishedTimeUtc = context.FireTimeUtc + context.JobRunTime,
        DurationMs = (long)context.JobRunTime.TotalMilliseconds,
        Error = Truncate(RunOutcome.Error(context, context.Get(RunLogJob.RefireErrorKey) as JobExecutionException, cronlight.Value)
            ?? "Failed and asked to run again at once"),
        Log = (context.Get(RunLog.ContextKey) as RunLog)?.Close()
    };

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        queue.Enqueue(HistoryEvent.Finished, Record(context, JobExecutionStatus.Vetoed) with
        {
            FinishedTimeUtc = context.FireTimeUtc,
            DurationMs = 0,
            Error = context.Get(CronlightContextKeys.VetoReason) is string reason ? Truncate(reason) : null
        });
        return Task.CompletedTask;
    }

    public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        Refires.End(context, Name);
        var status = RunOutcome.Status(context, jobException);
        if (retries != null && await retries.Retry(context, jobException, cancellationToken))
            status = JobExecutionStatus.Retried;
        queue.Enqueue(HistoryEvent.Finished, Record(context, status) with
        {
            FinishedTimeUtc = context.FireTimeUtc + context.JobRunTime,
            DurationMs = (long)context.JobRunTime.TotalMilliseconds,
            Error = RunOutcome.Error(context, jobException, cronlight.Value) is { } error ? Truncate(error) : null,
            Log = (context.Get(RunLog.ContextKey) as RunLog)?.Close()
        });
    }

    /// <summary>A job Quartz could not create never started: recorded as a failed run, so history says why the job stopped.</summary>
    public void JobNotCreated(JobNotCreated failure) => queue.Enqueue(HistoryEvent.Finished, new JobExecutionRecord
    {
        SchedulerName = failure.SchedulerName,
        InstanceName = failure.InstanceName,
        FireInstanceId = failure.Id,
        JobName = failure.Job.Name,
        JobGroup = failure.Job.Group,
        JobType = failure.JobType,
        FiredTimeUtc = failure.TimeUtc,
        FinishedTimeUtc = failure.TimeUtc,
        DurationMs = 0,
        Status = JobExecutionStatus.Failed,
        Error = Truncate(failure.Error)
    });

    /// <summary>
    /// The run's trigger data, masked as the API masks job data, so Run again can repeat the run without secrets
    /// being stored in history. Its merged data is not used: it holds the job's saved data and resolved secrets.
    /// </summary>
    private Dictionary<string, string>? RunData(IJobExecutionContext context)
    {
        var data = context.Trigger.JobDataMap.Where(kv => !Retries.IsReserved(kv.Key)).ToList();
        if (data.Count == 0)
            return null;
        var secretKeys = DashboardApi.SecretKeys(context.JobDetail.JobType);
        return data.ToDictionary(kv => kv.Key, kv => DashboardApi.Display(kv.Key, kv.Value?.ToString(), secretKeys, cronlight.Value) ?? "");
    }

    private JobExecutionRecord Record(IJobExecutionContext context, JobExecutionStatus status) => new()
    {
        SchedulerName = context.Scheduler.SchedulerName,
        InstanceName = context.Scheduler.SchedulerInstanceId,
        FireInstanceId = context.FireInstanceId,
        JobName = context.JobDetail.Key.Name,
        JobGroup = context.JobDetail.Key.Group,
        JobType = context.JobDetail.JobType.FullName,
        TriggerName = context.Trigger.Key.Name,
        TriggerGroup = context.Trigger.Key.Group,
        ScheduledFireTimeUtc = links.MakesUpFor(context) ?? context.ScheduledFireTimeUtc,
        FiredTimeUtc = context.FireTimeUtc,
        Status = status,
        RefireCount = context.RefireCount,
        Attempt = Retries.Attempt(context),
        Data = RunData(context)
    };

    private string Truncate(string text)
    {
        var max = options.Value.MaxErrorLength;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
