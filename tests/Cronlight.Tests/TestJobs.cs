using Quartz;

namespace Cronlight.Tests;

[CronlightJob("Invoice job", Description = "Sends the monthly invoices")]
[JobParameter("customerId", Required = true, Type = JobParameterType.Int)]
[JobParameter("smtpPassword", Type = JobParameterType.Secret)]
public class InvoiceJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

public class PlainJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

/// <summary>Blocks until released, so tests can observe it as running. Keyed by the "gate" job data value.</summary>
public class BlockingJob : IJob
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (SemaphoreSlim Started, SemaphoreSlim Release)> Gates = new();

    public static (SemaphoreSlim Started, SemaphoreSlim Release) Gate(string key) =>
        Gates.GetOrAdd(key, _ => (new SemaphoreSlim(0), new SemaphoreSlim(0)));

    public async Task Execute(IJobExecutionContext context)
    {
        var (started, release) = Gate(context.MergedJobDataMap.GetString("gate")!);
        started.Release();
        await release.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

public class FailingJob : IJob
{
    public Task Execute(IJobExecutionContext context) =>
        throw new InvalidOperationException("Boom: invoice 42 could not be sent");
}

/// <summary>Fails its first attempt asking Quartz to run it again at once, then succeeds.</summary>
public class RefireOnceJob : IJob
{
    public Task Execute(IJobExecutionContext context) =>
        context.RefireCount == 0
            ? throw new JobExecutionException("first attempt failed") { RefireImmediately = true }
            : Task.CompletedTask;
}

[CronlightJob("Report")]
[JobParameter("format", DefaultValue = "pdf")]
[JobParameter("pages", Type = JobParameterType.Int, DefaultValue = "10")]
public class ReportJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

/// <summary>Vetoes every job in the "vetoed" group, the way a host's maintenance-window listener might.</summary>
public class VetoGroupListener : ITriggerListener
{
    public const string Group = "vetoed";

    public string Name => "VetoGroup";

    public Task<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(context.JobDetail.Key.Group == Group);

    public Task TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction triggerInstructionCode,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

[CronlightJob("Export")]
[JobParameter("format", Required = true, Choices = new[] { "csv", "json" })]
public class ExportJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

/// <summary>Records the smtpPassword and endpoint values each run sees, keyed by the "run" job data value.</summary>
[CronlightJob("Secret echo")]
[JobParameter("smtpPassword", Type = JobParameterType.Secret)]
[JobParameter("endpoint")]
public class SecretEchoJob : IJob
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Password, string? Endpoint)> Seen = new();

    public Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        string? Get(string key) => data.TryGetValue(key, out var value) ? value?.ToString() : null;
        Seen[data.GetString("run")!] = (Get("smtpPassword"), Get("endpoint"));
        return Task.CompletedTask;
    }
}

/// <summary>Waits for BlockingJob.Gate("gate") to be released, ignoring cancellation, then fails.</summary>
public class GatedFailingJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var (started, release) = BlockingJob.Gate(context.MergedJobDataMap.GetString("gate")!);
        started.Release();
        await release.WaitAsync(TimeSpan.FromSeconds(30));
        throw new InvalidOperationException("Failed after waiting");
    }
}

/// <summary>Runs until cancelled. Signals BlockingJob.Gate("gate").Started once running.</summary>
public class CancellableJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        BlockingJob.Gate(context.MergedJobDataMap.GetString("gate")!).Started.Release();
        await Task.Delay(Timeout.Infinite, context.CancellationToken);
    }
}

/// <summary>Fails while <see cref="Failing"/> holds its "key" job data value, and succeeds otherwise.</summary>
public class FlipJob : IJob
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Failing = new();

    public Task Execute(IJobExecutionContext context)
    {
        var key = context.MergedJobDataMap.GetString("key")!;
        return Failing.GetValueOrDefault(key) ? throw new InvalidOperationException($"{key} is broken") : Task.CompletedTask;
    }
}

