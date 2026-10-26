using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Spi;

namespace Cronlight.History;

internal sealed class JobHistoryAttachment(
    JobHistoryListener listener,
    JobHistoryQueue queue,
    MisfireLinks links,
    ISchedulerFactory factory,
    IServiceProvider services,
    IOptions<JobHistoryOptions> options,
    IOptions<QuartzOptions> quartz,
    ILogger<JobHistoryAttachment> logger) : ISchedulerAttachment
{
    private const string JobFactoryProperty = "quartz.scheduler.jobFactory.type";

    // By instance rather than name: a scheduler shut down and created again under its name gets wrapped too
    private readonly ConditionalWeakTable<IScheduler, object> wrapped = new();

    public async Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        scheduler.AddJobListenerOnce(listener);
        scheduler.AddTriggerListenerOnce(new MisfireListener("Cronlight.History.Misfires", scheduler, Record, logger));

        // Quartz cannot say which factory a scheduler built in code uses, so the host wraps that one itself
        // (WithCronlightRunLogs). The host's own scheduler uses the factory its Quartz options name, which AddQuartz
        // registers in DI: AddQuartz's own, or the host's from UseJobFactory.
        if (!options.Value.CaptureLogs || services.GetService<IJobFactory>() is not { } jobFactory
            || scheduler.SchedulerName != (await factory.GetScheduler(cancellationToken)).SchedulerName
            || !wrapped.TryAdd(scheduler, jobFactory))
            return;
        if (quartz.Value.GetValueOrDefault(JobFactoryProperty) is { } configured && !Names(configured, jobFactory.GetType()))
        {
            logger.LogWarning("Run logs are not kept for scheduler {Scheduler}: its job factory ({Configured}) is not the one " +
                "registered for IJobFactory ({Registered})", scheduler.SchedulerName, configured, jobFactory.GetType().FullName);
            return;
        }
        scheduler.JobFactory = jobFactory.WithCronlightRunLogs(services);
    }

    // Quartz properties name types as "Namespace.Type, Assembly"
    private static bool Names(string configured, Type type) =>
        configured == type.FullName || configured.StartsWith(type.FullName + ",", StringComparison.Ordinal);

    // A misfire is not a run, so it is written finished, under its own id: a misfire reported again after a
    // rollback replaces its record rather than adding another
    private void Record(Misfire misfire)
    {
        if (misfire.RunsNow)
            links.Missed(misfire);
        queue.Enqueue(HistoryEvent.Finished, new JobExecutionRecord
        {
            SchedulerName = misfire.SchedulerName,
            InstanceName = misfire.InstanceName,
            FireInstanceId = misfire.Id,
            JobName = misfire.Job.Name,
            JobGroup = misfire.Job.Group,
            TriggerName = misfire.Trigger.Name,
            TriggerGroup = misfire.Trigger.Group,
            ScheduledFireTimeUtc = misfire.MissedFireTimeUtc,
            FiredTimeUtc = misfire.DetectedUtc,
            FinishedTimeUtc = misfire.DetectedUtc,
            Status = JobExecutionStatus.Misfired,
            Error = misfire.Description
        });
    }
}

