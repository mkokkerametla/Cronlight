using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cronlight.Tests;

/// <summary>
/// Clustered schedulers, on the databases Quartz can cluster on (not SQLite). Node rows are inserted only for hosts
/// that never start their scheduler: a started node recovers a stale node's row and deletes it.
/// </summary>
public class ClusterTests
{
    public static TheoryData<string> Databases => DatabaseTests.Databases;

    /// <summary>A node of the host's scheduler (or another one), as Quartz writes it: check-in in UTC ticks, interval in milliseconds.</summary>
    private static Task Node(TestHost host, string instance, TimeSpan checkedInAgo, string? scheduler = null) =>
        host.Database.Execute(
            "INSERT INTO QRTZ_SCHEDULER_STATE (SCHED_NAME, INSTANCE_NAME, LAST_CHECKIN_TIME, CHECKIN_INTERVAL) " +
            $"VALUES ('{scheduler ?? host.SchedulerName}', '{instance}', {(DateTimeOffset.UtcNow - checkedInAgo).UtcTicks}, 7500)");

    private static Task ForgetNodes(TestHost host) =>
        host.Database.Execute($"DELETE FROM QRTZ_SCHEDULER_STATE WHERE SCHED_NAME LIKE '{host.SchedulerName}%'");

    private static async Task<JsonElement> ClusterNodes(TestHost host) =>
        (await host.Client().GetFromJsonAsync<JsonElement>("api/scheduler")).GetProperty("clusterNodes");

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task A_clustered_scheduler_lists_its_node_as_this_one_and_alive(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true);
        // The node checks in as the scheduler starts; allow for that to land
        var nodes = await ClusterNodes(host);
        for (var deadline = DateTime.UtcNow.AddSeconds(15); nodes.GetArrayLength() == 0 && DateTime.UtcNow < deadline; await Task.Delay(200))
            nodes = await ClusterNodes(host);

        var node = Assert.Single(nodes.EnumerateArray());
        Assert.Equal((await host.Scheduler()).SchedulerInstanceId, node.GetProperty("instanceId").GetString());
        Assert.True(node.GetProperty("alive").GetBoolean());
        Assert.True(node.GetProperty("thisNode").GetBoolean());
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task A_dashboard_that_never_starts_the_scheduler_lists_the_nodes_that_run_it(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false);
        try
        {
            await Node(host, "worker-a", TimeSpan.FromSeconds(2));
            await Node(host, "worker-b", TimeSpan.FromMinutes(5));
            await Node(host, "elsewhere", TimeSpan.FromSeconds(2), scheduler: $"{host.SchedulerName}-other");

            var info = await host.Client().GetFromJsonAsync<JsonElement>("api/scheduler");

            Assert.False(info.GetProperty("started").GetBoolean());
            var nodes = info.GetProperty("clusterNodes").EnumerateArray()
                .ToDictionary(n => n.GetProperty("instanceId").GetString()!);
            Assert.Equal(["worker-a", "worker-b"], nodes.Keys.Order());
            Assert.True(nodes["worker-a"].GetProperty("alive").GetBoolean());
            Assert.False(nodes["worker-b"].GetProperty("alive").GetBoolean());
            Assert.InRange(nodes["worker-b"].GetProperty("secondsSinceCheckin").GetInt64(), 295, 330);
            Assert.All(nodes.Values, n => Assert.False(n.GetProperty("thisNode").GetBoolean()));
        }
        finally
        {
            await ForgetNodes(host);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task A_node_is_alive_within_the_check_in_misfire_threshold_Quartz_is_configured_with(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false,
            clusterCheckinMisfireThreshold: TimeSpan.FromMinutes(1));
        try
        {
            // Past the 7.5-second interval plus the default 7.5-second threshold, within the configured minute
            await Node(host, "worker-a", TimeSpan.FromSeconds(20));

            var node = Assert.Single((await ClusterNodes(host)).EnumerateArray());
            Assert.True(node.GetProperty("alive").GetBoolean());
        }
        finally
        {
            await ForgetNodes(host);
        }
    }

    [Fact]
    public async Task A_scheduler_that_is_not_clustered_lists_no_nodes()
    {
        await using var sqlite = await TestHost.Start();
        await using var memory = await TestHost.Start(ramJobStore: true);

        Assert.Equal(JsonValueKind.Null, (await ClusterNodes(sqlite)).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ClusterNodes(memory)).ValueKind);
    }

    private static async Task<HealthReportEntry> Health(TestHost host) =>
        (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["cronlight"];

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Checking_the_cluster_a_host_that_never_starts_the_scheduler_is_healthy_while_a_node_runs_it(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false,
            services: s => s.AddHealthChecks().AddCronlight(checkCluster: true));
        try
        {
            await Node(host, "worker-a", TimeSpan.FromSeconds(2));
            var running = await Health(host);
            Assert.Equal(HealthStatus.Healthy, running.Status);
            Assert.Equal("is running on 1 node", running.Data[host.SchedulerName]);

            await ForgetNodes(host);
            await Node(host, "worker-a", TimeSpan.FromMinutes(5));
            var down = await Health(host);
            Assert.Equal(HealthStatus.Degraded, down.Status);
            Assert.Equal($"{host.SchedulerName} has no running nodes", down.Description);
        }
        finally
        {
            await ForgetNodes(host);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Without_checking_the_cluster_a_scheduler_this_host_never_started_is_degraded(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false,
            services: s => s.AddHealthChecks().AddCronlight());
        try
        {
            await Node(host, "worker-a", TimeSpan.FromSeconds(2));
            var entry = await Health(host);
            Assert.Equal(HealthStatus.Degraded, entry.Status);
            Assert.Equal($"{host.SchedulerName} has not started", entry.Description);
        }
        finally
        {
            await ForgetNodes(host);
        }
    }
}
