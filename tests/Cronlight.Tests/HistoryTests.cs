using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Cronlight.Tests;

/// <summary>History scenarios, run against every store: SQLite, in-memory and (when configured) real databases.</summary>
internal static class HistoryScenarios
{
    public static async Task RecordsSuccessAndFailure(TestHost host)
    {
        var client = host.Client();
        await Create(client, typeof(PlainJob), "ok");
        await Create(client, typeof(FailingJob), "broken");
        await client.PostAsync("api/jobs/DEFAULT/ok/trigger", null);
        await client.PostAsync("api/jobs/DEFAULT/broken/trigger", null);

        var records = await WaitForHistory(client, "", r => r.Count(x => x.GetProperty("status").GetString() != "Running") >= 2);

        var ok = records.Single(r => r.GetProperty("jobName").GetString() == "ok");
        Assert.Equal("Succeeded", ok.GetProperty("status").GetString());
        Assert.Equal(typeof(PlainJob).FullName, ok.GetProperty("jobType").GetString());
        Assert.True(ok.GetProperty("durationMs").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("error").ValueKind);

        var broken = records.Single(r => r.GetProperty("jobName").GetString() == "broken");
        Assert.Equal("Failed", broken.GetProperty("status").GetString());
        Assert.Equal("InvalidOperationException: Boom: invoice 42 could not be sent", broken.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(broken.GetProperty("instanceName").GetString()));

        var failedOnly = await History(client, "?status=failed");
        Assert.Equal("broken", Assert.Single(failedOnly).GetProperty("jobName").GetString());

        var okOnly = await History(client, "?group=DEFAULT&name=ok");
        Assert.All(okOnly, r => Assert.Equal("ok", r.GetProperty("jobName").GetString()));

        var summary = await client.GetFromJsonAsync<JsonElement>("api/history/summary?hours=1");
        Assert.Equal(1, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());

        var scheduler = await client.GetFromJsonAsync<JsonElement>("api/scheduler");
        Assert.True(scheduler.GetProperty("historyEnabled").GetBoolean());
    }

    public static async Task RecordsRunningThenCompleted(TestHost host)
    {
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        await Create(client, typeof(BlockingJob), "block", new { gate = host.SchedulerName });
        await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);

        try
        {
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            var running = await WaitForHistory(client, "?name=block", r => r.Count == 1);
            Assert.Equal("Running", running[0].GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, running[0].GetProperty("finishedTimeUtc").ValueKind);
        }
        finally
        {
            gate.Release.Release();
        }

