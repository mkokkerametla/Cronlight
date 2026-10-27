using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using QuartzSample.Jobs;

namespace Cronlight.Tests;

/// <summary>A host with a second scheduler it built itself, as apps with one scheduler per team or database do.</summary>
public class SchedulerTests
{
    /// <summary>An in-memory scheduler, added to the host's dashboard with UseCronlight unless told otherwise.</summary>
    private static async Task<IScheduler> AddScheduler(TestHost host, bool useCronlight = true, string? name = null)
    {
        var scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            // Quartz keeps schedulers in a process-wide registry, so each test's has its own name
            ["quartz.scheduler.instanceName"] = name ?? UniqueName("extra"),
            ["quartz.threadPool.maxConcurrency"] = "2",
        }).GetScheduler();
        if (useCronlight)
            await scheduler.UseCronlight(host.Services);
        await scheduler.Start();
        return scheduler;
    }

    private static async Task<List<string?>> Schedulers(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/scheduler")).GetProperty("schedulers").EnumerateArray()
            .Select(e => e.GetString()).ToList();

    private static string Status(JsonElement record) => record.GetProperty("status").GetString()!;

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task Each_scheduler_has_its_own_jobs_history_and_audit_trail()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var extra = await AddScheduler(host);
        try
        {
            var client = host.Client();
            var other = $"?scheduler={extra.SchedulerName}";
            Assert.Equal([host.SchedulerName, extra.SchedulerName], await Schedulers(client));
            Assert.Equal(extra.SchedulerName,
                (await client.GetFromJsonAsync<JsonElement>($"api/scheduler{other}")).GetProperty("schedulerName").GetString());

            // The same job key in both, with different types
            await HistoryScenarios.Create(client, typeof(PlainJob), "nightly");
            var created = await client.PostJson($"api/jobs{other}", new { type = typeof(FailingJob).FullName, name = "nightly" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(typeof(FailingJob).FullName,
                (await client.GetFromJsonAsync<JsonElement>($"api/jobs/DEFAULT/nightly{other}")).GetProperty("jobType").GetString());
            Assert.Equal(typeof(PlainJob).FullName,
                (await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/nightly")).GetProperty("jobType").GetString());
            // An in-memory scheduler is listed through its own API
            Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>($"api/jobs/search{other}")).GetProperty("total").GetInt32());

            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"api/jobs/DEFAULT/nightly/trigger{other}", null)).StatusCode);
            var records = await HistoryScenarios.WaitForHistory(client, other, r => r.Any(x => Status(x) == "Failed"));
            Assert.All(records, r => Assert.Equal(extra.SchedulerName, r.GetProperty("schedulerName").GetString()));
            Assert.Empty(await HistoryScenarios.History(client, ""));

            Assert.Equal([host.SchedulerName, extra.SchedulerName, extra.SchedulerName], audit.Events.Select(e => e.Scheduler));
        }
        finally
        {
            await extra.Shutdown(waitForJobsToComplete: true);
        }
    }

    [Fact]
    public async Task A_scheduler_the_host_did_not_add_is_neither_served_nor_listened_to()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        // Another app's scheduler in the same process: Quartz's registry holds it, but this host never added it
        var unrelated = await AddScheduler(host, useCronlight: false);
        try
        {
            var client = host.Client();
            var query = $"?scheduler={unrelated.SchedulerName}";
            Assert.Equal([host.SchedulerName], await Schedulers(client));

            var read = await client.GetAsync($"api/jobs/search{query}");
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.Contains($"No scheduler named '{unrelated.SchedulerName}'", await read.Content.ReadAsStringAsync());

            var create = await client.PostJson($"api/jobs{query}", new { type = typeof(PlainJob).FullName, name = "intruder" });
            Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
            Assert.Empty(await unrelated.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
            var refused = Assert.Single(audit.Events);
            Assert.Equal((false, 404, unrelated.SchedulerName), (refused.Succeeded, refused.StatusCode, refused.Scheduler));

            Assert.DoesNotContain(unrelated.ListenerManager.GetJobListeners(), l => l.Name.StartsWith("Cronlight."));
        }
        finally
        {
            await unrelated.Shutdown();
        }
    }

    [Fact]
    public async Task An_added_scheduler_gets_retries_and_alerts_with_failures_counted_per_scheduler()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(
            history: _ => { },
            configure: o => o.SecretsSection = "JobSecrets",
            services: AlertTests.Alerts(sink, o => o.AfterConsecutiveFailures = 2));
        var extra = await AddScheduler(host);
        try
        {
            Assert.Equal(["Cronlight.Alerts", "Cronlight.History", "Cronlight.Metrics", "Cronlight.Retries"],
                extra.ListenerManager.GetJobListeners().Select(l => l.Name).Order());
            Assert.Contains(extra.ListenerManager.GetTriggerListeners(), l => l.Name == "Cronlight.SecretReferences");

            var client = host.Client();
            var other = $"?scheduler={extra.SchedulerName}";
            await HistoryScenarios.Create(client, typeof(FailingJob), "nightly");
            await client.PostJson($"api/jobs{other}",
                new { type = typeof(FailingJob).FullName, name = "nightly", retry = new { maxRetries = 1, delaySeconds = 1 } });

            // One failure of the same job key in each scheduler: two failures, but not two in a row in either
            await client.PostAsync("api/jobs/DEFAULT/nightly/trigger", null);
            await client.PostAsync($"api/jobs/DEFAULT/nightly/trigger{other}", null);
            await HistoryScenarios.WaitForHistory(client, "", r => r.Any(x => Status(x) == "Failed"));
            var attempts = await HistoryScenarios.WaitForHistory(client, other, r => r.Any(x => Status(x) == "Failed"));
            Assert.Equal(["Failed", "Retried"], attempts.Select(Status).Where(s => s != "Running"));
            await Task.Delay(500);
            Assert.Empty(sink.Alerts);

            // A second one in a row in the added scheduler alerts, naming it
            await client.PostAsync($"api/jobs/DEFAULT/nightly/trigger{other}", null);
            await sink.WaitFor(1);
            var alert = Assert.Single(sink.Alerts);
            Assert.Equal((extra.SchedulerName, "nightly", 2), (alert.SchedulerName, alert.JobName, alert.ConsecutiveFailures));
        }
        finally
        {
            await extra.Shutdown(waitForJobsToComplete: true);
        }
    }

    [Fact]
    public async Task A_scheduler_can_be_shown_or_left_changeable_only_to_some_users()
    {
        var audit = new AuditTests.RecordingSink();
        var hidden = UniqueName("a-hidden");
        var watched = UniqueName("b-watched");
        await using var host = await TestHost.Start(
            configure: o =>
            {
                o.ViewPolicy = "Viewers";
                o.ManagePolicy = "Viewers";
                o.Schedulers[hidden] = new CronlightSchedulerOptions { ViewPolicy = "Admins" };
                o.Schedulers[watched] = new CronlightSchedulerOptions { ManagePolicy = "Admins" };
            },
            services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var extras = new[] { await AddScheduler(host, name: hidden), await AddScheduler(host, name: watched) };
        try
        {
            var viewer = host.Client("viewer");
            var admin = host.Client("admin");
            Assert.Equal([host.SchedulerName, watched], await Schedulers(viewer));
            Assert.Equal([host.SchedulerName, hidden, watched], await Schedulers(admin));

            var refused = await viewer.GetAsync($"api/jobs/search?scheduler={hidden}");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains($"You do not have access to scheduler '{hidden}'", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"api/jobs/search?scheduler={hidden}")).StatusCode);

            // Seen but not changed by viewers, who still manage the host's own scheduler
            Assert.False((await viewer.GetFromJsonAsync<JsonElement>($"api/scheduler?scheduler={watched}")).GetProperty("canManage").GetBoolean());
            var create = await viewer.PostJson($"api/jobs?scheduler={watched}", new { type = typeof(PlainJob).FullName, name = "nope" });
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.Equal((false, 403, watched), (audit.Events[^1].Succeeded, audit.Events[^1].StatusCode, audit.Events[^1].Scheduler));
            Assert.Equal(HttpStatusCode.Created,
                (await viewer.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "fine" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created,
                (await admin.PostJson($"api/jobs?scheduler={watched}", new { type = typeof(PlainJob).FullName, name = "fine" })).StatusCode);
        }
        finally
        {
            foreach (var extra in extras)
                await extra.Shutdown();
        }
    }

    private static async Task<List<string>> JobTypes(HttpClient client, string? scheduler = null) =>
        (await client.GetFromJsonAsync<JsonElement>(scheduler == null ? "api/job-types" : $"api/job-types?scheduler={scheduler}"))
            .EnumerateArray().Select(t => t.GetProperty("typeName").GetString()!).ToList();

    [Fact]
    public async Task A_scheduler_given_its_own_job_assemblies_offers_only_their_job_types()
    {
        var partners = UniqueName("partners");
        // The host's own scheduler offers the test assembly's job types (TestHost lists it in JobAssemblies)
        await using var host = await TestHost.Start(configure: o => o.Schedulers[partners].JobAssemblies.Add(typeof(PartnerCallJob).Assembly));
        var extra = await AddScheduler(host, name: partners);
        try
        {
            var client = host.Client();
            Assert.Equal([typeof(PartnerCallJob).FullName!], await JobTypes(client, partners));
            var own = await JobTypes(client);
            Assert.Contains(typeof(PlainJob).FullName!, own);
            Assert.DoesNotContain(typeof(PartnerCallJob).FullName!, own);

            var refused = await client.PostJson($"api/jobs?scheduler={partners}", new { type = typeof(PlainJob).FullName, name = "plain" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains($"is not offered for scheduler '{partners}'", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostJson("api/jobs", new { type = typeof(PartnerCallJob).FullName, name = "call" })).StatusCode);

            Assert.Equal(HttpStatusCode.Created,
                (await client.PostJson($"api/jobs?scheduler={partners}", new { type = typeof(PartnerCallJob).FullName, name = "call" })).StatusCode);
            // Its form is described like any other job's
            var job = await client.GetFromJsonAsync<JsonElement>($"api/jobs/DEFAULT/call?scheduler={partners}");
            Assert.Equal("Partner call", job.GetProperty("displayName").GetString());
        }
        finally
        {
            await extra.Shutdown();
        }
    }

    /// <summary>The host's own settings under another scheduler name, as another scheduler on the same database would have them.</summary>
    private static NameValueCollection DatabaseProperties(TestHost host, string name)
    {
        var properties = new NameValueCollection();
        foreach (var (key, value) in host.Services.GetRequiredService<IOptions<QuartzOptions>>().Value)
            if (!key.Contains("jobFactory", StringComparison.Ordinal))
                properties[key] = value;
        properties["quartz.scheduler.instanceName"] = name;
        return properties;
    }

    // Each is a database the dashboard cannot open or read itself, so it goes through Quartz, as for an in-memory store
    [Theory]
    [InlineData("quartz.jobStore.tablePrefix", "QRTZ_;")]
    [InlineData("quartz.jobStore.tablePrefix", "MISSING_")]
    [InlineData("quartz.dataSource.{0}.connectionString", null)]
    [InlineData("quartz.dataSource.{0}.provider", "NoSuchProvider")]
    public async Task An_added_scheduler_whose_tables_cannot_be_read_directly_is_read_through_Quartz(string setting, string? value)
    {
        await using var host = await TestHost.Start();
        var name = UniqueName("described");
        var properties = DatabaseProperties(host, name);
        var scheduler = await new StdSchedulerFactory(properties).GetScheduler();
        // Only what the dashboard is told is wrong: the scheduler itself runs on the right settings
        var described = new NameValueCollection(properties);
        described[string.Format(setting, properties["quartz.jobStore.dataSource"])] = value;
        await scheduler.UseCronlight(host.Services, described);
        await scheduler.Start();
        try
        {
            var client = host.Client();
            var query = $"?scheduler={name}";
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostJson($"api/jobs{query}", new { type = typeof(PlainJob).FullName, name = "described-job" })).StatusCode);
            // Another app's job in the same scheduler, whose type Quartz cannot load here
            await host.Database.Execute(
                $"INSERT INTO QRTZ_JOB_DETAILS VALUES ('{name}', 'report', 'other', NULL, 'OtherApp.Jobs.ReportJob, OtherApp', 1, 0, 0, 0, NULL)");

            var page = await client.GetFromJsonAsync<JsonElement>($"api/jobs/search{query}");
            Assert.Equal(["described-job", "report"], page.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("name").GetString()).Order());
            Assert.Equal(1, page.GetProperty("counts").GetProperty("unloadable").GetInt32());
            Assert.Equal("local", (await client.GetFromJsonAsync<JsonElement>($"api/running{query}")).GetProperty("source").GetString());
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }

    [Fact]
    public async Task Adding_the_hosts_own_scheduler_again_changes_nothing()
    {
        await using var host = await TestHost.Start(history: _ => { });
        await (await host.Scheduler()).UseCronlight(host.Services);
        var client = host.Client();

        Assert.Equal([host.SchedulerName], await Schedulers(client));
        // Its listeners are attached once, so each run is recorded once
        await HistoryScenarios.Create(client, typeof(PlainJob), "once");
        await client.PostAsync("api/jobs/DEFAULT/once/trigger", null);
        var records = await HistoryScenarios.WaitForHistory(client, "?name=once", r => r[0].GetProperty("status").GetString() == "Succeeded");
        Assert.Single(records);
    }

    [Fact]
    public async Task An_added_scheduler_with_a_database_store_is_read_from_its_tables_when_given_its_properties()
    {
        await using var host = await TestHost.Start();
        var name = UniqueName("stored");
        var properties = DatabaseProperties(host, name);
        var scheduler = await new StdSchedulerFactory(properties).GetScheduler();
        await scheduler.UseCronlight(host.Services, properties);
        await scheduler.Start();
        try
        {
            var client = host.Client();
            var query = $"?scheduler={name}";
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostJson($"api/jobs{query}", new { type = typeof(PlainJob).FullName, name = "stored-job" })).StatusCode);
            Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>($"api/jobs/search{query}")).GetProperty("total").GetInt32());
            // Read from QRTZ_FIRED_TRIGGERS, which covers every node, rather than this node's running jobs
            Assert.Equal("cluster", (await client.GetFromJsonAsync<JsonElement>($"api/running{query}")).GetProperty("source").GetString());
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }
}
