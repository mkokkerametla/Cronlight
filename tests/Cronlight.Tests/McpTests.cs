using System.Net;
using System.Text;
using System.Text.Json;
using Cronlight.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Quartz;

namespace Cronlight.Tests;

/// <summary>The MCP server: its tools, and that they follow the dashboard's permissions, masking and auditing.</summary>
public class McpTests
{
    private static readonly string[] ReadTools =
        ["failure_summary", "get_job", "get_run_log", "list_jobs", "list_schedulers", "preview_cron", "query_runs", "running_now", "status_summary"];

    private static readonly string[] ActionTools = ["pause_job", "resume_job", "run_again", "run_job"];

    private static Task<TestHost> Start(bool allowActions = false, string environment = "Development",
        Action<CronlightOptions>? configure = null, Action<IServiceCollection>? services = null) =>
        TestHost.Start(environment, configure, history: _ => { }, services: s =>
        {
            s.AddCronlightMcp(o => o.AllowActions = allowActions);
            services?.Invoke(s);
        }, map: app => app.MapCronlightMcp("/cronlight/mcp"));

    private static Task<McpClient> Connect(TestHost host, string? role = null, string path = "mcp")
    {
        var http = host.Client(role, asDashboard: false);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, path),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> Call(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments ?? new());
        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.False(result.IsError == true, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>The message of a tool call that failed.</summary>
    private static async Task<string> Refused(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError, $"{tool} did not fail");
        return ((TextContentBlock)result.Content[0]).Text;
    }

    [Fact]
    public async Task Only_tools_that_read_are_offered_unless_actions_are_allowed()
    {
        await using (var host = await Start())
        {
            await using var client = await Connect(host);
            var tools = await client.ListToolsAsync();
            Assert.Equal(ReadTools, tools.Select(t => t.Name).Order());
            Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
            Assert.Equal("cronlight", client.ServerInfo.Name);
        }

        await using (var host = await Start(allowActions: true))
        {
            await using var client = await Connect(host);
            var tools = await client.ListToolsAsync();
            Assert.Equal(ReadTools.Concat(ActionTools).Order(), tools.Select(t => t.Name).Order());
            Assert.All(tools.Where(t => ActionTools.Contains(t.Name)), t =>
            {
                Assert.False(t.ProtocolTool.Annotations?.ReadOnlyHint);
                Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
            });
        }
    }

    [Fact]
    public async Task The_tools_show_jobs_runs_and_failures_as_the_dashboard_does()
    {
        await using var host = await Start();
        var api = host.Client();
        Assert.Equal(HttpStatusCode.Created, (await api.PostJson("api/jobs", new
        {
            type = typeof(InvoiceJob).FullName, name = "invoices", group = "billing",
            data = new { customerId = "42", smtpPassword = "hunter2" },
            triggers = new[] { new { cronExpression = "0 0 2 * * ?", timeZone = "Europe/London" } }
        })).StatusCode);
        await HistoryScenarios.Create(api, typeof(FailingJob), "broken");
        await api.PostAsync("api/jobs/DEFAULT/broken/trigger", null);
        await HistoryScenarios.WaitForHistory(api, "?name=broken", r => r.Any(x => x.GetProperty("status").GetString() == "Failed"));
        await api.PostAsync("api/jobs/DEFAULT/broken/trigger", null);
        await HistoryScenarios.WaitForHistory(api, "?name=broken&status=Failed", r => r.Count == 2);
        await using var client = await Connect(host);

        var list = await Call(client, "list_jobs", new() { ["search"] = "invoices" });
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(2, list.GetProperty("counts").GetProperty("all").GetInt32());
        var trigger = list.GetProperty("jobs")[0].GetProperty("triggers")[0];
        Assert.Equal("0 0 2 * * ? (Europe/London)", trigger.GetProperty("schedule").GetString());
        Assert.Equal("Invoice job", list.GetProperty("jobs")[0].GetProperty("type").GetString());

        var job = await Call(client, "get_job", new() { ["group"] = "billing", ["name"] = "invoices" });
        Assert.Equal("********", job.GetProperty("data").GetProperty("smtpPassword").GetString());
        Assert.Equal("42", job.GetProperty("data").GetProperty("customerId").GetString());

        var failures = await Call(client, "failure_summary");
        Assert.Equal(2, failures.GetProperty("failures").GetInt32());
        var broken = Assert.Single(failures.GetProperty("jobs").EnumerateArray());
        Assert.Equal(("broken", 2), (broken.GetProperty("jobName").GetString(), broken.GetProperty("failures").GetInt32()));
        Assert.Equal("InvalidOperationException: Boom: invoice 42 could not be sent", broken.GetProperty("lastError").GetString());
        Assert.Equal(2, broken.GetProperty("errors")[0].GetProperty("count").GetInt32());

        var runs = await Call(client, "query_runs", new() { ["status"] = "failed", ["limit"] = 1, ["sinceHours"] = 1 });
        Assert.Single(runs.GetProperty("runs").EnumerateArray());
        var older = await Call(client, "query_runs", new() { ["status"] = "Failed", ["before"] = runs.GetProperty("nextBefore").GetInt64() });
        Assert.Single(older.GetProperty("runs").EnumerateArray());
        // No more pages: tool results leave out what is null
        Assert.False(older.TryGetProperty("nextBefore", out _));

        var status = await Call(client, "status_summary");
        Assert.Equal(2, status.GetProperty("runs").GetProperty("failed").GetInt32());
        Assert.Equal(2, status.GetProperty("jobs").GetProperty("active").GetInt32());

        var cron = await Call(client, "preview_cron", new() { ["expression"] = "0 30 9 ? * MON-FRI", ["count"] = 3 });
        Assert.Equal(3, cron.GetProperty("nextFireTimes").GetArrayLength());

        Assert.Contains("Job billing.nope does not exist", await Refused(client, "get_job", new() { ["group"] = "billing", ["name"] = "nope" }));
        Assert.Contains("There is no scheduler 'other'", await Refused(client, "list_jobs", new() { ["scheduler"] = "other" }));
        Assert.Contains("Unknown status 'Exploded'", await Refused(client, "query_runs", new() { ["status"] = "Exploded" }));
    }

