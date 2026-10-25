using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Cronlight;

namespace Cronlight.SampleHost;

/// <summary>
/// A second scheduler, built in code as some apps do (one per team, or per database), kept in memory. The dashboard
/// shows it in its scheduler picker once it is added with UseCronlight.
/// </summary>
public class ReportsScheduler(IServiceProvider services, IJobFactory jobFactory) : IHostedService
{
    public const string Name = "Reports";
    private IScheduler? scheduler;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = Name,
            ["quartz.threadPool.maxConcurrency"] = "2",
        }).GetScheduler(cancellationToken);
        // The host's DI job factory, so jobs with constructor dependencies run here too
        scheduler.JobFactory = jobFactory;
        // Before Start, so history, alerts, retries and secret references cover its first runs
        await scheduler.UseCronlight(services, cancellationToken);
        await scheduler.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (scheduler != null)
            await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
    }
}
