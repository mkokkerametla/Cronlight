using Quartz;

namespace Cronlight.Tests;

[CronlightJob("Invoice job")]
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

[CronlightJob("Export")]
[JobParameter("format", Required = true, Choices = new[] { "csv", "json" })]
public class ExportJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
