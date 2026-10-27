using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>A history table from before the outcome index gets it at startup.</summary>
    public static async Task AddsTheOutcomeIndexToAnOlderTable(TestHost host)
    {
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        var drop = host.Database.Name is "SQLite" or "PostgreSQL"
            ? "DROP INDEX IDX_QRTZ_CRONLIGHT_HISTORY_STATUS"
            : "DROP INDEX IDX_QRTZ_CRONLIGHT_HISTORY_STATUS ON QRTZ_CRONLIGHT_HISTORY";
        // The writer creates the table and its indexes in the background at startup: wait for them rather than race it
        for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(100))
        {
            try
            {
                await host.Database.Execute(drop);
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }
        }

        await store.Initialize(CancellationToken.None);

        // Dropping it again works only because startup added it back
        await host.Database.Execute(drop);
        await store.Initialize(CancellationToken.None);
        // And a start with the index in place leaves it alone
        await store.Initialize(CancellationToken.None);
    }

    /// <summary>A history table from before retries gets the ATTEMPT column at startup.</summary>
    public static async Task AddsTheAttemptColumnToAnOlderTable(TestHost host)
    {
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        const string drop = "ALTER TABLE QRTZ_CRONLIGHT_HISTORY DROP COLUMN ATTEMPT";
        for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(100))
        {
            try
            {
                await host.Database.Execute(drop);
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }
        }

        await store.Initialize(CancellationToken.None);
        await store.RecordFinished(Record(host.SchedulerName, "upgraded", DateTimeOffset.UtcNow) with { Attempt = 2 }, CancellationToken.None);
        var record = Assert.Single(await store.Query(host.SchedulerName, new JobHistoryQuery { JobName = "upgraded" }, CancellationToken.None));
        Assert.Equal(2, record.Attempt);
        // A start with the column in place leaves it alone
        await store.Initialize(CancellationToken.None);
    }

    /// <summary>Fails twice and succeeds on its second retry: each failure that is retried shows as Retried.</summary>
    public static async Task RecordsRetryAttempts(TestHost host)
    {
        var client = host.Client();
        var key = Guid.NewGuid().ToString("N");
        RetriedJob.FailuresLeft[key] = 2;
        await Create(client, typeof(RetriedJob), "flaky", new { key });
        await client.PostAsync("api/jobs/DEFAULT/flaky/trigger", null);

        var records = await WaitForHistory(client, "?name=flaky", r => r.Count(x => x.GetProperty("status").GetString() != "Running") >= 3);

        Assert.Equal(new[] { ("Succeeded", 3), ("Retried", 2), ("Retried", 1) },
            records.Select(r => (r.GetProperty("status").GetString()!, r.GetProperty("attempt").GetInt32())));
        Assert.Equal(new[] { "Cronlight.Retries", "Cronlight.Retries" }, records.Take(2).Select(r => r.GetProperty("triggerGroup").GetString()));
        Assert.StartsWith("InvalidOperationException:", records[1].GetProperty("error").GetString());
        // Retried attempts are not failures: the job ended up succeeding
        var summary = await client.GetFromJsonAsync<JsonElement>("api/history/summary?hours=1");
        Assert.Equal((1, 0), (summary.GetProperty("succeeded").GetInt32(), summary.GetProperty("failed").GetInt32()));
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
        Assert.Equal("JobExecutionException: first attempt failed", records[1].GetProperty("error").GetString());
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

    internal static JobExecutionRecord Record(string scheduler, string job, DateTimeOffset fired) => new()
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
    internal static async Task WaitForStore(IJobHistoryStore store)
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
            if (records.Count > 0 && done(records))
                return records;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"History never matched; last saw {records.Count} records: " + string.Join(", ",
                    records.Select(r => $"{r.GetProperty("jobName").GetString()} {r.GetProperty("status").GetString()} " +
                        $"(refire {r.GetProperty("refireCount").GetInt32()}, trigger {r.GetProperty("triggerName").GetString()})")));
            await Task.Delay(100);
        }
    }
}

public class HistoryTests
{
    private static readonly Action<JobHistoryOptions> Database = o => o.Storage = JobHistoryStorage.Database;
    private static readonly Action<JobHistoryOptions> InMemory = o => o.Storage = JobHistoryStorage.InMemory;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queries_can_ask_for_runs_that_started_in_a_window(bool database)
    {
        await using var host = await TestHost.Start(history: database ? Database : InMemory);
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "windowed");
        await client.PostAsync("api/jobs/DEFAULT/windowed/trigger", null);
        var run = (await HistoryScenarios.WaitForHistory(client, "?name=windowed", r => r.Count == 1))[0];
        var fired = run.GetProperty("firedTimeUtc").GetDateTimeOffset();
        var store = host.Services.GetRequiredService<IJobHistoryStore>();

        async Task<int> Count(DateTimeOffset? from, DateTimeOffset? before) =>
            (await store.Query(host.SchedulerName, new JobHistoryQuery { JobName = "windowed", FiredFromUtc = from, FiredBeforeUtc = before },
                CancellationToken.None)).Count;

