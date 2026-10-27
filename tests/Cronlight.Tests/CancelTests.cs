using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Cronlight.Tests;

public class CancelTests
{
    private static async Task<string> StartLongRun(TestHost host, HttpClient client, string name)
    {
        var gate = Guid.NewGuid().ToString("N");
        await HistoryScenarios.Create(client, typeof(CancellableJob), name, new { gate });
        await client.PostAsync($"api/jobs/DEFAULT/{name}/trigger", null);
        Assert.True(await BlockingJob.Gate(gate).Started.WaitAsync(TimeSpan.FromSeconds(15)));

        var running = await client.GetFromJsonAsync<JsonElement>("api/running");
        return running.GetProperty("jobs").EnumerateArray()
            .Single(j => j.GetProperty("name").GetString() == name)
            .GetProperty("fireInstanceId").GetString()!;
    }

    [Fact]
    public async Task A_running_job_can_be_cancelled_and_history_and_audit_show_who_cancelled_it()
    {
        var sink = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var client = host.Client("admin");
        var fireInstanceId = await StartLongRun(host, client, "long");

        var response = await client.PostAsync($"api/jobs/DEFAULT/long/runs/{fireInstanceId}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var records = await HistoryScenarios.WaitForHistory(client, "?name=long",
            r => r.Any(x => x.GetProperty("status").GetString() != "Running"));
        var record = records.Single();
        Assert.Equal(("Cancelled", "Cancelled by test"), (record.GetProperty("status").GetString(), record.GetProperty("error").GetString()));
        Assert.Equal("Cancelled", Assert.Single(await HistoryScenarios.History(client, "?status=cancelled")).GetProperty("status").GetString());

        var audit = sink.Events.Last();
        Assert.Equal(("cancel", "DEFAULT", "long", true), (audit.Action, audit.JobGroup, audit.JobName, audit.Succeeded));
    }

    [Fact]
    public async Task Cancelling_a_run_again_before_it_stops_says_it_was_already_asked()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        // It does not check its cancellation token, so it keeps running until released
        var gate = BlockingJob.Gate(host.SchedulerName);
        await HistoryScenarios.Create(client, typeof(BlockingJob), "stubborn", new { gate = host.SchedulerName });
        await client.PostAsync("api/jobs/DEFAULT/stubborn/trigger", null);
        try
        {
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            var fireInstanceId = (await client.GetFromJsonAsync<JsonElement>("api/running")).GetProperty("jobs")[0]
                .GetProperty("fireInstanceId").GetString();

            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"api/jobs/DEFAULT/stubborn/runs/{fireInstanceId}/cancel", null)).StatusCode);
            var again = await client.PostAsync($"api/jobs/DEFAULT/stubborn/runs/{fireInstanceId}/cancel", null);
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
            Assert.Contains("Cancellation was already requested.", await again.Content.ReadAsStringAsync());
        }
        finally
        {
            gate.Release.Release();
        }
    }

    [Fact]
    public async Task Cancelling_a_run_that_is_not_running_is_refused()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "idle");

        var response = await client.PostAsync("api/jobs/DEFAULT/idle/runs/no-such-run/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not running", await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("api/jobs/DEFAULT/missing/runs/x/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Only_people_who_may_manage_jobs_can_cancel()
    {
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        });
        var admin = host.Client("admin");
        var fireInstanceId = await StartLongRun(host, admin, "long");

        var viewer = await host.Client("viewer").PostAsync($"api/jobs/DEFAULT/long/runs/{fireInstanceId}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        // The cross-site request rule applies as for every change
        var forged = await host.Client("admin", asDashboard: false).PostAsync($"api/jobs/DEFAULT/long/runs/{fireInstanceId}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"api/jobs/DEFAULT/long/runs/{fireInstanceId}/cancel", null)).StatusCode);
    }
}
