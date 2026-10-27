using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Spi;

namespace Cronlight.Tests;

/// <summary>
/// A job Quartz cannot create (its constructor or job factory throws) has every trigger set to Error, and never runs
/// again until someone resumes it. The job list, history and alerts must all show it.
/// </summary>
public class JobErrorTests
{
    /// <summary>A job Quartz cannot create, with a trigger that fires every second, so it tries to run at once.</summary>
    internal static async Task CreateBroken(TestHost host, string name, string group = "DEFAULT")
    {
        BreakableJob.Broken[host.SchedulerName] = true;
        var response = await host.Client().PostJson("api/jobs", new
        {
            type = typeof(BreakableJob).FullName,
            name,
            group,
            triggers = new[] { new { kind = "Interval", intervalSeconds = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> WaitForSearch(HttpClient client, string query, Func<JsonElement, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var response = await client.GetAsync($"api/jobs/search{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (done(page))
                return page;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The job list never matched; last saw {page}");
            await Task.Delay(100);
        }
    }

    private static int Count(JsonElement page, string status) => page.GetProperty("counts").GetProperty(status).GetInt32();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_job_that_cannot_be_created_is_listed_as_Error_until_it_is_resumed(bool ramJobStore)
    {
        await using var host = await TestHost.Start(ramJobStore: ramJobStore);
        var client = host.Client();
        await CreateBroken(host, "broken");

        var page = await WaitForSearch(client, "?status=Error", p => p.GetProperty("total").GetInt32() == 1);
        Assert.Equal("broken", page.GetProperty("jobs")[0].GetProperty("name").GetString());
        Assert.Equal((1, 1, 0), (Count(page, "all"), Count(page, "error"), Count(page, "active")));

        // Once the cause is fixed, resuming the job sets its triggers back to normal, and it runs again
        BreakableJob.Broken[host.SchedulerName] = false;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/broken/resume", null)).StatusCode);
        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/broken");
        Assert.All(job.GetProperty("triggers").EnumerateArray(), t => Assert.NotEqual("Error", t.GetProperty("state").GetString()));
        var after = await WaitForSearch(client, "", p => Count(p, "error") == 0);
        Assert.Equal(1, Count(after, "active"));
    }

    [Fact]
    public async Task Resuming_a_job_that_could_not_be_created_by_Run_now_leaves_no_trigger_behind()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        BreakableJob.Broken[host.SchedulerName] = true;
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostJson("api/jobs", new { type = typeof(BreakableJob).FullName, name = "manual" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/manual/trigger", null)).StatusCode);
        await WaitForSearch(client, "?status=Error", p => p.GetProperty("total").GetInt32() == 1);

        BreakableJob.Broken[host.SchedulerName] = false;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/manual/resume", null)).StatusCode);

        // Run now's trigger had fired already, so nothing is left to wait for; the job stays, for the next run
        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/manual");
        Assert.Empty(job.GetProperty("triggers").EnumerateArray());
    }

    [Fact]
    public async Task Resuming_a_group_or_every_job_brings_back_jobs_in_Error()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateBroken(host, "first", group: "billing");
        await CreateBroken(host, "second", group: "reports");
        await WaitForSearch(client, "", p => Count(p, "error") == 2);
        BreakableJob.Broken[host.SchedulerName] = false;

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/groups/billing/resume", null)).StatusCode);
        var page = await WaitForSearch(client, "?status=Error", p => p.GetProperty("total").GetInt32() == 1);
        Assert.Equal("second", page.GetProperty("jobs")[0].GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/scheduler/resume", null)).StatusCode);
        await WaitForSearch(client, "", p => Count(p, "error") == 0);
    }

    [Fact]
    public async Task History_records_a_job_that_could_not_be_created_as_failed_once()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        // Dots in both parts, so the job cannot be told from the text "billing.eu.nightly.export" alone
        await CreateBroken(host, "nightly.export", group: "billing.eu");

        var records = await HistoryScenarios.WaitForHistory(client, "?group=billing.eu&name=nightly.export",
            r => r.Any(x => x.GetProperty("status").GetString() == "Failed"));
        var failed = records.Single();
        Assert.Equal(typeof(BreakableJob).FullName, failed.GetProperty("jobType").GetString());
        var error = failed.GetProperty("error").GetString()!;
        Assert.StartsWith("Not run: the job could not be created. InvalidOperationException: No IInvoiceGateway is registered", error);
        Assert.EndsWith("Quartz has stopped its triggers until the job is resumed.", error);

        // Its trigger fires every second, but Quartz has stopped it: no further records
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(await HistoryScenarios.History(client, "?group=billing.eu&name=nightly.export"));
    }

    [Fact]
    public async Task A_job_factory_that_creates_jobs_by_reflection_reports_what_the_constructor_threw()
    {
        await using var host = await TestHost.Start(history: _ => { },
            services: s => s.AddQuartz(q => q.UseJobFactory<ReflectionJobFactory>()));
        var client = host.Client();
        await CreateBroken(host, "reflected");

        var records = await HistoryScenarios.WaitForHistory(client, "?name=reflected",
            r => r.Any(x => x.GetProperty("status").GetString() == "Failed"));
        // Not reflection's "Exception has been thrown by the target of an invocation"
        Assert.StartsWith("Not run: the job could not be created. InvalidOperationException: No IInvoiceGateway is registered",
            records.Single().GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_job_left_out_of_alerts_raises_none_when_it_cannot_be_created()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(history: _ => { },
            services: AlertTests.Alerts(sink, o => o.Jobs = job => job.Name != "ignored"));
        var client = host.Client();
        await CreateBroken(host, "ignored");

        // History is told at the same time as alerts
        await HistoryScenarios.WaitForHistory(client, "?name=ignored", r => r.Any(x => x.GetProperty("status").GetString() == "Failed"));
        await Task.Delay(500);
        Assert.Empty(sink.Alerts);
    }

    /// <summary>Creates jobs as a host's own factory might, through reflection, which wraps what a constructor throws.</summary>
    private sealed class ReflectionJobFactory(IServiceProvider services) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler) =>
            (IJob)Activator.CreateInstance(bundle.JobDetail.JobType, services.GetRequiredService<IOptions<QuartzOptions>>())!;

        public void ReturnJob(IJob job)
        {
        }
    }

    // A timeout or a disposed service looks like the app stopping, but is not
    [Fact]
    public async Task A_constructor_that_times_out_is_reported_too()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        BreakableJob.TimesOut[host.SchedulerName] = true;
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostJson("api/jobs", new { type = typeof(BreakableJob).FullName, name = "slow" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/slow/trigger", null)).StatusCode);

        var records = await HistoryScenarios.WaitForHistory(client, "?name=slow",
            r => r.Any(x => x.GetProperty("status").GetString() == "Failed"));
        Assert.StartsWith("Not run: the job could not be created. TaskCanceledException: The partner API did not answer in time",
            records[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_job_Quartz_stops_is_alerted_even_when_it_was_already_failing()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(services: AlertTests.Alerts(sink));
        var client = host.Client();
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostJson("api/jobs", new { type = typeof(BreakableJob).FullName, name = "worse" })).StatusCode);

        // It fails, and is alerted
        BreakableJob.Failing[host.SchedulerName] = true;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/worse/trigger", null)).StatusCode);
        await sink.WaitFor(1);

        // Then it cannot even be created, and Quartz stops it: that needs someone to resume it, so it is alerted too
        BreakableJob.Broken[host.SchedulerName] = true;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/worse/trigger", null)).StatusCode);
        await sink.WaitFor(2);
        Assert.Equal(CronlightAlertKind.Failing, sink.Alerts[1].Kind);
        Assert.StartsWith("Not run: the job could not be created", sink.Alerts[1].Error);
    }

    [Fact]
    public async Task A_job_that_cannot_be_created_raises_an_alert_at_once_and_recovers_once_resumed()
    {
        var sink = new AlertTests.RecordingAlertSink();
        // A threshold the job can never reach, since it stops after one attempt
        await using var host = await TestHost.Start(services: AlertTests.Alerts(sink, o => o.AfterConsecutiveFailures = 3));
        await CreateBroken(host, "broken");

        await sink.WaitFor(1);
        var alert = Assert.Single(sink.Alerts);
        Assert.Equal((CronlightAlertKind.Failing, "DEFAULT", "broken", typeof(BreakableJob).FullName),
            (alert.Kind, alert.JobGroup, alert.JobName, alert.JobType));
        Assert.Equal("Job DEFAULT.broken failed", alert.Title);
        Assert.StartsWith("Not run: the job could not be created. InvalidOperationException: No IInvoiceGateway is registered", alert.Error);

        BreakableJob.Broken[host.SchedulerName] = false;
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client().PostAsync("api/jobs/DEFAULT/broken/resume", null)).StatusCode);
        await sink.WaitFor(2);
        Assert.Equal((CronlightAlertKind.Recovered, "Job DEFAULT.broken succeeded again after a failure"),
            (sink.Alerts[1].Kind, sink.Alerts[1].Title));
    }
}