/// <summary>
/// Retried twice, a second apart. Fails while <see cref="FailuresLeft"/> for its "key" is above zero, and records
/// the "region" each attempt saw.
/// </summary>
[CronlightRetry(2, DelaySeconds = 1)]
public class RetriedJob : IJob
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FailuresLeft = new();
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<string?>> Seen = new();

    public Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        var key = data.GetString("key")!;
        Seen.GetOrAdd(key, _ => new()).Enqueue(data.TryGetValue("region", out var region) ? region?.ToString() : null);
        var left = FailuresLeft.AddOrUpdate(key, -1, (_, n) => n - 1);
        return left >= 0 ? throw new InvalidOperationException($"{key} failed, {left} failures to go") : Task.CompletedTask;
    }
}

/// <summary>Retried only on timeouts: throws a TimeoutException when its "error" is "timeout", else an InvalidOperationException.</summary>
[CronlightRetry(2, DelaySeconds = 1, RetryOn = [typeof(TimeoutException)])]
public class PickyRetryJob : IJob
{
    public Task Execute(IJobExecutionContext context) =>
        context.MergedJobDataMap.GetString("error") == "timeout"
            ? throw new TimeoutException("Upstream timed out")
            : throw new InvalidOperationException("Bad input");
}

/// <summary>Deletes itself, then fails, so its retry cannot be scheduled: the job it would run is gone.</summary>
[CronlightRetry(1, DelaySeconds = 1)]
public class SelfDeletingJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        await context.Scheduler.DeleteJob(context.JobDetail.Key);
        throw new InvalidOperationException("Failed after deleting itself");
    }
}

/// <summary>
/// Quartz cannot create it while its scheduler is in <see cref="Broken"/>: its constructor throws, as when a
/// dependency is not registered, or times out (<see cref="TimesOut"/>), as when a dependency calls a slow service.
/// While its scheduler is in <see cref="Failing"/>, it is created but its run fails.
/// </summary>
public class BreakableJob : IJob
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Broken = new();
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> TimesOut = new();
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Failing = new();

    public BreakableJob(Microsoft.Extensions.Options.IOptions<QuartzOptions> quartz)
    {
        var scheduler = quartz.Value["quartz.scheduler.instanceName"] ?? "";
        if (Broken.GetValueOrDefault(scheduler))
            throw new InvalidOperationException("No IInvoiceGateway is registered");
        if (TimesOut.GetValueOrDefault(scheduler))
            throw new TaskCanceledException("The partner API did not answer in time");
    }

    public Task Execute(IJobExecutionContext context) =>
        Failing.GetValueOrDefault(context.Scheduler.SchedulerName)
            ? throw new InvalidOperationException("Invoice 42 could not be sent")
            : Task.CompletedTask;
}

/// <summary>Logs through ILogger while it runs, its apiToken among it, from its own code and from work it starts.</summary>
public class LoggingJob(Microsoft.Extensions.Logging.ILogger<LoggingJob> logger) : IJob
{
    /// <summary>Set once work a run left running (with "linger") has logged, after the run finished.</summary>
    public static readonly ManualResetEventSlim Lingered = new();

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        // GetString throws for a missing key
        string? Get(string key) => data.TryGetValue(key, out var value) ? value?.ToString() : null;
        if (Get("linger") == "true")
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Still going after the run");
                Lingered.Set();
            });
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Calling {Customer} with token {Token}",
            Get("customer"), Get("apiToken"));
        Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(logger, "Below the minimum level, so not kept");
        await Task.Run(() => Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,
            new TimeoutException("upstream slow"), "Retrying a call"));
        var lines = int.TryParse(Get("lines"), out var n) ? n : 0;
        for (var i = 1; i <= lines; i++)
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Line {Line}", i);
    }
}

/// <summary>Fails with an exception whose message quotes its secret, as a parser rejecting a bad value does.</summary>
[JobParameter("authorization", Type = JobParameterType.Secret)]
public class LeakingJob : IJob
{
    public Task Execute(IJobExecutionContext context) =>
        throw new FormatException($"The format of value '{context.MergedJobDataMap.GetString("authorization")}' is invalid.");
}
