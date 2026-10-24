using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quartz;

namespace Cronlight.Tests;

public class ApiTests
{
    [Fact]
    public async Task Ui_is_served_under_the_prefix_with_relative_assets()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var redirect = await client.GetAsync("/cronlight"); // TestServer does not follow redirects
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/cronlight/", redirect.Headers.Location!.OriginalString);

        var index = await client.GetAsync("");
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        var html = await index.Content.ReadAsStringAsync();
        Assert.DoesNotContain("src=\"/", html);
        Assert.DoesNotContain("href=\"/", html);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Unloadable_jobs_are_listed_but_read_only()
    {
        await using var host = await TestHost.Start();
        host.InsertForeignJob("other", "report");
        var client = host.Client();

        var jobs = await client.GetFromJsonAsync<JsonElement>("api/jobs");
        var foreign = jobs.EnumerateArray().Single(j => j.GetProperty("name").GetString() == "report");
        Assert.False(foreign.GetProperty("loadable").GetBoolean());
        Assert.Contains("OtherApp", foreign.GetProperty("loadError").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("api/jobs/other/report/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("api/jobs/other/report/trigger", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutJson("api/jobs/other/report", new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync("api/jobs/other/report")).StatusCode);
        Assert.Equal(1, await host.CountJobRows("other", "report"));
    }

    [Fact]
    public async Task Scheduler_failures_return_problem_details_without_stack_traces()
    {
        // Opting in lets the call reach Quartz, which then fails to load the job type
        await using var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true);
        host.InsertForeignJob("other", "report");

        var response = await host.Client().PostAsync("api/jobs/other/report/trigger", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("   at ", body);
        Assert.Contains("traceId", body);
    }

    [Fact]
    public async Task Allowing_unloadable_jobs_permits_pause_and_delete()
    {
        await using var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true);
        host.InsertForeignJob("other", "report");
        var client = host.Client();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/other/report/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("api/jobs/other/report")).StatusCode);
        Assert.Equal(0, await host.CountJobRows("other", "report"));
    }

    [Fact]
    public async Task Create_validates_required_parameters_cron_and_time_zone()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(InvoiceJob).FullName;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new { type, name = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new
        {
            type, name = "a", data = new { customerId = "1" }, triggers = new[] { new { cronExpression = "nope" } }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJson("api/jobs", new
        {
            type, name = "a", data = new { customerId = "1" }, triggers = new[] { new { cronExpression = "0 0 9 * * ?", timeZone = "Mars/Olympus" } }
        })).StatusCode);
        Assert.Empty(await (await host.Scheduler()).GetJobGroupNames());
    }

    [Fact]
    public async Task Parameter_choices_are_offered_and_enforced()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(ExportJob).FullName;

        var types = await client.GetFromJsonAsync<JsonElement>("api/job-types");
        var format = types.EnumerateArray().Single(t => t.GetProperty("typeName").GetString() == type)
            .GetProperty("parameters")[0];
        Assert.Equal(new[] { "csv", "json" }, format.GetProperty("choices").EnumerateArray().Select(c => c.GetString()));

        var rejected = await client.PostJson("api/jobs", new { type, name = "e", data = new { format = "xml" } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("expected one of csv, json", await rejected.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new { type, name = "e", data = new { format = "csv" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutJson("api/jobs/DEFAULT/e", new { data = new { format = "pdf" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/e", new { data = new { format = "json" } })).StatusCode);
    }

    [Fact]
    public async Task Editing_triggers_adds_reschedules_and_removes_cron_triggers_and_keeps_paused_state()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "p",
            triggers = new[]
            {
                new { name = "keep", cronExpression = "0 0 9 * * ?", timeZone = (string?)null },
                new { name = "drop", cronExpression = "0 0 10 * * ?", timeZone = (string?)null }
            }
        });
        var scheduler = await host.Scheduler();
        await scheduler.PauseTrigger(new TriggerKey("keep", "DEFAULT"));

        var response = await client.PutJson("api/jobs/DEFAULT/p", new
        {
            triggers = new[]
            {
                new { name = "keep", cronExpression = "0 30 8 * * ?", timeZone = (string?)"Europe/London" },
                new { name = "new", cronExpression = "0 0 12 * * ?", timeZone = (string?)null }
            }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var triggers = (await scheduler.GetTriggersOfJob(new JobKey("p"))).Cast<ICronTrigger>().ToDictionary(t => t.Key.Name);
        Assert.Equal(new[] { "keep", "new" }, triggers.Keys.Order());
        Assert.Equal("0 30 8 * * ?", triggers["keep"].CronExpressionString);
        Assert.Equal("Europe/London", triggers["keep"].TimeZone.Id);
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey("keep", "DEFAULT")));
    }

    [Fact]
    public async Task Running_jobs_are_read_from_the_job_store()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        await client.PostJson("api/jobs", new { type = typeof(BlockingJob).FullName, name = "block", data = new { gate = host.SchedulerName } });
        await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);

        try
        {
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            var running = await client.GetFromJsonAsync<JsonElement>("api/running");

            Assert.Equal("cluster", running.GetProperty("source").GetString());
            var job = Assert.Single(running.GetProperty("jobs").EnumerateArray());
            Assert.Equal("block", job.GetProperty("name").GetString());
            Assert.Equal(typeof(BlockingJob).FullName, job.GetProperty("jobType").GetString());
        }
        finally
        {
            gate.Release.Release();
        }
    }

    [Fact]
    public async Task Cron_preview_reports_validity_and_next_times()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        var valid = await client.GetFromJsonAsync<JsonElement>("api/cron/preview?expression=0%200%209%20*%20*%20%3F&timeZone=America/Chicago&count=3");
        Assert.True(valid.GetProperty("isValid").GetBoolean());
        Assert.Equal(3, valid.GetProperty("nextFireTimes").GetArrayLength());

        var invalid = await client.GetFromJsonAsync<JsonElement>("api/cron/preview?expression=nope");
        Assert.False(invalid.GetProperty("isValid").GetBoolean());
    }
}