    [Fact]
    public async Task Actions_need_the_manage_permission_and_are_audited_as_coming_from_mcp()
    {
        var sink = new AuditTests.RecordingSink();
        await using var host = await Start(allowActions: true, "Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        }, s => s.AddSingleton<ICronlightAuditSink>(sink));
        await HistoryScenarios.Create(host.Client("admin"), typeof(FailingJob), "broken");
        var scheduler = await host.Scheduler();
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("nightly").ForJob("broken").WithCronSchedule("0 0 2 * * ?").Build());
        var job = new Dictionary<string, object?> { ["group"] = "DEFAULT", ["name"] = "broken" };

        await using (var viewer = await Connect(host, "viewer"))
        {
            Assert.Equal(1, (await Call(viewer, "list_jobs")).GetProperty("total").GetInt32());
            Assert.Contains("You cannot change jobs in scheduler", await Refused(viewer, "pause_job", job));
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey("nightly")));
        }

        await using (var admin = await Connect(host, "admin"))
        {
            Assert.Equal("Paused.", (await Call(admin, "pause_job", job)).GetProperty("message").GetString());
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey("nightly")));
            await Call(admin, "resume_job", job);
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey("nightly")));

            await Call(admin, "run_job", job);
            var failed = await HistoryScenarios.WaitForHistory(host.Client("admin"), "?name=broken&status=Failed", r => r.Count == 1);
            Assert.Equal("Started again.", (await Call(admin, "run_again", new() { ["runId"] = failed[0].GetProperty("id").GetInt64() }))
                .GetProperty("message").GetString());
            await HistoryScenarios.WaitForHistory(host.Client("admin"), "?name=broken&status=Failed", r => r.Count == 2);
        }

        var events = sink.Events.Where(e => e.Via == "mcp").ToList();
        Assert.Equal([("pause", 403), ("pause", 204), ("resume", 204), ("trigger", 204), ("trigger", 204)],
            events.Select(e => (e.Action, e.StatusCode)));
        Assert.All(events, e => Assert.Equal(("DEFAULT", "broken", host.SchedulerName), (e.JobGroup, e.JobName, e.Scheduler)));
        Assert.Equal("test", events[1].User);
        Assert.Contains("retryOf", events[^1].Changes.Keys);
    }

    [Fact]
    public async Task Requests_without_access_or_from_another_site_are_refused()
    {
        await using var host = await Start(environment: "Production", configure: o => o.ViewPolicy = "Viewers");
        var body = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_jobs","arguments":{}}}""", Encoding.UTF8, "application/json");

        async Task<HttpStatusCode> Post(HttpClient http, string? origin = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = body };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (origin != null)
                request.Headers.Add("Origin", origin);
            return (await http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Post(host.Client(asDashboard: false)));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(host.Client("viewer", asDashboard: false), "https://elsewhere.example"));
        Assert.Equal(HttpStatusCode.OK, await Post(host.Client("viewer", asDashboard: false), "http://localhost"));
    }

    [Fact]
    public async Task On_the_hosts_own_MCP_endpoint_the_tools_still_check_who_is_asking()
    {
        // The host serves its own MCP server, with no policy on it; Cronlight's tools join it
        await using var host = await TestHost.Start("Production", o => o.ViewPolicy = "Viewers",
            services: s => s.AddCronlightMcp(), map: app => app.MapMcp("/own-mcp"));

        await using (var anonymous = await Connect(host, path: "/own-mcp"))
            Assert.Contains("Access denied", await Refused(anonymous, "list_jobs", new()));
        await using (var viewer = await Connect(host, "viewer", "/own-mcp"))
            Assert.Equal(0, (await Call(viewer, "list_jobs")).GetProperty("total").GetInt32());
    }
}
