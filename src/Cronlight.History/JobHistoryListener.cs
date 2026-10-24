using System.Threading.Channels;
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

    public ChannelReader<(HistoryEvent Event, JobExecutionRecord Record)> Reader => channel.Reader;

    public void Enqueue(HistoryEvent historyEvent, JobExecutionRecord record)
    {
        if (channel.Writer.TryWrite((historyEvent, record)))
            return;

        // Full (the store is down or too slow) or completed (shutting down): drop rather than slow jobs down
        var total = Interlocked.Increment(ref dropped);
        if (total == 1 || total % 1000 == 0)
            logger.LogWarning("Cronlight history queue is full or closed; {Dropped} records dropped so far", total);
    }

    public void Complete() => channel.Writer.TryComplete();
}

internal sealed class JobHistoryListener(JobHistoryQueue queue, IOptions<JobHistoryOptions> options) : IJobListener
{
    public string Name => "Cronlight.History";

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        queue.Enqueue(HistoryEvent.Started, Record(context, JobExecutionStatus.Running));
        return Task.CompletedTask;
    }

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        queue.Enqueue(HistoryEvent.Finished, Record(context, JobExecutionStatus.Vetoed) with
        {
            FinishedTimeUtc = context.FireTimeUtc,
            DurationMs = 0
        });
        return Task.CompletedTask;
    }

    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        queue.Enqueue(HistoryEvent.Finished, Record(context, jobException == null ? JobExecutionStatus.Succeeded : JobExecutionStatus.Failed) with
        {
            FinishedTimeUtc = context.FireTimeUtc + context.JobRunTime,
            DurationMs = (long)context.JobRunTime.TotalMilliseconds,
            Error = jobException == null ? null : Describe(jobException)
        });
        return Task.CompletedTask;
    }

    private static JobExecutionRecord Record(IJobExecutionContext context, JobExecutionStatus status) => new()
    {
        SchedulerName = context.Scheduler.SchedulerName,
        InstanceName = context.Scheduler.SchedulerInstanceId,
        FireInstanceId = context.FireInstanceId,
        JobName = context.JobDetail.Key.Name,
        JobGroup = context.JobDetail.Key.Group,
        JobType = context.JobDetail.JobType.FullName,
        TriggerName = context.Trigger.Key.Name,
        TriggerGroup = context.Trigger.Key.Group,
        ScheduledFireTimeUtc = context.ScheduledFireTimeUtc,
        FiredTimeUtc = context.FireTimeUtc,
        Status = status,
        RefireCount = context.RefireCount
    };

    // Quartz wraps what the job threw as JobExecutionException -> SchedulerException("Job threw an unhandled
    // exception") -> the real exception, so unwrap Quartz's layers. A JobExecutionException the job threw
    // itself has no inner exception and keeps its own message.
    // Only type and message are kept: stack traces are long and more likely to carry sensitive details.
    private string Describe(JobExecutionException exception)
    {
        Exception cause = exception;
        while (cause is SchedulerException { InnerException: { } inner })
            cause = inner;
        var text = $"{cause.GetType().Name}: {cause.Message}";
        var max = options.Value.MaxErrorLength;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
