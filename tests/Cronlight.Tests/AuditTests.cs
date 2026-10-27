using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cronlight.Tests;

public class AuditTests
{
    private static readonly string Plain = typeof(PlainJob).FullName!;

    [Fact]
    public async Task Every_change_is_audited_with_who_made_it_and_the_outcome()
    {
        var sink = new RecordingSink();
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        }, services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var admin = host.Client("admin");

        await admin.PostJson("api/jobs", new { type = Plain, name = "nightly", group = "billing", triggers = new[] { new { cronExpression = "0 0 6 * * ?" } } });
        await admin.PutJson("api/jobs/billing/nightly", new { description = "Nightly run" });
        await admin.PostAsync("api/jobs/billing/nightly/pause", null);
        await admin.PostAsync("api/jobs/billing/nightly/resume", null);
        await admin.PostAsync("api/jobs/billing/nightly/trigger", null);
        await admin.DeleteAsync("api/jobs/billing/nightly");

        Assert.Equal(
            new[] { ("create", 201), ("update", 204), ("pause", 204), ("resume", 204), ("trigger", 204), ("delete", 204) },
            sink.Events.Select(e => (e.Action, e.StatusCode)));
        Assert.All(sink.Events, e =>
        {
            Assert.Equal("test", e.User);
            Assert.Equal(("billing", "nightly"), (e.JobGroup, e.JobName));
            Assert.True(e.Succeeded);
            Assert.Null(e.Error);
            Assert.InRange(e.TimeUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        });
        // Reads are not audited
        await admin.GetAsync("api/jobs");
        Assert.Equal(6, sink.Events.Count);
    }

    [Fact]
    public async Task Creates_and_edits_record_what_changed_without_revealing_secrets()
    {
        var sink = new RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var client = host.Client();

        await client.PostJson("api/jobs", new
        {
            type = Plain,
            name = "sync",
            description = "Sync orders",
            data = new { region = "eu", apiToken = "s3cr3t-before" },
            triggers = new[] { new { cronExpression = "0 0 6 * * ?" } },
        });
        // What the edit form sends: the masked token back unchanged, one value and the schedule changed
        await client.PutJson("api/jobs/DEFAULT/sync", new
        {
            description = "Sync orders hourly",
            data = new { region = "us", apiToken = "********" },
            triggers = new[] { new { cronExpression = "0 0 * * * ?", timeZone = "Europe/Paris" } },
        });
        // A new token: recorded as changed, but not its value
        await client.PutJson("api/jobs/DEFAULT/sync", new { data = new { region = "us", apiToken = "s3cr3t-after" } });

        var (create, edit, rotate) = (sink.Events[0].Changes, sink.Events[1].Changes, sink.Events[2].Changes);
        Assert.Equal(new CronlightAuditChange(null, "Sync orders"), create["description"]);
        Assert.Equal(new CronlightAuditChange(null, "eu"), create["data.region"]);
        Assert.Equal(new CronlightAuditChange(null, "********"), create["data.apiToken"]);
        Assert.Equal(new CronlightAuditChange(null, "0 0 6 * * ? (UTC)"), create["triggers"]);

        Assert.Equal(new CronlightAuditChange("Sync orders", "Sync orders hourly"), edit["description"]);
        Assert.Equal(new CronlightAuditChange("eu", "us"), edit["data.region"]);
        Assert.Equal(new CronlightAuditChange("0 0 6 * * ? (UTC)", "0 0 * * * ? (Europe/Paris)"), edit["triggers"]);
        Assert.False(edit.ContainsKey("data.apiToken")); // sent back masked, so unchanged

        Assert.Equal(new CronlightAuditChange("********", "********"), Assert.Single(rotate).Value);
        Assert.DoesNotContain("s3cr3t", JsonSerializer.Serialize(sink.Events));
    }

    [Fact]
    public async Task Refused_and_failed_attempts_are_audited_with_the_reason()
    {
        var sink = new RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(sink));

        await host.Client(asDashboard: false).PostAsync("api/jobs/DEFAULT/x/pause", null);
        await host.Client().PostAsync("api/jobs/DEFAULT/missing/trigger", null);
        await host.Client().PostJson("api/jobs", new { type = "Nope.Job", name = "bad" });

        Assert.All(sink.Events, e => Assert.False(e.Succeeded));
        Assert.Equal(
            new[] { ("pause", 403, "DEFAULT.x"), ("trigger", 404, "DEFAULT.missing"), ("create", 400, "DEFAULT.bad") },
            sink.Events.Select(e => (e.Action, e.StatusCode, $"{e.JobGroup}.{e.JobName}")));
        Assert.Contains("X-Cronlight-Request", sink.Events[0].Error);
        Assert.Equal("Job DEFAULT.missing does not exist.", sink.Events[1].Error);
        Assert.Equal("Unknown job type 'Nope.Job'", sink.Events[2].Error);
    }

    [Fact]
    public async Task Changes_in_read_only_mode_are_audited_as_refused()
    {
        var sink = new RecordingSink();
        await using var host = await TestHost.Start(configure: o => o.ReadOnly = true, services: s => s.AddSingleton<ICronlightAuditSink>(sink));

        await host.Client().DeleteAsync("api/jobs/DEFAULT/x");

        var refused = Assert.Single(sink.Events);
        Assert.Equal(("delete", 403, "anonymous"), (refused.Action, refused.StatusCode, refused.User));
        Assert.Contains("read-only", refused.Error);
    }

    [Fact]
    public async Task Events_are_logged_under_the_audit_category_without_secrets()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await TestHost.Start(services: s =>
            s.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(LoggerFactory.Create(b => b.AddProvider(logs)))));
        var client = host.Client();

        await client.PostJson("api/jobs", new { type = Plain, name = "logged", data = new { password = "hunter2" } });
        await host.Client(asDashboard: false).DeleteAsync("api/jobs/DEFAULT/logged");

        var audit = logs.Entries.Where(e => e.Category == "Cronlight.Audit").ToList();
        Assert.Equal(2, audit.Count);
        Assert.Equal(LogLevel.Information, audit[0].Level);
        Assert.Contains($"anonymous create DEFAULT.logged in {host.SchedulerName}: succeeded (201)", audit[0].Message);
        Assert.Contains("data.password", audit[0].Message);
        Assert.Equal(LogLevel.Warning, audit[1].Level);
        Assert.Contains($"delete DEFAULT.logged in {host.SchedulerName}: refused (403)", audit[1].Message);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("hunter2"));
    }

    [Fact]
    public async Task A_failing_sink_is_logged_and_does_not_fail_the_change()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await TestHost.Start(services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(LoggerFactory.Create(b => b.AddProvider(logs))));
            s.AddSingleton<ICronlightAuditSink, ThrowingSink>();
        });

        var response = await host.Client().PostJson("api/jobs", new { type = Plain, name = "kept" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(await (await host.Scheduler()).GetJobDetail(new Quartz.JobKey("kept")));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Audit sink ThrowingSink failed"));
    }

    internal sealed class RecordingSink : ICronlightAuditSink
    {
        private readonly ConcurrentQueue<CronlightAuditEvent> events = new();
        public List<CronlightAuditEvent> Events => events.ToList();

        public Task WriteAsync(CronlightAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            events.Enqueue(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ICronlightAuditSink
    {
        public Task WriteAsync(CronlightAuditEvent auditEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("audit store is down");
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries = new();
        public List<(string Category, LogLevel Level, string Message)> Entries => entries.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
