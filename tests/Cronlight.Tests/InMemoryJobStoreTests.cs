using System.Net.Http.Json;
using System.Text.Json;
using Cronlight.History;

namespace Cronlight.Tests;

/// <summary>
/// Hosts on Quartz's default in-memory job store, which Cronlight cannot read directly: running jobs come
/// from this node and history falls back to memory.
/// </summary>
public class InMemoryJobStoreTests
{
    [Fact]
    public async Task Running_jobs_come_from_this_node()
    {
        await using var host = await TestHost.Start(ramJobStore: true);
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        await HistoryScenarios.Create(client, typeof(BlockingJob), "busy", new { gate = host.SchedulerName });

        await client.PostAsync("api/jobs/DEFAULT/busy/trigger", null);
        Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
        try
        {
            var running = await client.GetFromJsonAsync<JsonElement>("api/running");
            Assert.Equal("local", running.GetProperty("source").GetString());
            var job = Assert.Single(running.GetProperty("jobs").EnumerateArray());
            Assert.Equal("busy", job.GetProperty("name").GetString());
            Assert.Equal(typeof(BlockingJob).FullName, job.GetProperty("jobType").GetString());
        }
        finally
        {
            gate.Release.Release();
        }
    }

    [Fact]
    public async Task History_falls_back_to_memory()
    {
        await using var host = await TestHost.Start(ramJobStore: true, history: _ => { });
        await HistoryScenarios.RecordsSuccessAndFailure(host);
    }

    [Fact]
    public async Task History_required_in_the_database_refuses_to_start_without_one()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHost.Start(ramJobStore: true, history: o => o.Storage = JobHistoryStorage.Database));

        Assert.Contains("cannot use the database", error.Message);
    }
}
