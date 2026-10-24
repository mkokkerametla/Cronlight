using Quartz;
using Cronlight;

namespace Cronlight.SampleHost;

[CronlightJob("Send invoices", Description = "Emails pending invoices for a customer")]
[JobParameter("customerId", Type = JobParameterType.Int, Required = true)]
[JobParameter("smtpPassword", Type = JobParameterType.Secret)]
[JobParameter("batchSize", Type = JobParameterType.Int, DefaultValue = "50")]
public class InvoiceJob(ILogger<InvoiceJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        logger.LogInformation("Invoices for customer {CustomerId}, batch {Batch}", data.GetString("customerId"), data.GetString("batchSize"));
        return Task.CompletedTask;
    }
}

// No dashboard attributes: still discoverable, UI falls back to free-form job data
public class PlainJob(ILogger<PlainJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("PlainJob ran with {Count} data entries", context.MergedJobDataMap.Count);
        return Task.CompletedTask;
    }
}

[CronlightJob("Slow job")]
public class SlowJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.Delay(TimeSpan.FromSeconds(15), context.CancellationToken);
}

[CronlightJob("Flaky job", Description = "Fails about half the time, to demo failure history")]
[JobParameter("failureRate", Type = JobParameterType.Int, DefaultValue = "50", Description = "Percent of runs that fail")]
public class FlakyJob : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var rate = int.TryParse(context.MergedJobDataMap.GetString("failureRate"), out var r) ? r : 50;
        if (Random.Shared.Next(100) < rate)
            throw new HttpRequestException("Upstream API returned 503 Service Unavailable");
        return Task.Delay(Random.Shared.Next(200, 1500), context.CancellationToken);
    }
}
