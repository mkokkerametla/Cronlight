using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quartz;
using QuartzSample.Jobs;

namespace Cronlight.Tests;

/// <summary>Secret job data stays masked wherever a run's outcome or a job's data is shown.</summary>
public class SecretMaskingTests
{
    [Fact]
    public async Task A_secret_quoted_in_a_failed_runs_error_is_masked_in_history_and_alerts()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(history: _ => { }, services: AlertTests.Alerts(sink));
        var client = host.Client();
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new
        {
            type = typeof(LeakingJob).FullName, name = "leaky", data = new { authorization = "Bearer s3cr3t-token" }
        })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/leaky/trigger", null)).StatusCode);
        var record = (await HistoryScenarios.WaitForHistory(client, "?name=leaky",
            r => r.Any(x => x.GetProperty("status").GetString() == "Failed"))).Single();
        await sink.WaitFor(1);

        Assert.Equal("FormatException: The format of value '********' is invalid.", record.GetProperty("error").GetString());
        Assert.Equal("FormatException: The format of value '********' is invalid.", Assert.Single(sink.Alerts).Error);
    }

    [Fact]
    public async Task Secrets_of_a_job_type_the_dashboard_does_not_offer_are_masked_too()
    {
        // TestHost offers only the test assembly's job types; this one was registered in code
        await using var host = await TestHost.Start();
        var job = JobBuilder.Create<PartnerCallJob>().WithIdentity("partner").StoreDurably()
            .UsingJobData("authorization", "Bearer partner-s3cr3t").Build();
        await (await host.Scheduler()).AddJob(job, replace: false);
        var client = host.Client();

        var shown = await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/partner");
        var listed = (await client.GetFromJsonAsync<JsonElement>("api/jobs/search?search=partner")).GetProperty("jobs")[0];

        Assert.Equal("********", shown.GetProperty("data").GetProperty("authorization").GetString());
        Assert.Equal("********", listed.GetProperty("data").GetProperty("authorization").GetString());
    }
}
