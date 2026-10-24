using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quartz;

namespace Cronlight.Tests;

public class ApiTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public async Task Scheduler_info_reports_whether_to_show_the_schedule_assistant(bool? setting, bool expected)
    {
        await using var host = await TestHost.Start(configure: o =>
        {
            if (setting is { } show)
                o.ShowScheduleAssistant = show;
        });

        var scheduler = await host.Client().GetFromJsonAsync<JsonElement>("api/scheduler");

        Assert.Equal(expected, scheduler.GetProperty("showScheduleAssistant").GetBoolean());
    }

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
    public async Task Editing_triggers_never_deletes_a_job_registered_by_the_host()
    {
        await using var host = await TestHost.Start();
        await EditScenarios.HostRegisteredJobSurvivesTriggerEdits(host);
    }

    [Fact]
    public async Task Editing_keeps_the_type_of_job_data_values()
    {
        await using var host = await TestHost.Start(useProperties: false);
        var client = host.Client();
        var scheduler = await host.Scheduler();
        var key = new JobKey("typed");
        await scheduler.AddJob(JobBuilder.Create<PlainJob>().WithIdentity(key).StoreDurably()
            .SetJobData(new JobDataMap { ["count"] = 5, ["enabled"] = true, ["label"] = "a", ["items"] = new List<string> { "x", "y" } })
            .Build(), replace: false);
        var before = (await scheduler.GetJobDetail(key))!.JobDataMap;

        // What the edit form sends back: every value as the text it was shown, one of them changed
        var shown = (await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/typed")).GetProperty("data")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        shown["count"] = "6";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/typed", new { data = shown })).StatusCode);

        var after = (await scheduler.GetJobDetail(key))!.JobDataMap;
        Assert.Equal(before["count"].GetType(), after["count"].GetType());
        Assert.Equal(6L, Convert.ToInt64(after["count"]));
        Assert.Equal(before["enabled"].GetType(), after["enabled"].GetType());
        Assert.Equal(before["items"].GetType(), after["items"].GetType());
        Assert.Equal("a", after["label"]);
    }

    [Fact]
    public async Task Create_stores_nothing_when_a_trigger_cannot_be_scheduled()
    {
        await using var host = await TestHost.Start();
        await EditScenarios.CreateStoresNothingWhenATriggerCannotBeScheduled(host);
    }

    [Fact]
    public async Task Editing_rejects_invalid_triggers_before_changing_anything()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(PlainJob).FullName;
        await client.PostJson("api/jobs", new { type, name = "other", triggers = new[] { new { name = "taken", cronExpression = "0 0 9 * * ?" } } });
        await client.PostJson("api/jobs", new { type, name = "p", description = "before", triggers = new[] { new { name = "mine", cronExpression = "0 0 6 * * ?" } } });

        var nameTaken = await client.PutJson("api/jobs/DEFAULT/p", new { description = "after", triggers = new[] { new { name = "taken", cronExpression = "0 0 7 * * ?" } } });
        Assert.Equal(HttpStatusCode.Conflict, nameTaken.StatusCode);
        var neverFires = await client.PutJson("api/jobs/DEFAULT/p", new { description = "after", triggers = new[] { new { name = "mine", cronExpression = "0 0 0 1 1 ? 2020" } } });
        Assert.Equal(HttpStatusCode.BadRequest, neverFires.StatusCode);

        var scheduler = await host.Scheduler();
        Assert.Equal("before", (await scheduler.GetJobDetail(new JobKey("p")))!.Description);
        Assert.Equal("mine", Assert.Single(await scheduler.GetTriggersOfJob(new JobKey("p"))).Key.Name);
    }

    [Fact]
    public async Task Unnamed_triggers_get_names_not_already_in_use()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "p",
            triggers = new[] { new { cronExpression = "0 0 1 * * ?" }, new { cronExpression = "0 0 2 * * ?" } }
        });
        var scheduler = await host.Scheduler();
        Assert.Equal(new[] { "p-trigger-1", "p-trigger-2" }, (await scheduler.GetTriggersOfJob(new JobKey("p"))).Select(t => t.Key.Name).Order());

        // Remove the first trigger and add a blank one: the blank must not reuse an existing name
        var response = await client.PutJson("api/jobs/DEFAULT/p", new
        {
            triggers = new[] { new { name = (string?)"p-trigger-2", cronExpression = "0 0 2 * * ?" }, new { name = (string?)null, cronExpression = "0 0 3 * * ?" } }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var crons = (await scheduler.GetTriggersOfJob(new JobKey("p"))).Cast<ICronTrigger>().ToDictionary(t => t.Key.Name, t => t.CronExpressionString);
        Assert.Equal("0 0 2 * * ?", crons["p-trigger-2"]);
        Assert.DoesNotContain("p-trigger-1", crons.Keys);
        Assert.Equal(2, crons.Count);
        Assert.Contains("0 0 3 * * ?", crons.Values);
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

/// <summary>Edit scenarios that depend on the job store, also run against each database in DatabaseTests.</summary>
public static class EditScenarios
{
    public static async Task HostRegisteredJobSurvivesTriggerEdits(TestHost host)
    {
        // Like AddQuartz().ScheduleJob<T>(): a non-durable job whose trigger is in the DEFAULT group
        var client = host.Client();
        var scheduler = await host.Scheduler();
        var key = new JobKey("cleanup", "maintenance");
        await scheduler.ScheduleJob(
            JobBuilder.Create<PlainJob>().WithIdentity(key).Build(),
            TriggerBuilder.Create().WithIdentity("nightly").ForJob(key).WithCronSchedule("0 0 2 * * ?").Build());

        // Same name: rescheduled in place, keeping its group
        var edited = await client.PutJson("api/jobs/maintenance/cleanup", new { triggers = new[] { new { name = "nightly", cronExpression = "0 0 3 * * ?" } } });
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var trigger = Assert.IsAssignableFrom<ICronTrigger>(Assert.Single(await scheduler.GetTriggersOfJob(key)));
        Assert.Equal(new TriggerKey("nightly", "DEFAULT"), trigger.Key);
        Assert.Equal("0 0 3 * * ?", trigger.CronExpressionString);

        // Renaming the only trigger adds the new one before removing the old one
        var renamed = await client.PutJson("api/jobs/maintenance/cleanup", new { triggers = new[] { new { name = "renamed", cronExpression = "0 0 4 * * ?" } } });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal("renamed", Assert.Single(await scheduler.GetTriggersOfJob(key)).Key.Name);

        // Removing every trigger keeps the job, now durable, for manual runs
        var cleared = await client.PutJson("api/jobs/maintenance/cleanup", new { triggers = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Empty(await scheduler.GetTriggersOfJob(key));
        Assert.True((await scheduler.GetJobDetail(key))!.Durable);
    }

    public static async Task CreateStoresNothingWhenATriggerCannotBeScheduled(TestHost host)
    {
        var client = host.Client();
        var type = typeof(PlainJob).FullName;
        await client.PostJson("api/jobs", new { type, name = "other", triggers = new[] { new { name = "taken", cronExpression = "0 0 9 * * ?" } } });

        var neverFires = await client.PostJson("api/jobs", new { type, name = "a", triggers = new[] { new { name = (string?)null, cronExpression = "0 0 0 1 1 ? 2020" } } });
        Assert.Equal(HttpStatusCode.BadRequest, neverFires.StatusCode);
        Assert.Contains("never fires", await neverFires.Content.ReadAsStringAsync());

        var nameTaken = await client.PostJson("api/jobs", new
        {
            type, name = "b",
            triggers = new[] { new { name = (string?)"fresh", cronExpression = "0 0 8 * * ?" }, new { name = (string?)"taken", cronExpression = "0 0 9 * * ?" } }
        });
        Assert.Equal(HttpStatusCode.Conflict, nameTaken.StatusCode);

        var scheduler = await host.Scheduler();
        Assert.False(await scheduler.CheckExists(new JobKey("a")));
        Assert.False(await scheduler.CheckExists(new JobKey("b")));
        Assert.False(await scheduler.CheckExists(new TriggerKey("fresh")));
    }
}
