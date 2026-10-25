using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Cronlight.Tests;

public class AlertTests
{
    internal static Action<IServiceCollection> Alerts(RecordingAlertSink sink, Action<CronlightAlertOptions>? configure = null) => s =>
    {
        s.AddSingleton<ICronlightAlertSink>(sink);
        s.AddCronlightAlerts(configure);
    };

    private static async Task Run(HttpClient client, string name) =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"api/jobs/DEFAULT/{name}/trigger", null)).StatusCode);

    [Fact]
    public async Task Alerts_once_after_N_failures_in_a_row_and_again_when_the_job_recovers()
    {
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(history: _ => { }, services: Alerts(sink, StreakOptions));
        await AlertsOnceAfterFailuresInARow(host, sink);
    }

    internal static void StreakOptions(CronlightAlertOptions o)
    {
        o.AfterConsecutiveFailures = 2;
        o.DashboardUrl = "https://ops.example/jobs/";
    }

    /// <summary>Fail 3, succeed 2, fail 2 with a threshold of 2: alert, recover, alert again.</summary>
    internal static async Task AlertsOnceAfterFailuresInARow(TestHost host, RecordingAlertSink sink)
    {
        var client = host.Client();
        var key = Guid.NewGuid().ToString("N");
        await HistoryScenarios.Create(client, typeof(FlipJob), "flaky", new { key });

        // Each run waits for the previous one's history, which the streak is counted from
        async Task RunAndWait(bool fail, int finished)
        {
            FlipJob.Failing[key] = fail;
            await Run(client, "flaky");
            await HistoryScenarios.WaitForHistory(client, "?name=flaky",
                r => r.Count(x => x.GetProperty("status").GetString() != "Running") >= finished);
        }

        await RunAndWait(fail: true, 1);
        await RunAndWait(fail: true, 2);
        await RunAndWait(fail: true, 3);
        await RunAndWait(fail: false, 4);
        await RunAndWait(fail: false, 5);
        await sink.WaitFor(2);
        // Any further alert would have been queued behind these runs
        await RunAndWait(fail: true, 6);
        await RunAndWait(fail: true, 7);
        await sink.WaitFor(3);

        var alerts = sink.Alerts;
        Assert.Equal(new[] { (CronlightAlertKind.Failing, 2), (CronlightAlertKind.Recovered, 3), (CronlightAlertKind.Failing, 2) },
            alerts.Select(a => (a.Kind, a.ConsecutiveFailures)));
        var failing = alerts[0];
        Assert.Equal(("DEFAULT", "flaky", typeof(FlipJob).FullName), (failing.JobGroup, failing.JobName, failing.JobType));
        Assert.Equal($"InvalidOperationException: {key} is broken", failing.Error);
        Assert.Equal("https://ops.example/jobs/", failing.DashboardUrl);
        Assert.Equal("Job DEFAULT.flaky failed 2 times in a row", failing.Title);
        Assert.Null(alerts[1].Error);
        Assert.Equal("Job DEFAULT.flaky succeeded again after 3 failures in a row", alerts[1].Title);
    }

    [Fact]
    public async Task Without_history_each_node_counts_its_own_runs()
    {
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(ramJobStore: true, services: Alerts(sink));
        var client = host.Client();
        var key = Guid.NewGuid().ToString("N");
        await HistoryScenarios.Create(client, typeof(FlipJob), "flaky", new { key });

        FlipJob.Failing[key] = true;
        await Run(client, "flaky");
        await sink.WaitFor(1);
        FlipJob.Failing[key] = false;
        await Run(client, "flaky");
        await sink.WaitFor(2);
        FlipJob.Failing[key] = true;
        await Run(client, "flaky");
        await sink.WaitFor(3);

        Assert.Equal(new[] { CronlightAlertKind.Failing, CronlightAlertKind.Recovered, CronlightAlertKind.Failing },
            sink.Alerts.Select(a => a.Kind));
        Assert.Equal("Job DEFAULT.flaky failed", sink.Alerts[0].Title);
    }

    [Fact]
    public async Task Cancelled_runs_and_jobs_left_out_by_the_filter_raise_no_alert()
    {
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(services: Alerts(sink, o => o.Jobs = job => job.Name != "ignored"));
        var client = host.Client();
        var gate = Guid.NewGuid().ToString("N");
        await HistoryScenarios.Create(client, typeof(CancellableJob), "long", new { gate });
        await HistoryScenarios.Create(client, typeof(FailingJob), "ignored");
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");

        await Run(client, "long");
        Assert.True(await BlockingJob.Gate(gate).Started.WaitAsync(TimeSpan.FromSeconds(15)));
        var running = await (await host.Scheduler()).GetCurrentlyExecutingJobs();
        var fireInstanceId = running.Single(c => c.JobDetail.Key.Name == "long").FireInstanceId;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"api/jobs/DEFAULT/long/runs/{fireInstanceId}/cancel", null)).StatusCode);
        await Run(client, "ignored");
        await Task.Delay(500);
        await Run(client, "broken");

        await sink.WaitFor(1);
        await Task.Delay(500);
        Assert.Equal("broken", Assert.Single(sink.Alerts).JobName);
    }

    [Fact]
    public async Task Alerts_are_posted_to_Slack_Teams_and_JSON_webhooks()
    {
        var handler = new RecordingHandler();
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(services: s =>
        {
            Alerts(sink, o =>
            {
                o.DashboardUrl = "https://ops.example/jobs/";
                o.Webhooks.Add(new CronlightWebhook { Url = "https://hooks.example/json" });
                o.Webhooks.Add(new CronlightWebhook { Url = "https://hooks.example/slack", Format = CronlightWebhookFormat.Slack });
                o.Webhooks.Add(new CronlightWebhook { Url = "https://hooks.example/teams", Format = CronlightWebhookFormat.Teams });
            })(s);
            s.AddHttpClient("Cronlight.Alerts").ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");
        await Run(client, "broken");
        await sink.WaitFor(1);

        var bodies = handler.Requests.ToDictionary(r => r.Url.AbsolutePath, r => JsonDocument.Parse(r.Body).RootElement);
        Assert.Equal(new[] { "/json", "/slack", "/teams" }, bodies.Keys.Order());

        var json = bodies["/json"];
        Assert.Equal("Failing", json.GetProperty("kind").GetString());
        Assert.Equal("broken", json.GetProperty("jobName").GetString());
        Assert.Equal(1, json.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal("InvalidOperationException: Boom: invoice 42 could not be sent", json.GetProperty("error").GetString());

        var slack = bodies["/slack"].GetProperty("text").GetString()!;
        Assert.Contains("*Job DEFAULT.broken failed*", slack);
        Assert.Contains("```InvalidOperationException: Boom: invoice 42 could not be sent```", slack);
        Assert.Contains("<https://ops.example/jobs/|Open Cronlight>", slack);

        var attachment = bodies["/teams"].GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("Job DEFAULT.broken failed", card.GetProperty("body")[0].GetProperty("text").GetString());
        Assert.Equal("https://ops.example/jobs/", card.GetProperty("actions")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Alerts_are_emailed_to_every_recipient()
    {
        await using var smtp = new FakeSmtpServer();
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(services: Alerts(sink, o =>
        {
            o.DashboardUrl = "https://ops.example/jobs/";
            o.Email = Email(smtp.Port);
        }));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");
        await Run(client, "broken");
        // Sinks are called after email has been sent
        await sink.WaitFor(1);

        var email = Assert.Single(smtp.Messages);
        Assert.Equal(new[] { "ops@example.com", "oncall@example.com" }, email.To);
        Assert.Equal("[Cronlight] Job DEFAULT.broken failed", email.Subject);
        Assert.Contains("Job:       DEFAULT.broken", email.Body);
        Assert.Contains("Error:\nInvalidOperationException: Boom: invoice 42 could not be sent", email.Body);
        Assert.Contains("Open Cronlight: https://ops.example/jobs/", email.Body);
    }

    [Fact]
    public async Task An_unreachable_mail_server_does_not_stop_other_alerts()
    {
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(services: Alerts(sink, o => o.Email = Email(FakeSmtpServer.ClosedPort())));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");
        await Run(client, "broken");

        // After three attempts, two seconds and four seconds apart
        await sink.WaitFor(1);
        Assert.Equal("broken", Assert.Single(sink.Alerts).JobName);
    }

    private static CronlightEmailAlertOptions Email(int port)
    {
        var email = new CronlightEmailAlertOptions { Host = "127.0.0.1", Port = port, UseTls = false, From = "Cronlight <cronlight@example.com>" };
        email.To.AddRange(["ops@example.com", "oncall@example.com"]);
        return email;
    }

    [Fact]
    public async Task A_failing_webhook_or_sink_does_not_stop_the_others()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.BadRequest };
        var sink = new RecordingAlertSink();
        await using var host = await TestHost.Start(services: s =>
        {
            s.AddSingleton<ICronlightAlertSink, ThrowingAlertSink>();
            Alerts(sink, o => o.Webhooks.Add(new CronlightWebhook { Url = "https://hooks.example/json" }))(s);
            s.AddHttpClient("Cronlight.Alerts").ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");
        await Run(client, "broken");

        await sink.WaitFor(1);
        // A 4xx is the webhook refusing the message, so it is not retried
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Invalid_settings_stop_the_app_from_starting()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHost.Start(services: s => s.AddCronlightAlerts(o => o.AfterConsecutiveFailures = 0)));
        Assert.Contains("AfterConsecutiveFailures", error.Message);

        error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHost.Start(services: s => s.AddCronlightAlerts(o => o.Webhooks.Add(new CronlightWebhook { Url = "hooks.example/x" }))));
        Assert.Contains("absolute http(s) URL", error.Message);
    }

    [Fact]
    public async Task Incomplete_email_settings_turn_email_off_without_stopping_the_app_or_other_alerts()
    {
        await using var smtp = new FakeSmtpServer();
        var sink = new RecordingAlertSink();
        // No To address, as in a configuration template not filled in yet
        await using var host = await TestHost.Start(services: Alerts(sink, o =>
            o.Email = new CronlightEmailAlertOptions { Host = "127.0.0.1", Port = smtp.Port, UseTls = false, From = "cronlight@example.com" }));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");
        await Run(client, "broken");

        await sink.WaitFor(1);
        Assert.Empty(smtp.Messages);
    }

    internal sealed class RecordingAlertSink : ICronlightAlertSink
    {
        private readonly ConcurrentQueue<CronlightAlert> alerts = new();
        private readonly SemaphoreSlim received = new(0);

        public List<CronlightAlert> Alerts => alerts.ToList();

        public Task SendAsync(CronlightAlert alert, CancellationToken cancellationToken)
        {
            alerts.Enqueue(alert);
            received.Release();
            return Task.CompletedTask;
        }

        /// <summary>Waits until at least this many alerts have arrived.</summary>
        public async Task WaitFor(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (alerts.Count < count)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await received.WaitAsync(left))
                    throw new TimeoutException($"Expected {count} alerts, got {alerts.Count}");
            }
        }
    }

    private sealed class ThrowingAlertSink : ICronlightAlertSink
    {
        public Task SendAsync(CronlightAlert alert, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("mail server down");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public ConcurrentQueue<(Uri Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }
}
