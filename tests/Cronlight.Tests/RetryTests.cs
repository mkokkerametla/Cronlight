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

        // Nor can an edit set them
        var edited = await client.PutJson("api/jobs/DEFAULT/b", new { data = new Dictionary<string, string> { ["Cronlight.Retry"] = "{}" } });
        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
        Assert.Contains("reserved", await edited.Content.ReadAsStringAsync());
        var editedRetry = await client.PutJson("api/jobs/DEFAULT/b", new { retry = new { maxRetries = 1, delaySeconds = 0 } });
        Assert.Contains("retry delay", await editedRetry.Content.ReadAsStringAsync());
        Assert.Null(Retry(await Job(client, "b")));
    }

    [Fact]
    public async Task A_stored_retry_policy_that_cannot_be_read_is_ignored()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        // As a host might have left it, writing the reserved key itself
        await (await host.Scheduler()).AddJob(JobBuilder.Create<FailingJob>().WithIdentity("garbled").StoreDurably()
            .UsingJobData("Cronlight.Retry", "{ maxRetries: three").Build(), replace: false);

        Assert.Null(Retry(await Job(client, "garbled")));
        await client.PostAsync("api/jobs/DEFAULT/garbled/trigger", null);
        Assert.Equal("Failed", Assert.Single(await Finished(client, "garbled", 1)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_job_paused_while_a_run_is_failing_keeps_its_retry_paused()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var gate = Guid.NewGuid().ToString("N");
        var created = await client.PostJson("api/jobs", new
        {
            type = typeof(GatedFailingJob).FullName,
            name = "paused",
            data = new { gate },
            retry = new { maxRetries = 1, delaySeconds = 3600 },
            triggers = new[] { new { kind = "Interval", intervalSeconds = 3600 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(await BlockingJob.Gate(gate).Started.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/paused/pause", null)).StatusCode);
        BlockingJob.Gate(gate).Release.Release();

        var retry = await WaitForRetry(client, "paused");
        Assert.Equal("Paused", retry.GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_retry_of_a_job_that_is_not_durable_cannot_be_cancelled_when_it_is_the_jobs_only_trigger()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        // Registered by the host: not durable, with one trigger, which has fired by the time the retry is scheduled
        var job = JobBuilder.Create<FailingJob>().WithIdentity("fleeting")
            .UsingJobData("Cronlight.Retry", """{"maxRetries":1,"delaySeconds":3600}""").Build();
        await (await host.Scheduler()).ScheduleJob(job, TriggerBuilder.Create().ForJob(job).StartNow().Build());
        var name = (await WaitForRetry(client, "fleeting")).GetProperty("name").GetString();

        var response = await client.PostAsync($"api/jobs/DEFAULT/fleeting/retries/{name}/cancel-retry", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cancelling it would delete the job", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"api/jobs/DEFAULT/missing/retries/{name}/cancel-retry", null)).StatusCode);
    }

    private static async Task<JsonElement> WaitForRetry(HttpClient client, string name)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(100))
        {
            var retry = (await Job(client, name)).GetProperty("triggers").EnumerateArray()
                .FirstOrDefault(t => t.GetProperty("kind").GetString() == "Retry");
            if (retry.ValueKind != JsonValueKind.Undefined)
                return retry;
            Assert.True(DateTime.UtcNow < deadline, $"No retry of {name} was scheduled");
        }
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

    [Fact]
    public async Task Run_again_repeats_a_failed_run_with_its_own_data_from_history()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        var key = Guid.NewGuid().ToString("N");
        // The first attempt and both retries fail
        RetriedJob.FailuresLeft[key] = 3;
        await HistoryScenarios.Create(client, typeof(RetriedJob), "regional", new { key, region = "eu" });
        await client.PostJson("api/jobs/DEFAULT/regional/trigger", new { data = new { region = "us" } });

        var failed = Assert.Single(await HistoryScenarios.WaitForHistory(client, "?name=regional&status=Failed", r => r.Count > 0));
        var data = failed.GetProperty("data").Deserialize<Dictionary<string, string>>()!;
        Assert.Equal(new Dictionary<string, string> { ["region"] = "us" }, data);

        // As the dashboard's Run again does: the failed run's data, not the saved data
        RetriedJob.FailuresLeft[key] = -1;
        var again = await client.PostJson("api/jobs/DEFAULT/regional/trigger", new { data, retryOf = failed.GetProperty("fireInstanceId").GetString() });
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        await Finished(client, "regional", 4);
        Assert.Equal(new[] { "us", "us", "us", "us" }, RetriedJob.Seen[key]);
    }

    [Fact]
    public async Task A_run_s_own_data_is_kept_in_history_with_secrets_masked()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = typeof(InvoiceJob).FullName, name = "invoice", data = new { customerId = "7", smtpPassword = "hunter2" } });
        await client.PostAsync("api/jobs/DEFAULT/invoice/trigger", null);
        await client.PostJson("api/jobs/DEFAULT/invoice/trigger", new { data = new { customerId = "8", smtpPassword = "s3cret" } });

        var records = await HistoryScenarios.WaitForHistory(client, "?name=invoice&status=Succeeded", r => r.Count == 2);
        Assert.Equal(new Dictionary<string, string> { ["customerId"] = "8", ["smtpPassword"] = "********" },
            records[0].GetProperty("data").Deserialize<Dictionary<string, string>>());
        // A plain run used the saved data only
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task Retries_can_be_limited_to_some_exception_types()
    {
        await using var host = await TestHost.Start(history: _ => { });
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PickyRetryJob), "picky", new { error = "invalid" });

        await client.PostAsync("api/jobs/DEFAULT/picky/trigger", null);
        await Finished(client, "picky", 1);
        await Task.Delay(1500);
        Assert.Equal(["Failed"], (await HistoryScenarios.History(client, "?name=picky")).Select(r => r.GetProperty("status").GetString()));

        await client.PostJson("api/jobs/DEFAULT/picky/trigger", new { data = new { error = "timeout" } });
        var records = await Finished(client, "picky", 4);
        Assert.Equal(["Failed", "Retried", "Retried", "Failed"], records.Select(r => r.GetProperty("status").GetString()));

        Assert.Equal(["System.TimeoutException"], Retry(await Job(client, "picky"))!.RetryOn);
        var bad = await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "bad",
            retry = new { maxRetries = 1, delaySeconds = 1, retryOn = new[] { "not a type" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("'not a type' is not an exception type name", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_retry_that_cannot_be_scheduled_leaves_the_run_failed_and_alerts()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(history: _ => { }, services: AlertTests.Alerts(sink));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(SelfDeletingJob), "doomed");
        await client.PostAsync("api/jobs/DEFAULT/doomed/trigger", null);

        var record = Assert.Single(await Finished(client, "doomed", 1));
        Assert.Equal("Failed", record.GetProperty("status").GetString());
        Assert.Contains("(its retry could not be scheduled:", record.GetProperty("error").GetString());
        await sink.WaitFor(1);
        Assert.Equal(CronlightAlertKind.Failing, Assert.Single(sink.Alerts).Kind);
    }

    [Fact]
    public async Task A_waiting_retry_shows_on_the_job_and_can_be_cancelled()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = typeof(FailingJob).FullName, name = "later", retry = new { maxRetries = 1, delaySeconds = 3600 } });
        await client.PostAsync("api/jobs/DEFAULT/later/trigger", null);
        await HistoryScenarios.WaitForHistory(client, "?name=later&status=Retried", r => r.Count > 0);

        var retry = (await Job(client, "later")).GetProperty("triggers").EnumerateArray().Single(t => t.GetProperty("kind").GetString() == "Retry");
        Assert.Equal(2, retry.GetProperty("attempt").GetInt32());
        var name = retry.GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"api/jobs/DEFAULT/later/retries/{name}/cancel-retry", null)).StatusCode);
        Assert.DoesNotContain((await Job(client, "later")).GetProperty("triggers").EnumerateArray(), t => t.GetProperty("kind").GetString() == "Retry");
        Assert.Equal("cancel-retry", audit.Events[^1].Action);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"api/jobs/DEFAULT/later/retries/{name}/cancel-retry", null)).StatusCode);
    }
}