        Assert.Equal(1, await Count(fired, fired.AddTicks(1)));
        Assert.Equal(0, await Count(fired.AddTicks(1), null));
        Assert.Equal(0, await Count(null, fired));
        Assert.Equal(1, await Count(fired.AddHours(-1), null));
    }

    [Fact]
    public async Task A_history_table_from_an_older_version_gets_the_outcome_index()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.AddsTheOutcomeIndexToAnOlderTable(host);
    }

    [Fact]
    public async Task A_run_still_going_when_the_app_stops_is_not_left_running()
    {
        var host = await TestHost.Start(history: Database);
        var disposed = false;
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        try
        {
            await HistoryScenarios.Create(client, typeof(BlockingJob), "block", new { gate = host.SchedulerName });
            await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            await HistoryScenarios.WaitForHistory(client, "?name=block", r => r.Count == 1);
            // As Quartz does by default, the app stops without waiting for the job
            disposed = true;
            await host.DisposeAsync();
        }
        finally
        {
            gate.Release.Release();
            if (!disposed)
                await host.DisposeAsync();
        }

        var where = $"FROM QRTZ_CRONLIGHT_HISTORY WHERE SCHED_NAME = '{host.SchedulerName}' AND JOB_NAME = 'block'";
        Assert.Equal("Failed", await host.Database.Scalar($"SELECT STATUS {where}"));
        Assert.Equal("Did not finish: the app stopped while it was running.", await host.Database.Scalar($"SELECT ERROR {where}"));
    }

    [Fact]
    public async Task Closing_runs_at_shutdown_does_not_bring_back_a_row_that_is_gone()
    {
        var host = await TestHost.Start(history: Database);
        var disposed = false;
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        var where = $"FROM QRTZ_CRONLIGHT_HISTORY WHERE SCHED_NAME = '{host.SchedulerName}' AND JOB_NAME = 'block'";
        try
        {
            await HistoryScenarios.Create(client, typeof(BlockingJob), "block", new { gate = host.SchedulerName });
            await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            await HistoryScenarios.WaitForHistory(client, "?name=block", r => r.Count == 1);
            // Deleted meanwhile, as retention would
            await host.Database.Execute($"DELETE {where}");
            disposed = true;
            await host.DisposeAsync();
        }
        finally
        {
            gate.Release.Release();
            if (!disposed)
                await host.DisposeAsync();
        }

        Assert.Equal(0L, await host.Database.Scalar($"SELECT COUNT(*) {where}"));
    }

    [Fact]
    public async Task In_memory_history_closes_runs_still_going_when_the_app_stops_unless_they_are_gone()
    {
        var host = await TestHost.Start(history: InMemory);
        var disposed = false;
        var client = host.Client();
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        var (gone, going) = (BlockingJob.Gate($"{host.SchedulerName}-gone"), BlockingJob.Gate($"{host.SchedulerName}-going"));
        try
        {
            await HistoryScenarios.Create(client, typeof(BlockingJob), "gone", new { gate = $"{host.SchedulerName}-gone" });
            await client.PostAsync("api/jobs/DEFAULT/gone/trigger", null);
            Assert.True(await gone.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            await HistoryScenarios.WaitForHistory(client, "?name=gone", r => r.Count == 1);
            // Removed meanwhile, as retention would
            await store.DeleteFiredBefore(host.SchedulerName, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

            await HistoryScenarios.Create(client, typeof(BlockingJob), "going", new { gate = $"{host.SchedulerName}-going" });
            await client.PostAsync("api/jobs/DEFAULT/going/trigger", null);
            Assert.True(await going.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            await HistoryScenarios.WaitForHistory(client, "?name=going", r => r.Count == 1);
            disposed = true;
            await host.DisposeAsync();
        }
        finally
        {
            gone.Release.Release();
            going.Release.Release();
            if (!disposed)
                await host.DisposeAsync();
        }

        var record = Assert.Single(await store.Query(host.SchedulerName, new JobHistoryQuery(), CancellationToken.None));
        Assert.Equal(("going", JobExecutionStatus.Failed, "Did not finish: the app stopped while it was running."),
            (record.JobName, record.Status, record.Error));
    }

    [Fact]
    public async Task A_custom_store_is_told_of_runs_still_going_when_the_app_stops()
    {
        var store = new ListStore();
        var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var disposed = false;
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        try
        {
            await HistoryScenarios.Create(client, typeof(BlockingJob), "block", new { gate = host.SchedulerName });
            await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            await HistoryScenarios.WaitForHistory(client, "?name=block", r => r.Count == 1);
            disposed = true;
            await host.DisposeAsync();
        }
        finally
        {
            gate.Release.Release();
            if (!disposed)
                await host.DisposeAsync();
        }

        // Written as the run's end, since the store cannot say whether it is still Running
        var record = Assert.Single(store.Records);
        Assert.Equal((JobExecutionStatus.Failed, "Did not finish: the app stopped while it was running."), (record.Status, record.Error));
    }

    [Fact]
    public async Task In_memory_pages_newest_first()
    {
        await using var host = await TestHost.Start(history: InMemory);
        await HistoryScenarios.PagesNewestFirst(host);
    }

    [Fact]
    public async Task In_memory_history_keeps_the_most_recent_records_up_to_its_capacity()
    {
        await using var host = await TestHost.Start(history: o =>
        {
            InMemory(o);
            o.InMemoryCapacity = 3;
        });
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 1; i <= 5; i++)
            await store.RecordFinished(HistoryScenarios.Record(host.SchedulerName, $"run-{i}", start.AddMinutes(i)), CancellationToken.None);

        var kept = await store.Query(host.SchedulerName, new JobHistoryQuery(), CancellationToken.None);
        Assert.Equal(["run-5", "run-4", "run-3"], kept.Select(r => r.JobName));
    }

    [Fact]
    public async Task Records_older_than_the_retention_are_removed_as_cleanup_runs()
    {
        await using var host = await TestHost.Start(history: o =>
        {
            InMemory(o);
            o.Retention = TimeSpan.FromDays(1);
            o.CleanupInterval = TimeSpan.FromMilliseconds(200);
        });
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        await store.RecordFinished(HistoryScenarios.Record(host.SchedulerName, "old", DateTimeOffset.UtcNow.AddDays(-2)), CancellationToken.None);
        await store.RecordFinished(HistoryScenarios.Record(host.SchedulerName, "recent", DateTimeOffset.UtcNow.AddHours(-1)), CancellationToken.None);

        for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(100))
        {
            var names = (await store.Query(host.SchedulerName, new JobHistoryQuery(), CancellationToken.None)).Select(r => r.JobName).ToList();
            if (!names.Contains("old"))
            {
                Assert.Equal(["recent"], names);
                break;
            }
            Assert.True(DateTime.UtcNow < deadline, "The old record was never removed");
        }
    }

    [Fact]
    public async Task A_cleanup_that_fails_is_tried_again_on_the_next_pass()
    {
        var store = new ListStore { FailDelete = n => n == 1 ? new TimeoutException("Lock wait timeout exceeded") : null };
        await using var host = await TestHost.Start(history: o => o.CleanupInterval = TimeSpan.FromMilliseconds(200),
            services: s => s.AddSingleton<IJobHistoryStore>(store));

        for (var deadline = DateTime.UtcNow.AddSeconds(15); store.Deletes < 2; await Task.Delay(50))
            Assert.True(DateTime.UtcNow < deadline, "Cleanup was not tried again");
    }

    [Fact]
    public async Task Storage_that_fails_to_initialize_is_tried_again_and_then_written()
    {
        var store = new ListStore { FailInitialize = n => n == 1 ? new IOException("The database is starting up") : null };
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "waited");
        await client.PostAsync("api/jobs/DEFAULT/waited/trigger", null);

        // Tried again five seconds later; the run waits in the queue meanwhile
        for (var deadline = DateTime.UtcNow.AddSeconds(15); !store.Records.Any(r => r is { JobName: "waited", Status: JobExecutionStatus.Succeeded });)
        {
            Assert.True(DateTime.UtcNow < deadline, "The run was never written");
            await Task.Delay(100);
        }
        Assert.Equal(2, store.Initializations);
    }

    [Fact]
    public async Task Stopping_before_the_store_is_ready_does_not_wait_for_it()
    {
        var store = new SlowStartingStore();
        var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Stopping_while_the_store_is_down_does_not_wait_for_it()
    {
        var store = new ListStore();
        var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "lost");
        store.Down = true;
        await client.PostAsync("api/jobs/DEFAULT/lost/trigger", null);
        for (var deadline = DateTime.UtcNow.AddSeconds(15); store.Writes < 1; await Task.Delay(50))
            Assert.True(DateTime.UtcNow < deadline, "History was never written");

        // The writer waits between attempts while the store is down; stopping gives up on it at once
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}");
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Stopping_gives_up_on_a_store_too_slow_to_finish_within_the_shutdown_timeout()
    {
        var store = new GatedStore("slow");
        var host = await TestHost.Start(history: _ => { }, services: s =>
        {
            s.AddSingleton<IJobHistoryStore>(store);
            s.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(1));
        });
        var client = host.Client();
        try
        {
            await HistoryScenarios.Create(client, typeof(PlainJob), "slow");
            await client.PostAsync("api/jobs/DEFAULT/slow/trigger", null);
            Assert.True(await store.Held.WaitAsync(TimeSpan.FromSeconds(15)));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await host.DisposeAsync();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}");
        }
        finally
        {
            store.Release.Release();
        }
    }

    [Fact]
    public async Task An_older_history_table_the_app_may_not_change_still_records_runs()
    {
        var database = TestDatabase.Sqlite();
        await using (var creator = await TestHost.Start(database: database, history: Database))
            await HistoryScenarios.WaitForStore(creator.Services.GetRequiredService<IJobHistoryStore>());
        // As a version from before retries, run logs and the outcome index left it
        await database.Execute("ALTER TABLE QRTZ_CRONLIGHT_HISTORY DROP COLUMN ATTEMPT");
        await database.Execute("ALTER TABLE QRTZ_CRONLIGHT_HISTORY DROP COLUMN RUN_LOG");
        await database.Execute("DROP INDEX IDX_QRTZ_CRONLIGHT_HISTORY_STATUS");

        await using (var host = await TestHost.Start(database: database, history: o =>
        {
            Database(o);
            o.CreateTable = false;
        }))
            await HistoryScenarios.RecordsSuccessAndFailure(host);

        // Missing pieces are reported, not added
        Assert.Equal(0L, await database.Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('QRTZ_CRONLIGHT_HISTORY') WHERE name IN ('ATTEMPT', 'RUN_LOG')"));
        Assert.Equal(0L, await database.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IDX_QRTZ_CRONLIGHT_HISTORY_STATUS'"));
    }

    [Fact]
    public async Task A_table_name_that_is_not_a_plain_identifier_is_refused_or_kept_in_memory()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TestHost.Start(history: o =>
        {
            Database(o);
            o.TableName = "HISTORY; DROP TABLE QRTZ_TRIGGERS";
        }));
        Assert.Equal("Cronlight history cannot use the database: table name 'HISTORY; DROP TABLE QRTZ_TRIGGERS' is not a plain identifier.",
            error.Message);

        // Left to choose, history falls back to memory
        await using var host = await TestHost.Start(history: o => o.TableName = "HISTORY; DROP TABLE QRTZ_TRIGGERS");
        await HistoryScenarios.RecordsSuccessAndFailure(host);
    }

    [Fact]
    public async Task A_custom_store_that_keeps_no_logs_says_a_run_has_none()
    {
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(new ListStore()));
        var response = await host.Client().GetAsync("api/history/1/log");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("This run has no log.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_run_that_ends_while_history_drains_at_shutdown_is_recorded()
    {
        var store = new GatedStore("drain");
        var writerStopsNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await TestHost.Start(history: _ => { }, services: s =>
        {
            s.AddSingleton<IJobHistoryStore>(store);
            // Stops just before the history writer, which is registered first and so stops last
            s.Insert(0, ServiceDescriptor.Singleton<IHostedService>(new StopSignal(writerStopsNext)));
        });
        var client = host.Client();
        var gate = BlockingJob.Gate(host.SchedulerName);
        Task? stopping = null;
        try
        {
            await HistoryScenarios.Create(client, typeof(BlockingJob), "late", new { gate = host.SchedulerName });
            await HistoryScenarios.Create(client, typeof(PlainJob), "drain");
            await client.PostAsync("api/jobs/DEFAULT/late/trigger", null);
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            // The end of this run holds up the writer, as a slow database would
            await client.PostAsync("api/jobs/DEFAULT/drain/trigger", null);
            Assert.True(await store.Held.WaitAsync(TimeSpan.FromSeconds(15)));

            // The app stops without waiting for "late", which ends while history is still being written
            stopping = host.DisposeAsync().AsTask();
            await writerStopsNext.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(300);
            gate.Release.Release();
            await Task.Delay(300);
        }
        finally
        {
            store.Release.Release();
            gate.Release.Release();
            await (stopping ?? host.DisposeAsync().AsTask());
        }

        Assert.Equal(JobExecutionStatus.Succeeded, Assert.Single(store.Records, r => r.JobName == "late").Status);
    }

    [Fact]
    public async Task A_store_whose_records_cannot_be_read_back_does_not_hold_up_history()
    {
        var store = new RefusingStore("poison", readable: false);
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "poison");
        await HistoryScenarios.Create(client, typeof(PlainJob), "ok");

        await client.PostAsync("api/jobs/DEFAULT/poison/trigger", null);
        await client.PostAsync("api/jobs/DEFAULT/ok/trigger", null);

        // Its Query fails, so the dashboard cannot read it either: look in the store
        for (var deadline = DateTime.UtcNow.AddSeconds(15); !store.Records.Any(r => r is { JobName: "ok", Status: JobExecutionStatus.Succeeded });)
        {
            Assert.True(DateTime.UtcNow < deadline, "The record of the second job was never written");
            await Task.Delay(100);
        }
    }

    // In memory too, when the Quartz tables say what every node is running
    [Theory]
    [InlineData(JobHistoryStorage.Database)]
    [InlineData(JobHistoryStorage.InMemory)]
    public async Task Runs_that_no_node_is_running_any_more_are_closed_as_failed(JobHistoryStorage storage)
    {
        await using var host = await TestHost.Start(history: o =>
        {
            o.Storage = storage;
            o.UnfinishedRunCheckInterval = TimeSpan.FromMilliseconds(200);
        });
        var client = host.Client();
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        await HistoryScenarios.WaitForStore(store);
        var gate = BlockingJob.Gate(host.SchedulerName);
        await HistoryScenarios.Create(client, typeof(BlockingJob), "block", new { gate = host.SchedulerName });
        await client.PostAsync("api/jobs/DEFAULT/block/trigger", null);
        try
        {
            Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
            var running = (await client.GetFromJsonAsync<JsonElement>("api/running")).GetProperty("jobs")[0];
            var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
            JobExecutionRecord Started(string job, string fireInstanceId, int refireCount) => new()
            {
                SchedulerName = host.SchedulerName,
                FireInstanceId = fireInstanceId,
                RefireCount = refireCount,
                InstanceName = "crashed-node",
                JobName = job,
                JobGroup = "DEFAULT",
                FiredTimeUtc = hourAgo,
                Status = JobExecutionStatus.Running
            };
            // Started an hour ago, but still running: Quartz lists its fire instance id as executing. Written first, so the
            // check that closes the next one has seen this one too
            await store.RecordStarted(Started("block", running.GetProperty("fireInstanceId").GetString()!, 7), CancellationToken.None);
            // The node running this one crashed an hour ago, so Quartz has no record of it running
            await store.RecordStarted(Started("lost", "lost-run", 0), CancellationToken.None);

            var lost = await HistoryScenarios.WaitForHistory(client, "?name=lost", r => r[0].GetProperty("status").GetString() == "Failed");
            Assert.Equal("Did not finish: no end was recorded, and no node is running it any more. The node that ran it (crashed-node) probably stopped or crashed.",
                lost[0].GetProperty("error").GetString());
            Assert.Contains(await HistoryScenarios.History(client, "?name=block"),
                r => r.GetProperty("refireCount").GetInt32() == 7 && r.GetProperty("status").GetString() == "Running");
        }
        finally
        {
            gate.Release.Release();
        }
    }

    [Fact]
    public async Task A_history_table_from_an_older_version_gets_the_attempt_column()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.AddsTheAttemptColumnToAnOlderTable(host);
    }

    [Fact]
    public async Task Records_each_retry_attempt()
    {
        await using var host = await TestHost.Start(history: Database);
        await HistoryScenarios.RecordsRetryAttempts(host);
    }

    [Fact]
    public async Task History_is_off_unless_registered()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/history/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/history/1/log")).StatusCode);
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
    public async Task History_waits_while_its_table_is_unavailable_and_is_written_once_it_is_back()
    {
        await using var host = await TestHost.Start(history: Database);
        var client = host.Client();
        await HistoryScenarios.WaitForStore(host.Services.GetRequiredService<IJobHistoryStore>());
        await HistoryScenarios.Create(client, typeof(PlainJob), "during");
        var scheduler = await host.Scheduler();

        // As if the database were down: the history table cannot be read or written
        await host.Database.Execute("ALTER TABLE QRTZ_CRONLIGHT_HISTORY RENAME TO QRTZ_CRONLIGHT_HISTORY_AWAY");
        try
        {
            await client.PostAsync("api/jobs/DEFAULT/during/trigger", null);
            for (var deadline = DateTime.UtcNow.AddSeconds(15); (await scheduler.GetMetaData()).NumberOfJobsExecuted < 1;)
            {
                Assert.True(DateTime.UtcNow < deadline, "The job did not run");
                await Task.Delay(50);
            }
            // Long enough for the writer to try, and fail
            await Task.Delay(TimeSpan.FromSeconds(1.5));
        }
        finally
        {
            await host.Database.Execute("ALTER TABLE QRTZ_CRONLIGHT_HISTORY_AWAY RENAME TO QRTZ_CRONLIGHT_HISTORY");
        }

        var records = await HistoryScenarios.WaitForHistory(client, "?name=during", r => r[0].GetProperty("status").GetString() == "Succeeded");
        Assert.Single(records);
    }

    [Fact]
    public async Task A_write_refused_once_just_after_an_outage_is_tried_again()
    {
        var store = new OutageStore();
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "ok");

        await client.PostAsync("api/jobs/DEFAULT/ok/trigger", null);
        // Reads fail while the store is down, as the dashboard's would
        for (var deadline = DateTime.UtcNow.AddSeconds(15); store.Writes < 5 && DateTime.UtcNow < deadline;)
            await Task.Delay(50);

        var records = await HistoryScenarios.WaitForHistory(client, "", r => r[0].GetProperty("status").GetString() == "Succeeded");
        Assert.Single(records);
    }

    [Fact]
    public async Task A_record_the_store_keeps_refusing_does_not_hold_up_later_ones()
    {
        var store = new RefusingStore("poison");
        await using var host = await TestHost.Start(history: _ => { }, services: s => s.AddSingleton<IJobHistoryStore>(store));
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "poison");
        await HistoryScenarios.Create(client, typeof(PlainJob), "ok");

        await client.PostAsync("api/jobs/DEFAULT/poison/trigger", null);
        await client.PostAsync("api/jobs/DEFAULT/ok/trigger", null);

        var records = await HistoryScenarios.WaitForHistory(client, "", r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded"));
        Assert.Equal("ok", Assert.Single(records).GetProperty("jobName").GetString());
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
        Assert.Equal(5L, await host.Database.Scalar(
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

    internal static TestDatabase Require(string name)
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
    public async Task History_tables_from_older_versions_get_the_outcome_index(string name)
    {
        await using var host = await Start(name);
        await HistoryScenarios.AddsTheOutcomeIndexToAnOlderTable(host);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task History_tables_from_older_versions_get_the_attempt_column(string name)
    {
        await using var host = await Start(name);
        await HistoryScenarios.AddsTheAttemptColumnToAnOlderTable(host);
    }

    // Retries are triggers in the job store, written from the job's thread
    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Each_retry_attempt_is_recorded(string name)
    {
        await using var host = await Start(name);
        await HistoryScenarios.RecordsRetryAttempts(host);
    }

    // Only a cluster reads failure streaks from history, and SQLite cannot be clustered
    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Alerts_read_failure_streaks_from_history_in_a_cluster(string name)
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(database: Require(name), clustered: true,
            history: o => o.Storage = JobHistoryStorage.Database, services: AlertTests.Alerts(sink, AlertTests.StreakOptions));
        await AlertTests.AlertsOnceAfterFailuresInARow(host, sink);
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Runs_history_closed_as_unfinished_do_not_count_toward_a_clusters_alerts(string name)
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(database: Require(name), clustered: true,
            history: o => o.Storage = JobHistoryStorage.Database, services: AlertTests.Alerts(sink));
        var client = host.Client();
        var store = host.Services.GetRequiredService<IJobHistoryStore>();
        await HistoryScenarios.WaitForStore(store);
        await HistoryScenarios.Create(client, typeof(PlainJob), "deployed");
        // A run a rolling deploy cut short, which history closed: it raised no alert, so its job cannot recover from it
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.RecordFinished(new JobExecutionRecord
        {
            SchedulerName = host.SchedulerName,
            FireInstanceId = "cut-short",
            InstanceName = "old-node",
            JobName = "deployed",
            JobGroup = "DEFAULT",
            FiredTimeUtc = earlier,
            FinishedTimeUtc = earlier,
            Status = JobExecutionStatus.Failed,
            Error = "Did not finish: the app stopped while it was running."
        }, CancellationToken.None);

        await client.PostAsync("api/jobs/DEFAULT/deployed/trigger", null);
        await HistoryScenarios.WaitForHistory(client, "?name=deployed", r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded"));
        // An alert would be sent as the run ends
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(sink.Alerts);
    }

    // The upgrade is tried again on the next cleanup, here every 200 ms
    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task An_upgrade_step_that_failed_is_tried_again_later(string name)
    {
        var database = Require(name);
        var id = Guid.NewGuid().ToString("N")[..8];
        var (older, view) = ($"HB_{id}", $"HV_{id}");
        await using (var creator = await TestHost.Start(database: database, history: o =>
        {
            o.Storage = JobHistoryStorage.Database;
            o.TableName = older;
        }))
            await HistoryScenarios.WaitForStore(creator.Services.GetRequiredService<IJobHistoryStore>());
        await database.Execute($"ALTER TABLE {older} DROP COLUMN RUN_LOG");
        await database.Execute($"CREATE VIEW {view} AS SELECT * FROM {older}");
        try
        {
            await using var host = await TestHost.Start(database: database, history: o =>
            {
                o.Storage = JobHistoryStorage.Database;
                o.TableName = view;
                o.CleanupInterval = TimeSpan.FromMilliseconds(200);
            });
            await HistoryScenarios.WaitForStore(host.Services.GetRequiredService<IJobHistoryStore>());

            // What stopped the upgrade is gone: the name now belongs to the table itself, which can be changed. Swapped in
            // one step, or the history writer, trying again every 200 ms, could find no table there and create one.
            await database.Execute(name switch
            {
                "SQL Server" => $"BEGIN TRANSACTION; DROP VIEW {view}; EXEC sp_rename '{older}', '{view}'; COMMIT",
                "MySQL" => $"RENAME TABLE {view} TO {view}_OLD, {older} TO {view}",
                _ => $"BEGIN; DROP VIEW {view}; ALTER TABLE {older} RENAME TO {view}; COMMIT",
            });
            for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(100))
            {
                try
                {
                    await database.Scalar($"SELECT COUNT(RUN_LOG) FROM {view}");
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                }
            }
        }
        finally
        {
            // Whichever of these the test got to
            foreach (var drop in new[] { $"DROP VIEW {view}", $"DROP VIEW {view}_OLD", $"DROP TABLE {view}", $"DROP TABLE {older}" })
            {
                try
                {
                    await database.Execute(drop);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Job_search_reads_the_Quartz_tables(string name)
    {
        await using var host = await Start(name);
        await JobSearchScenarios.SearchesFiltersAndPages(host);
    }

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
            Assert.Equal(5, Convert.ToInt32(indexes));
        }
        finally
        {
            await database.Execute($"DROP TABLE IF EXISTS {table}");
        }
    }

    // Restarting runs the upgrade checks again, which must find the table's own indexes wherever it is
    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task A_history_table_in_another_schema_or_database_keeps_its_indexes_across_restarts(string name)
    {
        var database = Require(name);
        var tableName = $"H_{Guid.NewGuid().ToString("N")[..8]}";
        // Not where the connection points: another schema, or for SQL Server and MySQL another database
        var (setup, table, dropIndex, countIndexes) = name switch
        {
            "PostgreSQL" => ("CREATE SCHEMA IF NOT EXISTS cronlight_other", $"cronlight_other.{tableName}",
                $"DROP INDEX cronlight_other.IDX_{tableName}_STATUS",
                $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'cronlight_other' AND tablename = '{tableName.ToLowerInvariant()}' AND indexname LIKE 'idx%'"),
            "SQL Server" => ("IF DB_ID('cronlight_other') IS NULL CREATE DATABASE cronlight_other", $"cronlight_other.dbo.{tableName}",
                $"DROP INDEX IDX_{tableName}_STATUS ON cronlight_other.dbo.{tableName}",
                $"SELECT COUNT(*) FROM cronlight_other.sys.indexes WHERE object_id = OBJECT_ID('cronlight_other.dbo.{tableName}') AND name LIKE 'IDX%'"),
            _ => ("CREATE DATABASE IF NOT EXISTS cronlight_other", $"cronlight_other.{tableName}",
                $"DROP INDEX IDX_{tableName}_STATUS ON cronlight_other.{tableName}",
                $"SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'cronlight_other' AND TABLE_NAME = '{tableName}' AND INDEX_NAME LIKE 'IDX%'"),
        };
        await database.Execute(setup);
        // PostgreSQL: a table of the same name in the default schema, whose index must not be taken for this table's
        if (name == "PostgreSQL")
        {
            await database.Execute($"CREATE TABLE {tableName} (ID INT, SCHED_NAME VARCHAR(10), STATUS VARCHAR(10))");
            await database.Execute($"CREATE INDEX IDX_{tableName}_STATUS ON {tableName} (SCHED_NAME, STATUS, ID)");
        }
        try
        {
            await using var host = await TestHost.Start(database: database, history: o =>
            {
                o.Storage = JobHistoryStorage.Database;
                o.TableName = table;
            });
            await HistoryScenarios.RecordsSuccessAndFailure(host);
            var store = host.Services.GetRequiredService<IJobHistoryStore>();

            // An index lost meanwhile is added back at the next start, and a start with every index in place leaves them alone
            await database.Execute(dropIndex);
            await store.Initialize(CancellationToken.None);
            await store.Initialize(CancellationToken.None);
            Assert.Equal(5, Convert.ToInt32(await database.Scalar(countIndexes)));
        }
        finally
        {
            await database.Execute($"DROP TABLE IF EXISTS {table}");
            if (name == "PostgreSQL")
                await database.Execute($"DROP TABLE IF EXISTS {tableName}");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task An_upgrade_step_the_database_refuses_does_not_stop_history(string name)
    {
        var database = Require(name);
        var id = Guid.NewGuid().ToString("N")[..8];
        var (older, view) = ($"HB_{id}", $"HV_{id}");
        await using (var creator = await TestHost.Start(database: database, history: o =>
        {
            o.Storage = JobHistoryStorage.Database;
            o.TableName = older;
        }))
            await HistoryScenarios.WaitForStore(creator.Services.GetRequiredService<IJobHistoryStore>());
        // A table from a version before run logs, which the app cannot change: through a view it takes rows, but adding a
        // column or an index fails, as it does for an app without ALTER rights or when another node got there first
        await database.Execute($"ALTER TABLE {older} DROP COLUMN RUN_LOG");
        await database.Execute($"CREATE VIEW {view} AS SELECT * FROM {older}");
        try
        {
            await using var host = await TestHost.Start(database: database, history: o =>
            {
                o.Storage = JobHistoryStorage.Database;
                o.TableName = view;
            });
            await HistoryScenarios.RecordsSuccessAndFailure(host);
        }
        finally
        {
            await database.Execute($"DROP VIEW IF EXISTS {view}");
            await database.Execute($"DROP TABLE IF EXISTS {older}");
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

/// <summary>
/// Keeps records in a list. Its second and third writes (a run's end) find it down, reads failing too; the fourth, once
/// it is back, is refused once (a deadlock, say); later ones work.
/// </summary>
internal sealed class OutageStore : IJobHistoryStore
{
    private readonly List<JobExecutionRecord> records = new();
    private long nextId;
    private int writes;
    private volatile bool down;

    public int Writes => writes;

    public Task Initialize(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken) => Write(record);

    public Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken) => Write(record);

    private Task Write(JobExecutionRecord record)
    {
        var write = Interlocked.Increment(ref writes);
        down = write is 2 or 3;
        if (down)
            throw new IOException("Connection refused");
        if (write == 4)
            throw new InvalidOperationException("Transaction was deadlocked and has been chosen as the deadlock victim");
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
        if (down)
            throw new IOException("Connection refused");
        lock (records)
            return Task.FromResult<IReadOnlyList<JobExecutionRecord>>(records
                .Where(r => r.SchedulerName == schedulerName && (query.Status == null || r.Status == query.Status))
                .OrderByDescending(r => r.Id).Take(query.Limit).ToList());
    }

    // Down means down: every call fails
    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        down ? throw new IOException("Connection refused") : Task.FromResult(new JobHistorySummary(sinceUtc, 0, 0, 0, 0));

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Signals when the host stops it, which it does just before the services registered ahead of it.</summary>
internal sealed class StopSignal(TaskCompletionSource stopping) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.TrySetResult();
        return Task.CompletedTask;
    }
}

/// <summary>Keeps records in a list. Writing the end of one job's run waits until the test releases it.</summary>
internal sealed class GatedStore(string heldJob) : IJobHistoryStore
{
    private readonly List<JobExecutionRecord> records = new();
    private long nextId;

    public SemaphoreSlim Held { get; } = new(0);
    public SemaphoreSlim Release { get; } = new(0);

    public List<JobExecutionRecord> Records
    {
        get
        {
            lock (records)
                return records.ToList();
        }
    }

    public Task Initialize(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken) => Upsert(record);

    public async Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken)
    {
        if (record.JobName == heldJob)
        {
            Held.Release();
            await Release.WaitAsync(TimeSpan.FromSeconds(30));
        }
        await Upsert(record);
    }

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
            return Task.FromResult<IReadOnlyList<JobExecutionRecord>>(records
                .Where(r => r.SchedulerName == schedulerName && (query.Status == null || r.Status == query.Status))
                .OrderByDescending(r => r.Id).Take(query.Limit).ToList());
    }

    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new JobHistorySummary(sinceUtc, 0, 0, 0, 0));

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// Keeps records in a list, but refuses every record of one job while it otherwise works: a record that can never be
/// written. Unless readable, reading records back fails too, as a store that cannot read a newer version's rows would.
/// </summary>
internal sealed class RefusingStore(string refusedJob, bool readable = true) : IJobHistoryStore
{
    private readonly List<JobExecutionRecord> records = new();
    private long nextId;

    public List<JobExecutionRecord> Records
    {
        get
        {
            lock (records)
                return records.ToList();
        }
    }

    public Task Initialize(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken) => Write(record);

    public Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken) => Write(record);

    private Task Write(JobExecutionRecord record)
    {
        if (record.JobName == refusedJob)
            throw new InvalidOperationException("String or binary data would be truncated");
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
        if (!readable)
            throw new NotSupportedException("Unknown status 'Skipped'");
        lock (records)
            return Task.FromResult<IReadOnlyList<JobExecutionRecord>>(records
                .Where(r => r.SchedulerName == schedulerName && (query.Status == null || r.Status == query.Status))
                .OrderByDescending(r => r.Id).Take(query.Limit).ToList());
    }

    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new JobHistorySummary(sinceUtc, 0, 0, 0, 0));

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);
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

/// <summary>
/// Keeps records in a list. Each fault, given the call's number (from 1), may return an exception to throw. While Down,
/// every write and read fails, as when its database cannot be reached.
/// </summary>
internal sealed class ListStore : IJobHistoryStore
{
    private readonly List<JobExecutionRecord> records = new();
    private long nextId;
    private int initializations;
    private int writes;
    private int deletes;

    public Func<int, Exception?>? FailInitialize { get; init; }
    public Func<int, Exception?>? FailDelete { get; init; }
    public volatile bool Down;

    public int Initializations => initializations;
    public int Writes => writes;
    public int Deletes => deletes;

    public List<JobExecutionRecord> Records
    {
        get
        {
            lock (records)
                return records.ToList();
        }
    }

    public Task Initialize(CancellationToken cancellationToken) =>
        FailInitialize?.Invoke(Interlocked.Increment(ref initializations)) is { } error ? Task.FromException(error) : Task.CompletedTask;

    public Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken) => Write(record);

    public Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken) => Write(record);

    private Task Write(JobExecutionRecord record)
    {
        Interlocked.Increment(ref writes);
        if (Down)
            throw new IOException("Connection refused");
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
        if (Down)
            throw new IOException("Connection refused");
        lock (records)
            return Task.FromResult<IReadOnlyList<JobExecutionRecord>>(records
                .Where(r => r.SchedulerName == schedulerName && (query.Status == null || r.Status == query.Status))
                .OrderByDescending(r => r.Id).Take(query.Limit).ToList());
    }

    public Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        Down ? throw new IOException("Connection refused") : Task.FromResult(new JobHistorySummary(sinceUtc, 0, 0, 0, 0));

    public Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        FailDelete?.Invoke(Interlocked.Increment(ref deletes)) is { } error ? Task.FromException<int>(error) : Task.FromResult(0);
}
