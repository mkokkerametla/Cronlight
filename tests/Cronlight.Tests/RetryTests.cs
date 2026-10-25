using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Cronlight.Tests;

public class RetryTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement> Job(HttpClient client, string name) =>
        await client.GetFromJsonAsync<JsonElement>($"api/jobs/DEFAULT/{name}");

    private static JobRetryPolicy? Retry(JsonElement job) =>
        job.GetProperty("retry").ValueKind == JsonValueKind.Null ? null : job.GetProperty("retry").Deserialize<JobRetryPolicy>(Web);

    private static Task<List<JsonElement>> Finished(HttpClient client, string name, int count) =>
        HistoryScenarios.WaitForHistory(client, $"?name={name}", r => r.Count(x => x.GetProperty("status").GetString() != "Running") >= count);

    [Fact]
    public async Task Only_the_last_attempt_of_a_retried_run_raises_an_alert()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(history: _ => { }, services: AlertTests.Alerts(sink));
        var client = host.Client();
        var failing = Guid.NewGuid().ToString("N");
        var recovering = Guid.NewGuid().ToString("N");
        RetriedJob.FailuresLeft[failing] = 3;
        RetriedJob.FailuresLeft[recovering] = 1;
        await HistoryScenarios.Create(client, typeof(RetriedJob), "failing", new { key = failing });
        await HistoryScenarios.Create(client, typeof(RetriedJob), "recovering", new { key = recovering });

        await client.PostAsync("api/jobs/DEFAULT/recovering/trigger", null);
        await client.PostAsync("api/jobs/DEFAULT/failing/trigger", null);

        await Finished(client, "recovering", 2);
        var records = await Finished(client, "failing", 3);
        Assert.Equal(new[] { "Failed", "Retried", "Retried" }, records.Select(r => r.GetProperty("status").GetString()));
        await sink.WaitFor(1);
        await Task.Delay(500);
        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(("failing", 1), (alert.JobName, alert.ConsecutiveFailures));
        Assert.Equal(new JobRetryPolicy(2, 1), Retry(await Job(client, "failing")));
    }

    [Fact]
    public async Task A_job_can_have_its_own_retries_which_are_audited_and_kept_when_other_fields_change()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var client = host.Client();

        var created = await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "plain",
            data = new { region = "eu" },
            retry = new { maxRetries = 3, delaySeconds = 30, exponential = true },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await Job(client, "plain");
        Assert.Equal(new JobRetryPolicy(3, 30, true), Retry(job));
        // Stored with the job, but not shown or edited as its data
        Assert.Equal(new[] { "region" }, job.GetProperty("data").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new CronlightAuditChange(null, "3 retries, 30s doubling"), audit.Events[^1].Changes["retry"]);

        // The form sends the data it shows, without Cronlight's keys: they are kept
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/plain", new { data = new { region = "us" } })).StatusCode);
        Assert.Equal(new JobRetryPolicy(3, 30, true), Retry(await Job(client, "plain")));
        Assert.False(audit.Events[^1].Changes.ContainsKey("retry"));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutJson("api/jobs/DEFAULT/plain", new { retry = new { maxRetries = 0, delaySeconds = 30 } })).StatusCode);
        Assert.Null(Retry(await Job(client, "plain")));
        Assert.Equal(new CronlightAuditChange("3 retries, 30s doubling", null), audit.Events[^1].Changes["retry"]);
    }

    [Fact]
    public async Task A_job_follows_its_types_retries_until_it_is_given_different_ones()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var scheduler = await host.Scheduler();
        await HistoryScenarios.Create(client, typeof(RetriedJob), "typed", new { key = "typed" });

        // The type's policy, sent back unchanged, is not stored: a later change to the attribute applies
        await client.PutJson("api/jobs/DEFAULT/typed", new { retry = new { maxRetries = 2, delaySeconds = 1 } });
        Assert.False((await scheduler.GetJobDetail(new JobKey("typed")))!.JobDataMap.ContainsKey("Cronlight.Retry"));

        // Off for this job only
        await client.PutJson("api/jobs/DEFAULT/typed", new { retry = new { maxRetries = 0, delaySeconds = 1 } });
        Assert.Null(Retry(await Job(client, "typed")));
        var types = await client.GetFromJsonAsync<JsonElement>("api/job-types");
        var type = types.EnumerateArray().Single(t => t.GetProperty("typeName").GetString() == typeof(RetriedJob).FullName);
        Assert.Equal(new JobRetryPolicy(2, 1), type.GetProperty("retry").Deserialize<JobRetryPolicy>(Web));
    }

    [Fact]
    public async Task Invalid_retries_and_Cronlight_s_own_data_keys_are_refused()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var type = typeof(PlainJob).FullName;

        var tooMany = await client.PostJson("api/jobs", new { type, name = "a", retry = new { maxRetries = 21, delaySeconds = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        var noDelay = await client.PostJson("api/jobs", new { type, name = "a", retry = new { maxRetries = 1, delaySeconds = 0 } });
        Assert.Contains("retry delay", await noDelay.Content.ReadAsStringAsync());

        var reserved = await client.PostJson("api/jobs", new { type, name = "a", data = new Dictionary<string, string> { ["Cronlight.Attempt"] = "5" } });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Contains("reserved", await reserved.Content.ReadAsStringAsync());

        await HistoryScenarios.Create(client, typeof(PlainJob), "b");
        var run = await client.PostJson("api/jobs/DEFAULT/b/trigger", new { data = new Dictionary<string, string> { ["Cronlight.Attempt"] = "5" } });
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
    }

    [Fact]
    public async Task A_run_with_different_data_uses_it_for_that_run_and_its_retries_only()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var client = host.Client();
        var key = Guid.NewGuid().ToString("N");
        RetriedJob.FailuresLeft[key] = 1;
        await HistoryScenarios.Create(client, typeof(RetriedJob), "regional", new { key, region = "eu" });

        var run = await client.PostJson("api/jobs/DEFAULT/regional/trigger", new { data = new { region = "us", key }, retryOf = "fire-1" });
        Assert.Equal(HttpStatusCode.NoContent, run.StatusCode);
        await Finished(client, "regional", 2);
        await client.PostAsync("api/jobs/DEFAULT/regional/trigger", null);
        await Finished(client, "regional", 3);

        // The first attempt and its retry saw the change; the next plain run sees the saved value
        Assert.Equal(new[] { "us", "us", "eu" }, RetriedJob.Seen[key]);
        Assert.Equal("eu", (await Job(client, "regional")).GetProperty("data").GetProperty("region").GetString());
        // Only what changed is audited, with the run it retries
        var changes = audit.Events.First(e => e.Action == "trigger").Changes;
        Assert.Equal(new CronlightAuditChange("eu", "us"), changes["data.region"]);
        Assert.Equal(new CronlightAuditChange(null, "fire-1"), changes["retryOf"]);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task A_run_with_different_data_keeps_masked_secrets_and_checks_the_job_types_rules()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = typeof(InvoiceJob).FullName, name = "invoice", data = new { customerId = "7", smtpPassword = "hunter2" } });

        var cleared = await client.PostJson("api/jobs/DEFAULT/invoice/trigger", new { data = new { customerId = "" } });
        Assert.Contains("Missing required parameters: customerId", await cleared.Content.ReadAsStringAsync());

        var masked = await client.PostJson("api/jobs/DEFAULT/invoice/trigger", new { data = new { customerId = "8", smtpPassword = "********" } });
        Assert.Equal(HttpStatusCode.NoContent, masked.StatusCode);
        Assert.Equal(new[] { "data.customerId" }, audit.Events[^1].Changes.Keys);
    }
}