/// <summary>A built-in store, which can record a run as finished only while it is still recorded as Running.</summary>
internal interface IUnfinishedRuns
{
    /// <summary>Records the run as finished in one step, unless it is no longer Running (its end was written meanwhile).</summary>
    Task<bool> FinishIfRunning(JobExecutionRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Writes queued records to the store, applies retention to every scheduler served, and closes runs left Running by a
/// node that stopped before they finished. Registered ahead of Quartz's hosted service, so it stops only after the
/// scheduler has finished its last job.
/// </summary>
internal sealed class JobHistoryWriter(
    CronlightSchedulers schedulers,
    IJobHistoryStore store,
    JobHistoryQueue queue,
    RunningJobsReader runningJobs,
    IOptions<JobHistoryOptions> options,
    ILogger<JobHistoryWriter> logger) : IHostedService
{
    /// <summary>A run that started this recently is left alone, however it looks: its end may still be on its way.</summary>
    private static readonly TimeSpan UnfinishedRunGrace = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource stopping = new();
    private Task? writing;
    private Task? cleaning;
    private Task? closing;
    private volatile bool initialized;
    // The store failed a write and could not be read either: records wait until it is back
    private volatile bool unavailable;
    private readonly TaskCompletionSource storeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        writing = Task.Run(() => Write(stopping.Token), CancellationToken.None);
        cleaning = Task.Run(() => Clean(stopping.Token), CancellationToken.None);
        closing = Task.Run(() => CloseUnfinished(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The scheduler has already stopped (it is registered after us, so it stops first), but jobs it did not wait for may
        // still be running. The queue stays open until what is in it is written, so a run ending meanwhile is recorded.
        if (initialized && !unavailable)
            await WhenWritten(cancellationToken);
        queue.Complete();

        // Storage never came up, or is down now: there is nothing to drain into, so stop retrying instead of holding up shutdown
        if (!initialized || unavailable)
            await stopping.CancelAsync();

        try
        {
            if (writing != null)
                await writing.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stopped before all execution history was written");
        }
        finally
        {
            await stopping.CancelAsync();
            foreach (var task in new[] { cleaning, closing }.OfType<Task>())
                await task.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task WhenWritten(CancellationToken cancellationToken)
    {
        try
        {
            while (queue.Pending > 0 && !unavailable)
                await Task.Delay(20, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host gives up; what is left is written while it waits for the writer below
        }
    }

    private async Task Write(CancellationToken cancellationToken)
    {
        await InitializeStore(cancellationToken);
        if (!initialized)
        {
            var lost = 0;
            while (queue.Reader.TryRead(out _))
                lost++;
            if (lost > 0)
                logger.LogWarning("Execution history storage was never available; {Count} records were not written", lost);
            return;
        }

        // Runs recorded as started and not yet as finished. Those still going when the app stops would otherwise stay Running:
        // their end, if it comes, comes after the queue has closed.
        var open = new Dictionary<(string, string, int), JobExecutionRecord>();
        await foreach (var (historyEvent, record) in queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (historyEvent == HistoryEvent.Started)
                open[Run(record)] = record;
            else
                open.Remove(Run(record));
            var written = await Write(historyEvent, record, cancellationToken);
            queue.Written();
            if (!written)
            {
                var lost = 1;
                while (queue.Reader.TryRead(out _))
                    lost++;
                logger.LogWarning("Stopped while the execution history store was unavailable; {Count} records were not written", lost);
                return;
            }
        }

        // A run's end dropped from a full queue looks the same
        var error = RunOutcome.DidNotFinish + (queue.Overflowed == 0
            ? "the app stopped while it was running."
            : "no end was recorded. It was still running when the app stopped, or its end was lost while the history queue was full.");
        foreach (var run in open.Values)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var closed = run with
                {
                    Status = JobExecutionStatus.Failed,
                    FinishedTimeUtc = now,
                    DurationMs = (long)(now - run.FiredTimeUtc).TotalMilliseconds,
                    Error = error
                };
                // Only while still Running: its end may have been written another way, or retention removed it since
                if (store is IUnfinishedRuns unfinishedRuns)
                    await unfinishedRuns.FinishIfRunning(closed, CancellationToken.None);
                else
                    await store.RecordFinished(closed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not record that {Group}.{Job} did not finish before the app stopped", run.JobGroup, run.JobName);
            }
        }
    }

    // An attempt of a run: a job refired at once runs again under the same fire instance id
    private static (string, string, int) Run(JobExecutionRecord record) => (record.SchedulerName, record.FireInstanceId, record.RefireCount);

    /// <summary>
    /// Writes one record, waiting while the store is unavailable (its database is down, say), so an outage loses only
    /// the records beyond QueueCapacity. A record the store refuses while it can still be read is dropped after a second
    /// try: it would never be written, and would hold up the rest. False when the app stops while the store is down.
    /// </summary>
    private async Task<bool> Write(HistoryEvent historyEvent, JobExecutionRecord record, CancellationToken cancellationToken)
    {
        // Refusals while the store could be read, since it last could not; waits while it could not
        for (int refused = 0, waits = 0; ;)
        {
            try
            {
                // The misfire listener cannot read the job store, so a misfired job's type is looked up here
                if (record is { Status: JobExecutionStatus.Misfired, JobType: null })
                    record = record with { JobType = await schedulers.JobType(record.SchedulerName, new JobKey(record.JobName, record.JobGroup)) };
                if (historyEvent == HistoryEvent.Started)
                    await store.RecordStarted(record, CancellationToken.None);
                else
                    await store.RecordFinished(record, CancellationToken.None);
                if (unavailable)
                {
                    unavailable = false;
                    logger.LogInformation("The execution history store is available again; writing the records that waited");
                }
                return true;
            }
            catch (Exception ex)
            {
                if (await Readable(record.SchedulerName))
                {
                    // A second try covers a deadlock or a timeout
                    if (++refused < 2)
                        continue;
                    // History must never take the app down; the record is lost but jobs keep running
                    logger.LogWarning(ex, "Could not write execution history for {Group}.{Job}", record.JobGroup, record.JobName);
                    return true;
                }
                if (!unavailable)
                {
                    unavailable = true;
                    logger.LogWarning(ex, "The execution history store is unavailable; records wait, up to QueueCapacity, until it is back");
                }
            }

            refused = 0;
            try
            {
                // 1, 2, 4, 8 and 16 seconds, then every 30
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, waits++))), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Whether the store answers at all. A summary of no runs (since the end of time) reads no record back, so a record it
    /// cannot read, such as one a newer version wrote, does not make it look unavailable.
    /// </summary>
    private async Task<bool> Readable(string schedulerName)
    {
        try
        {
            await store.Summarize(schedulerName, DateTimeOffset.MaxValue, CancellationToken.None);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task InitializeStore(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.Initialize(cancellationToken);
                initialized = true;
                storeReady.TrySetResult();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep queueing meanwhile; records beyond QueueCapacity are dropped
                var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * attempt));
                logger.LogError(ex, "Could not initialize execution history storage; retrying in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Every CleanupInterval: tries again a schema upgrade that failed at startup (a lock timeout, say), and applies
    /// retention.
    /// </summary>
    private async Task Clean(CancellationToken cancellationToken)
    {
        // On a new database the table only exists once initialization has created it
        try
        {
            await storeReady.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        do
        {
            try
            {
                if (store is AdoJobHistoryStore { UpgradePending: true } upgrading)
                    await upgrading.Initialize(cancellationToken);
                if (options.Value.Retention is not { } retention)
                    continue;
                foreach (var scheduler in await schedulers.All(cancellationToken))
                {
                    var removed = await store.DeleteFiredBefore(scheduler.SchedulerName, DateTimeOffset.UtcNow - retention, cancellationToken);
                    if (removed > 0)
                        logger.LogInformation("Removed {Count} execution history records of {Scheduler} older than {Retention}",
                            removed, scheduler.SchedulerName, retention);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Execution history cleanup failed");
            }
        }
        while (await WaitForNextTick(timer, cancellationToken));
    }

    /// <summary>
    /// Records runs that are still Running in history, but that no node is running any more, as failed: the node running
    /// them crashed, or stopped without recording their end. Quartz's fired triggers say which runs are going on every
    /// node, so this is done only for schedulers whose database Cronlight reads, and with the built-in stores, which
    /// close a run only while it is still Running.
    /// </summary>
    private async Task CloseUnfinished(CancellationToken cancellationToken)
    {
        if (options.Value.UnfinishedRunCheckInterval <= TimeSpan.Zero || store is not IUnfinishedRuns unfinishedRuns)
            return;
        try
        {
            await storeReady.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.UnfinishedRunCheckInterval);
        do
        {
            try
            {
                foreach (var scheduler in await schedulers.All(cancellationToken))
                    await CloseUnfinished(unfinishedRuns, scheduler, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Looking for runs left unfinished in execution history failed");
            }
        }
        while (await WaitForNextTick(timer, cancellationToken));
    }

    private async Task CloseUnfinished(IUnfinishedRuns unfinishedRuns, IScheduler scheduler, CancellationToken cancellationToken)
    {
        var startedBefore = DateTimeOffset.UtcNow - UnfinishedRunGrace;
        var unfinished = new List<JobExecutionRecord>();
        const int pageSize = 500;
        for (long? before = null; unfinished.Count < 10 * pageSize;)
        {
            var page = await store.Query(scheduler.SchedulerName,
                new JobHistoryQuery { Status = JobExecutionStatus.Running, BeforeId = before, Limit = pageSize }, cancellationToken);
            unfinished.AddRange(page.Where(r => r is { Status: JobExecutionStatus.Running } && r.FiredTimeUtc < startedBefore));
            if (page.Count < pageSize)
                break;
            before = page[^1].Id;
        }
        if (unfinished.Count == 0)
            return;

        // Read after the history, so a run still going then is listed here
        var running = await runningJobs.Read(scheduler, cancellationToken);
        if (running.Source != RunningJobsDto.Cluster)
            return;
        var executing = running.Jobs.Select(j => j.FireInstanceId).ToHashSet();
        var closed = 0;
        foreach (var run in unfinished.Where(r => !executing.Contains(r.FireInstanceId)))
        {
            // Its end may have been written since it was read, and wins; should it arrive later, it replaces this
            if (await unfinishedRuns.FinishIfRunning(run with
                {
                    Status = JobExecutionStatus.Failed,
                    Error = RunOutcome.DidNotFinish +
                        $"no end was recorded, and no node is running it any more. The node that ran it ({run.InstanceName}) probably stopped or crashed."
                }, cancellationToken))
                closed++;
        }
        if (closed > 0)
            logger.LogWarning("Recorded {Count} runs of {Scheduler} as failed: they were still Running in history, but no node is running them",
                closed, scheduler.SchedulerName);
    }

    private static async Task<bool> WaitForNextTick(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
