using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight.History;

internal sealed class JobHistoryAttachment(JobHistoryListener listener) : ISchedulerAttachment
{
    public Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        scheduler.AddJobListenerOnce(listener);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes queued records to the store and applies retention to every scheduler served. Registered ahead of
/// Quartz's hosted service, so it stops only after the scheduler has finished its last job.
/// </summary>
internal sealed class JobHistoryWriter(
    CronlightSchedulers schedulers,
    IJobHistoryStore store,
    JobHistoryQueue queue,
    IOptions<JobHistoryOptions> options,
    ILogger<JobHistoryWriter> logger) : IHostedService
{
    private readonly CancellationTokenSource stopping = new();
    private Task? writing;
    private Task? cleaning;
    private volatile bool initialized;
    private readonly TaskCompletionSource storeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        writing = Task.Run(() => Write(stopping.Token), CancellationToken.None);
        cleaning = Task.Run(() => Clean(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The scheduler has already stopped (it is registered after us, so it stops first): drain what is left
        queue.Complete();

        // Storage never came up: there is nothing to drain into, so stop retrying instead of holding up shutdown
        if (!initialized)
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
            if (cleaning != null)
                await cleaning.ContinueWith(_ => { }, TaskScheduler.Default);
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

        await foreach (var (historyEvent, record) in queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                if (historyEvent == HistoryEvent.Started)
                    await store.RecordStarted(record, CancellationToken.None);
                else
                    await store.RecordFinished(record, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // History must never take the app down; the record is lost but jobs keep running
                logger.LogWarning(ex, "Could not write execution history for {Group}.{Job}", record.JobGroup, record.JobName);
            }
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

    private async Task Clean(CancellationToken cancellationToken)
    {
        if (options.Value.Retention is not { } retention)
            return;

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
