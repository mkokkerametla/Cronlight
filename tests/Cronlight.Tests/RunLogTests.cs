using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight.Tests;

/// <summary>What runs log through ILogger, kept with them in history.</summary>
public class RunLogTests
{
    /// <summary>
    /// A real logger factory, which TestHost otherwise replaces with one that only writes errors to a file in CI. Never
    /// disposed: Quartz's static logging may hold on to it after this host has gone.
    /// </summary>
    private sealed class KeptLoggerFactory(IEnumerable<ILoggerProvider> providers) : ILoggerFactory
    {
        private readonly LoggerFactory inner = new(providers.Append(ErrorFileLoggerFactory.Instance), new LoggerFilterOptions { MinLevel = LogLevel.Trace });
        public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);
        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
        public void Dispose() { }
    }

    private static Task<TestHost> Start(JobHistoryStorage storage, Action<JobHistoryOptions>? history = null) =>
        TestHost.Start(
            history: o =>
            {
                o.Storage = storage;
                history?.Invoke(o);
            },
            services: s => s.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new KeptLoggerFactory(sp.GetServices<ILoggerProvider>()))));

    private static async Task<JsonElement> RunAndWait(TestHost host, string name, object data)
    {
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(LoggingJob), name, data);
        await client.PostAsync($"api/jobs/DEFAULT/{name}/trigger", null);
        var records = await HistoryScenarios.WaitForHistory(client, $"?name={name}", r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded"));
        return Assert.Single(records);
    }

    private static async Task<List<JsonElement>> Log(TestHost host, JsonElement record) =>
        (await host.Client().GetFromJsonAsync<JsonElement>($"api/history/{record.GetProperty("id").GetInt64()}/log")).EnumerateArray().ToList();

    [Theory]
    [InlineData(JobHistoryStorage.Database)]
    [InlineData(JobHistoryStorage.InMemory)]
    public async Task What_a_run_logs_is_kept_with_it_and_its_secrets_are_masked(JobHistoryStorage storage)
    {
        await using var host = await Start(storage);

        var record = await RunAndWait(host, "caller", new { customer = "acme", apiToken = "tok-12345" });

        Assert.True(record.GetProperty("hasLog").GetBoolean());
        // The list says there is a log, and leaves the log itself out
        Assert.False(record.TryGetProperty("log", out _));
        var log = await Log(host, record);
        Assert.Equal(new[]
        {
            ("Information", "Calling acme with token ********"),
            // Logged from work the job started, with the exception's type and message but no stack trace
            ("Warning", "Retrying a call (TimeoutException: upstream slow)")
        }, log.Select(e => (e.GetProperty("level").GetString()!, e.GetProperty("message").GetString()!)));
        Assert.All(log, e => Assert.Equal(typeof(LoggingJob).FullName, e.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task A_long_log_keeps_its_first_entries_and_says_how_many_were_left_out()
    {
        await using var host = await Start(JobHistoryStorage.Database, o => o.MaxLogEntries = 3);

        var log = await Log(host, await RunAndWait(host, "chatty", new { customer = "acme", lines = "5" }));

        Assert.Equal(4, log.Count);
        Assert.Equal("Line 1", log[2].GetProperty("message").GetString());
        Assert.StartsWith("4 more log entries were not kept", log[3].GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_secret_cut_by_the_length_limit_is_masked_before_it_is_cut()
    {
        // "Calling acme with token " is 24 characters, so the limit cuts through the token
        await using var host = await Start(JobHistoryStorage.Database, o => o.MaxLogMessageLength = 30);

        var log = await Log(host, await RunAndWait(host, "cut", new { customer = "acme", apiToken = "tok-12345" }));

        var first = log[0].GetProperty("message").GetString()!;
        Assert.Equal("Calling acme with token *****…", first);
        Assert.DoesNotContain("tok-", first);
    }

    [Fact]
    public async Task What_work_left_running_logs_after_the_run_is_not_kept()
    {
        await using var host = await Start(JobHistoryStorage.Database);

        var record = await RunAndWait(host, "lingering", new { customer = "acme", linger = "true" });
        // The work the run left behind has logged by now
        Assert.True(LoggingJob.Lingered.Wait(TimeSpan.FromSeconds(10)));

        Assert.DoesNotContain(await Log(host, record), e => e.GetProperty("message").GetString() == "Still going after the run");
    }

    [Fact]
    public async Task Runs_that_log_nothing_or_with_capture_off_have_no_log()
    {
        await using (var host = await Start(JobHistoryStorage.Database))
        {
            var client = host.Client();
            await HistoryScenarios.Create(client, typeof(PlainJob), "quiet");
            await client.PostAsync("api/jobs/DEFAULT/quiet/trigger", null);
            var quiet = Assert.Single(await HistoryScenarios.WaitForHistory(client, "?name=quiet",
                r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded")));
            Assert.False(quiet.GetProperty("hasLog").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/history/{quiet.GetProperty("id").GetInt64()}/log")).StatusCode);
        }

        await using (var host = await Start(JobHistoryStorage.Database, o => o.CaptureLogs = false))
        {
            var record = await RunAndWait(host, "uncaptured", new { customer = "acme" });
            Assert.False(record.GetProperty("hasLog").GetBoolean());
        }
    }

    [Fact]
    public async Task A_scheduler_built_in_code_keeps_logs_once_its_job_factory_is_wrapped()
    {
        await using var host = await Start(JobHistoryStorage.InMemory);
        var extra = await new Quartz.Impl.StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"extra-{Guid.NewGuid():N}",
            ["quartz.threadPool.maxConcurrency"] = "1",
        }).GetScheduler();
        try
        {
            var wrapped = host.Services.GetRequiredService<Quartz.Spi.IJobFactory>().WithCronlightRunLogs(host.Services);
            // Wrapping again is harmless
            Assert.Same(wrapped, wrapped.WithCronlightRunLogs(host.Services));
            extra.JobFactory = wrapped;
            await extra.UseCronlight(host.Services);
            await extra.Start();
            await extra.ScheduleJob(JobBuilder.Create<LoggingJob>().WithIdentity("logs").UsingJobData("customer", "acme").Build(),
                TriggerBuilder.Create().StartNow().Build());

            var client = host.Client();
            var query = $"?scheduler={extra.SchedulerName}&name=logs";
            var record = Assert.Single(await HistoryScenarios.WaitForHistory(client, query,
                r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded")));
            Assert.True(record.GetProperty("hasLog").GetBoolean());
            var log = await client.GetFromJsonAsync<JsonElement>($"api/history/{record.GetProperty("id").GetInt64()}/log?scheduler={extra.SchedulerName}");
            Assert.StartsWith("Calling acme", log[0].GetProperty("message").GetString());
        }
        finally
        {
            await extra.Shutdown(waitForJobsToComplete: true);
        }
    }

    [Theory]
    [InlineData(JobHistoryStorage.Database)]
    [InlineData(JobHistoryStorage.InMemory)]
    public async Task A_running_jobs_log_so_far_can_be_read_until_it_finishes(JobHistoryStorage storage)
    {
        await using var host = await Start(storage);
        var client = host.Client();
        var gate = BlockingJob.Gate($"live-{host.SchedulerName}");
        await HistoryScenarios.Create(client, typeof(LoggingJob), "live",
            new { customer = "acme", apiToken = "tok-12345", gate = $"live-{host.SchedulerName}" });
        await client.PostAsync("api/jobs/DEFAULT/live/trigger", null);
        Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));

        var running = await client.GetFromJsonAsync<JsonElement>("api/running");
        var id = running.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("name").GetString() == "live")
            .GetProperty("fireInstanceId").GetString();
        var log = await client.GetFromJsonAsync<JsonElement>($"api/running/{id}/log");
        Assert.Equal(new[] { "Calling acme with token ********", "Retrying a call (TimeoutException: upstream slow)" },
            log.EnumerateArray().Select(e => e.GetProperty("message").GetString()));

        gate.Release.Release();
        var record = Assert.Single(await HistoryScenarios.WaitForHistory(client, "?name=live",
            r => r.Any(x => x.GetProperty("status").GetString() == "Succeeded")));
        // Finished: its whole log is in history, and no longer read from the run
        var gone = await client.GetAsync($"api/running/{id}/log");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("its log is then in History", await gone.Content.ReadAsStringAsync());
        Assert.Equal("Done waiting", (await Log(host, record))[^1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_running_jobs_log_is_not_there_with_capture_off()
    {
        await using var host = await Start(JobHistoryStorage.InMemory, o => o.CaptureLogs = false);
        var client = host.Client();
        var gate = BlockingJob.Gate($"uncaptured-{host.SchedulerName}");
        await HistoryScenarios.Create(client, typeof(LoggingJob), "uncaptured", new { customer = "acme", gate = $"uncaptured-{host.SchedulerName}" });
        await client.PostAsync("api/jobs/DEFAULT/uncaptured/trigger", null);
        Assert.True(await gate.Started.WaitAsync(TimeSpan.FromSeconds(15)));
        try
        {
            var running = await client.GetFromJsonAsync<JsonElement>("api/running");
            var id = running.GetProperty("jobs")[0].GetProperty("fireInstanceId").GetString();
            var response = await client.GetAsync($"api/running/{id}/log");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("What this run logs is not kept", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            gate.Release.Release();
        }
    }
}
