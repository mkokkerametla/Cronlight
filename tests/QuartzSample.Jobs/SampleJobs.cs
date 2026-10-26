using Cronlight;
using Quartz;

namespace QuartzSample.Jobs;

[CronlightJob("Partner call")]
[JobParameter("authorization", Type = JobParameterType.Secret)]
public class PartnerCallJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
