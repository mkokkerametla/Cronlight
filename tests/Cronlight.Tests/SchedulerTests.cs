using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;

namespace Cronlight.Tests;

/// <summary>A host with a second scheduler it built itself, as apps with one scheduler per team or database do.</summary>
public class SchedulerTests
{
    /// <summary>An in-memory scheduler, added to the host's dashboard with UseCronlight unless told otherwise.</summary>
    private static async Task<IScheduler> AddScheduler(TestHost host, bool useCronlight = true)
    {
        var scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            // Quartz keeps schedulers in a process-wide registry, so each test's has its own name
            ["quartz.scheduler.instanceName"] = $"extra-{Guid.NewGuid().ToString("N")[..8]}",
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
            Assert.Equal(["Cronlight.Alerts", "Cronlight.History", "Cronlight.Retries"],
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
}