        var done = await WaitForHistory(client, "?name=block", r => r[0].GetProperty("status").GetString() == "Succeeded");
        Assert.Single(done); // the start row was completed, not duplicated
        Assert.NotEqual(JsonValueKind.Null, done[0].GetProperty("finishedTimeUtc").ValueKind);
    }

    public static async Task PagesNewestFirst(TestHost host)
    {
        var client = host.Client();
        await Create(client, typeof(PlainJob), "paged");
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsync("api/jobs/DEFAULT/paged/trigger", null);
            await WaitForHistory(client, "?name=paged&status=Succeeded", r => r.Count == i + 1);
        }

        var first = await client.GetFromJsonAsync<JsonElement>("api/history?name=paged&limit=2");
        var page = first.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(2, page.Count);
        Assert.True(page[0].GetProperty("id").GetInt64() > page[1].GetProperty("id").GetInt64());

        var before = first.GetProperty("nextBefore").GetInt64();
        var second = await client.GetFromJsonAsync<JsonElement>($"api/history?name=paged&limit=2&before={before}");
        Assert.Single(second.GetProperty("records").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    public static async Task RetentionIsScopedToTheScheduler(TestHost host)
    {
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var otherScheduler = $"{host.SchedulerName}-other";
        await WaitForStore(store, host.SchedulerName);

        await store.RecordFinished(Record(host.SchedulerName, "old", old), CancellationToken.None);
        await store.RecordFinished(Record(host.SchedulerName, "new", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.RecordFinished(Record(otherScheduler, "other-old", old), CancellationToken.None);

        var removed = await store.DeleteFiredBefore(host.SchedulerName, DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);

        Assert.Equal(1, removed);
        var mine = await store.Query(host.SchedulerName, new JobHistoryQuery(), CancellationToken.None);
        Assert.Equal("new", Assert.Single(mine).JobName);
        Assert.Single(await store.Query(otherScheduler, new JobHistoryQuery(), CancellationToken.None));
        await store.DeleteFiredBefore(otherScheduler, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
    }

    private static JobExecutionRecord Record(string scheduler, string job, DateTimeOffset fired) => new()
    {
        SchedulerName = scheduler,
        FireInstanceId = Guid.NewGuid().ToString("N"),
        InstanceName = "test-node",
        JobName = job,
        JobGroup = "DEFAULT",
        FiredTimeUtc = fired,
        FinishedTimeUtc = fired.AddSeconds(1),
        DurationMs = 1000,
        Status = JobExecutionStatus.Succeeded
    };

    public static async Task Create(HttpClient client, Type type, string name, object? data = null)
    {
        var response = await client.PostJson("api/jobs", new { type = type.FullName, name, data });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<List<JsonElement>> History(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<JsonElement>($"api/history{query}")).GetProperty("records").EnumerateArray().ToList();

    /// <summary>History is written in the background, so poll until it shows what the test expects.</summary>
    // The writer creates the history table in the background after startup; this test uses the store directly
    private static async Task WaitForStore(IJobHistoryStore store, string schedulerName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                await store.Query(schedulerName, new JobHistoryQuery(), CancellationToken.None);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    public static async Task<List<JsonElement>> WaitForHistory(HttpClient client, string query, Func<List<JsonElement>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var records = await History(client, query);
            if (records.Count > 0 && done(records) || DateTime.UtcNow > deadline)
                return done(records) ? records : throw new TimeoutException($"History never matched; last saw {records.Count} records");
            await Task.Delay(100);
        }
    }
}

public class HistoryTests
{
    private static readonly Action<JobHistoryOptions> Database = o => o.Storage = JobHistoryStorage.Database;
    private static readonly Action<JobHistoryOptions> InMemory = o => o.Storage = JobHistoryStorage.InMemory;

    [Fact]
    public async Task History_is_off_unless_registered()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/history")).StatusCode);
        var scheduler = await client.GetFromJsonAsync<JsonElement>("api/scheduler");
        Assert.False(scheduler.GetProperty("historyEnabled").GetBoolean());
    }

    [Fact]
    public async Task Sqlite_records_success_and_failure()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.RecordsSuccessAndFailure(host);
    }

    [Fact]
    public async Task Sqlite_records_running_then_completed()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.RecordsRunningThenCompleted(host);
    }

    [Fact]
    public async Task Sqlite_pages_newest_first()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.PagesNewestFirst(host);
    }

    // Automatic cleanup is off in retention tests: its startup pass would race the test's own deletes
    [Fact]
    public async Task Sqlite_retention_is_scoped_to_the_scheduler()
    {
        await using var host = await TestHost.Start(history: o =>
        {
            Database(o);
            o.Retention = null;
        });
        await HistoryScenarios.RetentionIsScopedToTheScheduler(host);
    }

    [Fact]
    public async Task In_memory_records_success_and_failure()
    {
        await using var host = await TestHost.Start(history: InMemory);
        await HistoryScenarios.RecordsSuccessAndFailure(host);
    }

    [Fact]
    public async Task In_memory_records_running_then_completed()
    {
        await using var host = await TestHost.Start(history: InMemory);
        await HistoryScenarios.RecordsRunningThenCompleted(host);
    }

    [Fact]
    public async Task In_memory_retention_is_scoped_to_the_scheduler()
    {
        await using var host = await TestHost.Start(history: o =>
        {
            InMemory(o);
            o.Retention = null;
        });
        await HistoryScenarios.RetentionIsScopedToTheScheduler(host);
    }

    [Fact]
    public async Task Unknown_status_filter_is_rejected()
    {
        await using var host = await TestHost.Start(history: Database);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client().GetAsync("api/history?status=Exploded")).StatusCode);
    }

    [Fact]
    public async Task Missing_table_with_CreateTable_off_does_not_stop_jobs_or_shutdown()
    {
        var host = await TestHost.Start(history: o =>
        {
            o.Storage = JobHistoryStorage.Database;
            o.CreateTable = false;
        });
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        await HistoryScenarios.Create(client, typeof(BlockingJob), "still-runs", new { gate = host.SchedulerName });

        await client.PostAsync("api/jobs/DEFAULT/still-runs/trigger", null);

        Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
        gate.Release.Release();
        var history = await client.GetAsync("api/history");
        Assert.Equal(HttpStatusCode.InternalServerError, history.StatusCode);
        Assert.Equal("application/problem+json", history.Content.Headers.ContentType!.MediaType);

        // The writer is still retrying initialization; stopping must not wait for the host's shutdown timeout
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Error_messages_are_truncated()
    {
        await using var host = await TestHost.Start(history: o =>
        {
            o.Storage = JobHistoryStorage.InMemory;
            o.MaxErrorLength = 20;
        });
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(FailingJob), "long-error");
        await client.PostAsync("api/jobs/DEFAULT/long-error/trigger", null);

        var records = await HistoryScenarios.WaitForHistory(client, "?status=Failed", r => r.Count == 1);
        Assert.Equal(20, records[0].GetProperty("error").GetString()!.Length);
    }
}

