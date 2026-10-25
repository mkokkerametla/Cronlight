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
