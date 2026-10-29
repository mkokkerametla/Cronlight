using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Cronlight.Tests;

public class SecretReferenceTests
{
    private static readonly string Echo = typeof(SecretEchoJob).FullName!;

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["JobSecrets:Smtp:Password"] = "hunter2",
        ["ConnectionStrings:Main"] = "Server=db;Password=outside-the-section",
    };

    private static Task<TestHost> Start(bool enabled = true, Action<IServiceCollection>? services = null) =>
        TestHost.Start(configure: o => o.SecretsSection = enabled ? "JobSecrets" : null, settings: Settings,
            history: _ => { }, services: services);

    private static async Task<HttpResponseMessage> CreateEcho(HttpClient client, string name, object data) =>
        await client.PostJson("api/jobs", new { type = Echo, name, data });

    [Fact]
    public async Task A_reference_is_stored_and_shown_as_its_name_and_resolved_only_when_the_job_runs()
    {
        var sink = new AuditTests.RecordingSink();
        await using var host = await Start(services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var client = host.Client();
        var run = Guid.NewGuid().ToString("N");

        var created = await CreateEcho(client, "mail", new { run, smtpPassword = "secret:Smtp:Password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The database and the API only ever see the name
        var stored = await (await host.Scheduler()).GetJobDetail(new JobKey("mail"));
        Assert.Equal("secret:Smtp:Password", stored!.JobDataMap.GetString("smtpPassword"));
        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/mail");
        Assert.Equal("secret:Smtp:Password", job.GetProperty("data").GetProperty("smtpPassword").GetString());
        Assert.Equal(new CronlightAuditChange(null, "secret:Smtp:Password"), sink.Events.Single().Changes["data.smtpPassword"]);

        await client.PostAsync("api/jobs/DEFAULT/mail/trigger", null);
        await HistoryScenarios.WaitForHistory(client, "?name=mail", r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded"));

        Assert.Equal("hunter2", SecretEchoJob.Seen[run].Password);
        // The run's copy was changed, not the stored job
        stored = await (await host.Scheduler()).GetJobDetail(new JobKey("mail"));
        Assert.Equal("secret:Smtp:Password", stored!.JobDataMap.GetString("smtpPassword"));
    }

    [Fact]
    public async Task Editing_a_job_keeps_its_reference_when_sent_back_unchanged()
    {
        await using var host = await Start();
        var client = host.Client();
        await CreateEcho(client, "mail", new { run = "x", smtpPassword = "secret:Smtp:Password" });

        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/mail");
        var data = job.GetProperty("data").Deserialize<Dictionary<string, string>>()!;
        data["run"] = "y";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/mail", new { data })).StatusCode);

        var stored = await (await host.Scheduler()).GetJobDetail(new JobKey("mail"));
        Assert.Equal("secret:Smtp:Password", stored!.JobDataMap.GetString("smtpPassword"));
    }

    [Theory]
    [InlineData("secret:Smtp:Missing", "No secret named 'Smtp:Missing' is configured")]
    [InlineData("secret:", "is not a valid secret reference")]
    [InlineData("secret:Smtp::Password", "is not a valid secret reference")]
    // Names are read inside the section only, so other configuration cannot be reached
    [InlineData("secret:../ConnectionStrings:Main", "is not a valid secret reference")]
    public async Task References_that_cannot_be_resolved_are_refused_when_saving(string reference, string error)
    {
        await using var host = await Start();
        var client = host.Client();

        var created = await CreateEcho(client, "mail", new { run = "x", smtpPassword = reference });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains(error, await created.Content.ReadAsStringAsync());

        await CreateEcho(client, "ok", new { run = "x", smtpPassword = "secret:Smtp:Password" });
        var updated = await client.PutJson("api/jobs/DEFAULT/ok", new { data = new { run = "x", smtpPassword = reference } });
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Contains(error, await updated.Content.ReadAsStringAsync());

        // Nor can a run with different data use one
        var run = await client.PostJson("api/jobs/DEFAULT/ok/trigger", new { data = new { smtpPassword = reference } });
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.Contains(error, await run.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_a_secrets_section_references_are_refused_and_secrets_stay_masked()
    {
        await using var host = await Start(enabled: false);
        var client = host.Client();

        var created = await CreateEcho(client, "mail", new { run = "x", smtpPassword = "secret:Smtp:Password" });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("Secret references are not enabled", await created.Content.ReadAsStringAsync());

        // A value that only looks like a reference (e.g. stored by code) is still treated as a secret
        var scheduler = await host.Scheduler();
        await scheduler.AddJob(JobBuilder.Create<SecretEchoJob>().WithIdentity("coded").StoreDurably()
            .UsingJobData("smtpPassword", "secret:Smtp:Password").Build(), replace: false);
        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/coded");
        Assert.Equal("********", job.GetProperty("data").GetProperty("smtpPassword").GetString());

        var info = await client.GetFromJsonAsync<JsonElement>("api/scheduler");
        Assert.False(info.GetProperty("secretReferences").GetBoolean());
    }

    [Fact]
    public async Task Only_sensitive_values_are_resolved()
    {
        await using var host = await Start();
        var client = host.Client();
        var run = Guid.NewGuid().ToString("N");

        // endpoint is a plain parameter: a reference there stays text, so it cannot leak a secret into a URL or body
        await CreateEcho(client, "mail", new { run, smtpPassword = "plain-password", endpoint = "secret:Smtp:Password" });
        await client.PostAsync("api/jobs/DEFAULT/mail/trigger", null);
        await HistoryScenarios.WaitForHistory(client, "?name=mail", r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded"));

        Assert.Equal(("plain-password", "secret:Smtp:Password"), SecretEchoJob.Seen[run]);
        var job = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/mail");
        Assert.Equal("********", job.GetProperty("data").GetProperty("smtpPassword").GetString());
    }

    [Fact]
    public async Task A_run_whose_secret_is_missing_is_vetoed_with_the_reason_in_history()
    {
        await using var host = await Start();
        var client = host.Client();
        var run = Guid.NewGuid().ToString("N");

        // Saved by code, or the secret was removed from configuration after the job was saved
        var scheduler = await host.Scheduler();
        await scheduler.AddJob(JobBuilder.Create<SecretEchoJob>().WithIdentity("mail").StoreDurably()
            .UsingJobData("run", run).UsingJobData("smtpPassword", "secret:Smtp:Gone").Build(), replace: false);
        await client.PostAsync("api/jobs/DEFAULT/mail/trigger", null);

        var records = await HistoryScenarios.WaitForHistory(client, "?name=mail", r => r.Any(x => x.GetProperty("status").GetString() == "Vetoed"));
        Assert.Equal("smtpPassword: No secret named 'Smtp:Gone' is configured (configuration key JobSecrets:Smtp:Gone).",
            records.Single().GetProperty("error").GetString());
        Assert.False(SecretEchoJob.Seen.ContainsKey(run));

        var info = await client.GetFromJsonAsync<JsonElement>("api/scheduler");
        Assert.True(info.GetProperty("secretReferences").GetBoolean());
    }

    /// <summary>
    /// A host whose own scheduler reads JobSecrets, as in the tests above, and whose second scheduler, billing, has a section
    /// of its own, BillingSecrets, as one a team that manages only billing's jobs would be given.
    /// </summary>
    private static Task<TestHost> StartWithBilling(string billing, bool dashboardSection = true) =>
        TestHost.Start(configure: o =>
        {
            o.SecretsSection = dashboardSection ? "JobSecrets" : null;
            o.Schedulers[billing].SecretsSection = "BillingSecrets";
        }, settings: new(Settings) { ["BillingSecrets:Invoices:Password"] = "billing-hunter2" }, history: _ => { });

    private static string BillingName() => $"billing-{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task A_scheduler_with_its_own_secrets_section_cannot_use_the_dashboards()
    {
        var name = BillingName();
        await using var host = await StartWithBilling(name);
        var billing = await SchedulerTests.AddScheduler(host, name: name);
        try
        {
            var client = host.Client();
            var other = $"?scheduler={billing.SchedulerName}";
            const string error = "No secret named 'Smtp:Password' is configured (configuration key BillingSecrets:Smtp:Password)";

            var created = await client.PostJson($"api/jobs{other}", new { type = Echo, name = "mail", data = new { run = "x", smtpPassword = "secret:Smtp:Password" } });
            Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
            Assert.Contains(error, await created.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Created, (await client.PostJson($"api/jobs{other}",
                new { type = Echo, name = "mail", data = new { run = "x", smtpPassword = "secret:Invoices:Password" } })).StatusCode);
            var updated = await client.PutJson($"api/jobs/DEFAULT/mail{other}", new { data = new { smtpPassword = "secret:Smtp:Password" } });
            Assert.Contains(error, await updated.Content.ReadAsStringAsync());
            var run = await client.PostJson($"api/jobs/DEFAULT/mail/trigger{other}", new { data = new { smtpPassword = "secret:Smtp:Password" } });
            Assert.Contains(error, await run.Content.ReadAsStringAsync());
            // Nor the other way round
            var own = await CreateEcho(client, "mail", new { run = "x", smtpPassword = "secret:Invoices:Password" });
            Assert.Contains("configuration key JobSecrets:Invoices:Password", await own.Content.ReadAsStringAsync());

            // A job saved by code is held to it when it runs
            var coded = Guid.NewGuid().ToString("N");
            await billing.AddJob(JobBuilder.Create<SecretEchoJob>().WithIdentity("coded").StoreDurably()
                .UsingJobData("run", coded).UsingJobData("smtpPassword", "secret:Smtp:Password").Build(), replace: false);
            await client.PostAsync($"api/jobs/DEFAULT/coded/trigger{other}", null);
            var records = await HistoryScenarios.WaitForHistory(client, $"{other}&name=coded", r => r.Any(x => x.GetProperty("status").GetString() == "Vetoed"));
            Assert.Equal($"smtpPassword: {error}.", records.Single().GetProperty("error").GetString());
            Assert.False(SecretEchoJob.Seen.ContainsKey(coded));
        }
        finally
        {
            await billing.Shutdown(waitForJobsToComplete: true);
        }
    }

    [Fact]
    public async Task A_scheduler_with_its_own_secrets_section_has_references_when_the_dashboard_does_not()
    {
        var name = BillingName();
        await using var host = await StartWithBilling(name, dashboardSection: false);
        var billing = await SchedulerTests.AddScheduler(host, name: name);
        try
        {
            var client = host.Client();
            var other = $"?scheduler={billing.SchedulerName}";
            var run = Guid.NewGuid().ToString("N");

            Assert.Equal(HttpStatusCode.Created, (await client.PostJson($"api/jobs{other}",
                new { type = Echo, name = "mail", data = new { run, smtpPassword = "secret:Invoices:Password" } })).StatusCode);
            var job = await client.GetFromJsonAsync<JsonElement>($"api/jobs/DEFAULT/mail{other}");
            Assert.Equal("secret:Invoices:Password", job.GetProperty("data").GetProperty("smtpPassword").GetString());
            await client.PostAsync($"api/jobs/DEFAULT/mail/trigger{other}", null);
            await HistoryScenarios.WaitForHistory(client, $"{other}&name=mail", r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded"));
            Assert.Equal("billing-hunter2", SecretEchoJob.Seen[run].Password);

            Assert.True((await client.GetFromJsonAsync<JsonElement>($"api/scheduler{other}")).GetProperty("secretReferences").GetBoolean());
            Assert.False((await client.GetFromJsonAsync<JsonElement>("api/scheduler")).GetProperty("secretReferences").GetBoolean());
            Assert.Contains("Secret references are not enabled",
                await (await CreateEcho(client, "mail", new { run = "x", smtpPassword = "secret:Smtp:Password" })).Content.ReadAsStringAsync());
        }
        finally
        {
            await billing.Shutdown(waitForJobsToComplete: true);
        }
    }
}