/// <summary>
/// The same scenarios on real databases, plus the core dashboard flow. Skipped unless the connection
/// string variables are set (QUARTZ_TEST_POSTGRES, QUARTZ_TEST_SQLSERVER, QUARTZ_TEST_MYSQL).
/// </summary>
public class DatabaseTests
{
    public static TheoryData<string> Databases => new() { "PostgreSQL", "SQL Server", "MySQL" };

    private static TestDatabase Require(string name)
    {
        var database = TestDatabase.FromEnvironment(name);
        // CI sets this so a missing connection string fails loudly instead of skipping the database
        if (database == null && Environment.GetEnvironmentVariable("CRONLIGHT_REQUIRE_DATABASES") == "true")
            Assert.Fail($"{name} connection string is not set but CRONLIGHT_REQUIRE_DATABASES=true");
        Skip.If(database == null, $"{name} connection string not set");
        return database!;
    }

    private static Task<TestHost> Start(string name, bool cleanup = true) =>
        TestHost.Start(database: Require(name), history: o =>
        {
            o.Storage = JobHistoryStorage.Database;
            if (!cleanup)
                o.Retention = null;
        });

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task History_records_success_and_failure(string name)
    {
        await using var host = await Start(name);
        await HistoryScenarios.RecordsSuccessAndFailure(host);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task History_and_running_view_while_a_job_runs(string name)
    {
        await using var host = await Start(name);
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        await HistoryScenarios.Create(client, typeof(BlockingJob), "block", new { gate = host.SchedulerName });
        await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);

        try
        {
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            var running = await client.GetFromJsonAsync<JsonElement>("api/running");
            Assert.Equal("cluster", running.GetProperty("source").GetString());
            Assert.Single(running.GetProperty("jobs").EnumerateArray());
        }
        finally
        {
            gate.Release.Release();
        }

        await HistoryScenarios.WaitForHistory(client, "?name=block", r => r[0].GetProperty("status").GetString() == "Succeeded");
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task History_pages_newest_first(string name)
    {
        await using var host = await Start(name);
        await HistoryScenarios.PagesNewestFirst(host);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task History_retention_is_scoped_to_the_scheduler(string name)
    {
        await using var host = await Start(name, cleanup: false);
        await HistoryScenarios.RetentionIsScopedToTheScheduler(host);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Dashboard_create_edit_pause_delete(string name)
    {
        await using var host = await Start(name);
        var client = host.Client();
        var created = await client.PostJson("api/jobs", new
        {
            type = typeof(InvoiceJob).FullName,
            name = "inv",
            data = new Dictionary<string, string> { ["customerId"] = "1", ["smtpPassword"] = "hunter2" },
            triggers = new[] { new { name = "t", cronExpression = "0 0 9 * * ?", timeZone = (string?)"America/Chicago" } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/jobs/DEFAULT/inv/pause", null)).StatusCode);

        var edit = await client.PutJson("api/jobs/DEFAULT/inv", new
        {
            data = new Dictionary<string, string> { ["customerId"] = "2", ["smtpPassword"] = "********" },
            triggers = new[] { new { name = "t", cronExpression = "0 30 8 * * ?", timeZone = (string?)"Europe/London" } }
        });
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var scheduler = await host.Scheduler();
        var detail = await scheduler.GetJobDetail(new JobKey("inv"));
        Assert.Equal("2", detail!.JobDataMap.GetString("customerId"));
        Assert.Equal("hunter2", detail.JobDataMap.GetString("smtpPassword"));
        Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey("t", "DEFAULT")));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("api/jobs/DEFAULT/inv")).StatusCode);
        Assert.False(await scheduler.CheckExists(new JobKey("inv")));
    }
}
