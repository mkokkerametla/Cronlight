using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Cronlight.Tests;

public class GroupPauseTests
{
    private static readonly string Plain = typeof(PlainJob).FullName!;

    private static async Task CreateScheduled(HttpClient client, string group, string name) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("api/jobs", new
        {
            type = Plain, name, group, triggers = new[] { new { cronExpression = "0 0 6 * * ?" } }
        })).StatusCode);

    private static async Task<Dictionary<string, string>> Statuses(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/jobs/search?pageSize=200")).GetProperty("jobs").EnumerateArray()
            .ToDictionary(j => $"{j.GetProperty("group").GetString()}.{j.GetProperty("name").GetString()}",
                j => j.GetProperty("triggers").EnumerateArray().All(t => t.GetProperty("state").GetString() == "Paused") ? "Paused" : "Active");

    [Fact]
    public async Task A_group_is_paused_and_resumed_without_touching_other_groups()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateScheduled(client, "billing", "invoices");
        await CreateScheduled(client, "billing", "reminders");
        await CreateScheduled(client, "reports", "daily");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/groups/billing/pause", null)).StatusCode);
        Assert.Equal(new Dictionary<string, string>
        {
            ["billing.invoices"] = "Paused", ["billing.reminders"] = "Paused", ["reports.daily"] = "Active"
        }, await Statuses(client));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/groups/billing/resume", null)).StatusCode);
        Assert.All((await Statuses(client)).Values, s => Assert.Equal("Active", s));

        var missing = await client.PostAsync("api/groups/nowhere/pause", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_whole_scheduler_is_paused_and_resumed()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await CreateScheduled(client, "billing", "invoices");
        await CreateScheduled(client, "reports", "daily");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/scheduler/pause", null)).StatusCode);
        Assert.All((await Statuses(client)).Values, s => Assert.Equal("Paused", s));
        // Quartz pauses the groups themselves, so a job added meanwhile starts paused too
        await CreateScheduled(client, "later", "added");
        Assert.Equal("Paused", (await Statuses(client))["later.added"]);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/scheduler/resume", null)).StatusCode);
        Assert.All((await Statuses(client)).Values, s => Assert.Equal("Active", s));
    }

    [Fact]
    public async Task Groups_holding_another_apps_jobs_are_refused_unless_allowed()
    {
        await using (var host = await TestHost.Start())
        {
            host.InsertForeignJob("other", "report");
            var client = host.Client();
            await CreateScheduled(client, "other", "mine");

            var group = await client.PostAsync("api/groups/other/pause", null);
            Assert.Equal(HttpStatusCode.Conflict, group.StatusCode);
            Assert.Contains("other.report", await group.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("api/scheduler/pause", null)).StatusCode);
            // Nothing was half done
            Assert.Equal("Active", (await Statuses(client))["other.mine"]);
        }

        await using (var host = await TestHost.Start(configure: o => o.AllowManagingUnloadableJobs = true))
        {
            host.InsertForeignJob("other", "report");
            var client = host.Client();
            await CreateScheduled(client, "other", "mine");
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/groups/other/pause", null)).StatusCode);
            Assert.Equal("Paused", (await Statuses(client))["other.mine"]);
        }
    }

    [Fact]
    public async Task Pausing_many_jobs_is_audited_and_needs_the_manage_policy()
    {
        var sink = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        }, services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var admin = host.Client("admin");
        await CreateScheduled(admin, "billing", "invoices");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client("viewer").PostAsync("api/groups/billing/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client("admin", asDashboard: false).PostAsync("api/scheduler/pause", null)).StatusCode);
        await admin.PostAsync("api/groups/billing/pause", null);
        await admin.PostAsync("api/groups/billing/resume", null);
        await admin.PostAsync("api/scheduler/pause", null);
        await admin.PostAsync("api/scheduler/resume", null);

        var events = sink.Events.Where(e => e.Action != "create").ToList();
        Assert.Equal(
            new[]
            {
                ("pause-all", "", false), ("pause-group", "billing", true), ("resume-group", "billing", true),
                ("pause-all", "", true), ("resume-all", "", true)
            },
            events.Select(e => (e.Action, e.JobGroup, e.Succeeded)));
        Assert.All(events, e => Assert.Equal("", e.JobName));
        Assert.Equal("1", events[1].Changes["jobs"].After);
    }
}
