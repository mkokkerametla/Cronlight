using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;

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

    public static async Task RecordsEachRefireAttempt(TestHost host)
    {
        var client = host.Client();
        await Create(client, typeof(RefireOnceJob), "retry");
        await client.PostAsync("api/jobs/DEFAULT/retry/trigger", null);

        var records = await WaitForHistory(client, "?group=DEFAULT&name=retry", r => r.Count(x => x.GetProperty("status").GetString() != "Running") >= 2);

        // Newest first: the second attempt succeeded, the first one failed
        Assert.Equal(2, records.Count);
        Assert.Equal(("Succeeded", 1), (records[0].GetProperty("status").GetString(), records[0].GetProperty("refireCount").GetInt32()));
        Assert.Equal(("Failed", 0), (records[1].GetProperty("status").GetString(), records[1].GetProperty("refireCount").GetInt32()));
    }

    public static async Task RecordsVetoedRuns(TestHost host)
    {
        var scheduler = await host.Scheduler();
        scheduler.ListenerManager.AddTriggerListener(new VetoGroupListener(), GroupMatcher<TriggerKey>.AnyGroup());
        var client = host.Client();
        var created = await client.PostJson("api/jobs", new { type = typeof(PlainJob).FullName, name = "skipped", group = VetoGroupListener.Group });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await client.PostAsync($"api/jobs/{VetoGroupListener.Group}/skipped/trigger", null);

        var records = await WaitForHistory(client, $"?group={VetoGroupListener.Group}&name=skipped",
            r => r[0].GetProperty("status").GetString() == "Vetoed");
        var vetoed = Assert.Single(records);
        Assert.Equal(0, vetoed.GetProperty("durationMs").GetInt64());
        Assert.Single(await History(client, "?status=vetoed"));
        var summary = await client.GetFromJsonAsync<JsonElement>("api/history/summary?hours=1");
        Assert.Equal(1, summary.GetProperty("vetoed").GetInt32());
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
        await WaitForStore(store);

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

    public static async Task<List<JsonElement>> History(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<JsonElement>($"api/history{query}")).GetProperty("records").EnumerateArray().ToList();

    /// <summary>History is written in the background, so poll until it shows what the test expects.</summary>
    // The writer creates the history table in the background after startup, and this test uses the store
    // directly. Reads return nothing until then, so probe with a write (under another scheduler name) instead.
    private static async Task WaitForStore(IJobHistoryStore store)
    {
        var probe = Record("cronlight-test-probe", "probe", DateTimeOffset.UtcNow);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                await store.RecordFinished(probe, CancellationToken.None);
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
        await store.DeleteFiredBefore(probe.SchedulerName, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
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
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/history/summary")).StatusCode);
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
    public async Task Sqlite_records_each_refire_attempt()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.RecordsEachRefireAttempt(host);
    }

    [Fact]
    public async Task In_memory_records_each_refire_attempt()
    {
        await using var host = await TestHost.Start(history: InMemory);
        await HistoryScenarios.RecordsEachRefireAttempt(host);
    }

    [Fact]
    public async Task Sqlite_records_vetoed_runs()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.RecordsVetoedRuns(host);
    }

    [Fact]
    public async Task In_memory_records_vetoed_runs()
    {
        await using var host = await TestHost.Start(history: InMemory);
        await HistoryScenarios.RecordsVetoedRuns(host);
    }

    [Fact]
    public async Task A_failed_write_is_skipped_and_later_records_are_still_written()
    {
        var store = new FailsFirstStartStore();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "ok");

        await client.PostAsync("api/jobs/DEFAULT/ok/trigger", null);

        // The start record failed to write; the finish still arrives, inserted as a complete record
        var records = await HistoryScenarios.WaitForHistory(client, "", r => r[0].GetProperty("status").GetString() == "Succeeded");
        Assert.Equal("ok", Assert.Single(records).GetProperty("jobName").GetString());
        Assert.Equal(1, store.FailedStarts);
    }

    [Fact]
    public async Task Sqlite_history_table_can_be_schema_qualified()
    {
        await using var host = await TestHost.Start(history: o =>
        {
            Database(o);
            o.TableName = "main.QUALIFIED_HISTORY";
        });
        await HistoryScenarios.RecordsSuccessAndFailure(host);
        Assert.Equal(4L, await host.Database.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'QUALIFIED_HISTORY' AND name LIKE 'IDX%'"));
    }

    [Fact]
    public async Task Retention_cleanup_waits_until_the_store_is_ready()
    {
        var store = new SlowStartingStore();
        await using var host = await TestHost.Start(
            history: o => o.Retention = TimeSpan.FromDays(1),
            services: s => s.AddSingleton<IJobHistoryStore>(store));

        // Cleanup runs at startup; it must not touch the store before Initialize has finished
        await Task.Delay(500);
        Assert.Equal(0, store.Deletes);

        store.Ready.SetResult();
        await store.FirstDelete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(store.DeletedBeforeReady);
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
    public async Task History_records_each_refire_attempt(string name)
    {
        await using var host = await Start(name);
        await HistoryScenarios.RecordsEachRefireAttempt(host);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task History_table_can_be_schema_qualified(string name)
    {
        var database = Require(name);
        var schema = (string)(await database.Scalar(name switch
        {
            "PostgreSQL" => "SELECT current_schema()",
            "SQL Server" => "SELECT SCHEMA_NAME()",
            _ => "SELECT DATABASE()"
        }))!;
        var tableName = $"H_{Guid.NewGuid().ToString("N")[..8]}";
        var table = $"{schema}.{tableName}";
        try
        {
            await using var host = await TestHost.Start(database: database, history: o =>
            {
                o.Storage = JobHistoryStorage.Database;
                o.TableName = table;
            });
            await HistoryScenarios.RecordsSuccessAndFailure(host);

            // A failed CREATE INDEX would be masked: the retry finds the table and carries on without indexes
            var indexes = await database.Scalar(name switch
            {
                "PostgreSQL" => $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = '{schema}' AND tablename = '{tableName.ToLowerInvariant()}' AND indexname LIKE 'idx%'",
                "SQL Server" => $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('{table}') AND name LIKE 'IDX%'",
                _ => $"SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{tableName}' AND INDEX_NAME LIKE 'IDX%'"
            });
            Assert.Equal(4, Convert.ToInt32(indexes));
        }
        finally
        {
            await database.Execute($"DROP TABLE IF EXISTS {table}");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Editing_triggers_never_deletes_a_job_registered_by_the_host(string name)
    {
        await using var host = await Start(name);
        await EditScenarios.HostRegisteredJobSurvivesTriggerEdits(host);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Create_stores_nothing_when_a_trigger_cannot_be_scheduled(string name)
    {
        await using var host = await Start(name);
        await EditScenarios.CreateStoresNothingWhenATriggerCannotBeScheduled(host);
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

/// <summary>A custom store whose Initialize waits for the test, to check nothing uses it before then.</summary>
internal sealed class SlowStartingStore : IJobHistoryStore
{
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Deletes;
    public bool DeletedBeforeReady;

    public Task Initialize(CancellationToken cancellationToken) => Ready.Task.WaitAsync(cancellationToken);

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        DeletedBeforeReady |= !Ready.Task.IsCompleted;
        Interlocked.Increment(ref Deletes);
        FirstDelete.TrySetResult();
        return Task.FromResult(0);
    }

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<JobExecutionRecord>> Query(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobExecutionRecord>>([]);

    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new JobHistorySummary(sinceUtc, 0, 0, 0, 0));
}

/// <summary>Keeps records in a list and throws on the first start record, to check the writer carries on.</summary>
internal sealed class FailsFirstStartStore : IJobHistoryStore
{
    private readonly List<JobExecutionRecord> records = new();
    private long nextId;
    private int starts;
    public int FailedStarts;

    public Task Initialize(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref starts) == 1)
        {
            FailedStarts++;
            throw new InvalidOperationException("database unavailable");
        }
        return Upsert(record);
    }

    public Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken) => Upsert(record);

    private Task Upsert(JobExecutionRecord record)
    {
        lock (records)
        {
            var index = records.FindIndex(r => r.FireInstanceId == record.FireInstanceId && r.RefireCount == record.RefireCount);
            if (index >= 0)
                records[index] = record with { Id = records[index].Id };
            else
                records.Add(record with { Id = ++nextId });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobExecutionRecord>> Query(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken)
    {
        lock (records)
            return Task.FromResult<IReadOnlyList<JobExecutionRecord>>(records.Where(r => r.SchedulerName == schedulerName).OrderByDescending(r => r.Id).ToList());
    }

    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new JobHistorySummary(sinceUtc, 0, 0, 0, 0));

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);
}
