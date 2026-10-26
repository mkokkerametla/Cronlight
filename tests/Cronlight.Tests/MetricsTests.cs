using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cronlight.Tests;

public class MetricsTests
{
    /// <summary>Measurements on the Cronlight meter for one scheduler: other test hosts share the process.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener listener = new();
        public ConcurrentQueue<(string Instrument, double Value, Dictionary<string, string?> Tags)> Measurements { get; } = new();

        public Recorder(Func<string> scheduler)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CronlightMetrics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            };
            void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                var all = new Dictionary<string, string?>();
                foreach (var tag in tags)
                    all[tag.Key] = tag.Value?.ToString();
                if (all.GetValueOrDefault("cronlight.scheduler") == scheduler())
                    Measurements.Enqueue((instrument.Name, value, all));
            }
            listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            listener.Start();
        }

        public void Dispose() => listener.Dispose();
    }

    [Fact]
    public async Task Runs_are_counted_by_outcome_with_their_duration()
    {
        string? name = null;
        using var recorder = new Recorder(() => name!);
        await using var host = await TestHost.Start();
        name = host.SchedulerName;
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(PlainJob), "ok");
        await HistoryScenarios.Create(client, typeof(FailingJob), "broken");

        await client.PostAsync("api/jobs/DEFAULT/ok/trigger", null);
        await client.PostAsync("api/jobs/DEFAULT/broken/trigger", null);
        for (var deadline = DateTime.UtcNow.AddSeconds(15);
             recorder.Measurements.Count(m => m.Instrument == "cronlight.job.runs") < 2 && DateTime.UtcNow < deadline;)
            await Task.Delay(50);

        var runs = recorder.Measurements.Where(m => m.Instrument == "cronlight.job.runs")
            .Select(m => (m.Tags["cronlight.job.group"]!, m.Tags["cronlight.job.name"]!, m.Tags["cronlight.run.outcome"]!, m.Value))
            .OrderBy(r => r.Item2)
            .ToList();
        Assert.Equal(new[] { ("DEFAULT", "broken", "failed", 1.0), ("DEFAULT", "ok", "succeeded", 1.0) }, runs);
        Assert.Equal(2, recorder.Measurements.Count(m => m.Instrument == "cronlight.job.run.duration" && m.Value >= 0));
        // Each run went up and back down
        var active = recorder.Measurements.Where(m => m.Instrument == "cronlight.job.runs.active").ToList();
        Assert.Equal((2, 0.0), (active.Count(m => m.Value == 1), active.Sum(m => m.Value)));
    }

    [Fact]
    public async Task An_attempt_refired_at_once_counts_as_failed_and_leaves_no_run_active()
    {
        string? name = null;
        using var recorder = new Recorder(() => name!);
        await using var host = await TestHost.Start();
        name = host.SchedulerName;
        var client = host.Client();
        await HistoryScenarios.Create(client, typeof(RefireOnceJob), "retry");

        await client.PostAsync("api/jobs/DEFAULT/retry/trigger", null);
        for (var deadline = DateTime.UtcNow.AddSeconds(15);
             recorder.Measurements.Count(m => m.Instrument == "cronlight.job.runs") < 2 && DateTime.UtcNow < deadline;)
            await Task.Delay(50);

        Assert.Equal(new[] { "failed", "succeeded" },
            recorder.Measurements.Where(m => m.Instrument == "cronlight.job.runs").Select(m => m.Tags["cronlight.run.outcome"]));
        Assert.Equal(0, recorder.Measurements.Where(m => m.Instrument == "cronlight.job.runs.active").Sum(m => m.Value));
    }

    [Fact]
    public async Task A_failure_that_is_retried_counts_as_retried()
    {
        string? name = null;
        using var recorder = new Recorder(() => name!);
        await using var host = await TestHost.Start();
        name = host.SchedulerName;
        var client = host.Client();
        var key = Guid.NewGuid().ToString("N");
        RetriedJob.FailuresLeft[key] = 1;
        await HistoryScenarios.Create(client, typeof(RetriedJob), "flaky", new { key });

        await client.PostAsync("api/jobs/DEFAULT/flaky/trigger", null);
        for (var deadline = DateTime.UtcNow.AddSeconds(15);
             recorder.Measurements.Count(m => m.Instrument == "cronlight.job.runs") < 2 && DateTime.UtcNow < deadline;)
            await Task.Delay(50);

        Assert.Equal(new[] { "retried", "succeeded" },
            recorder.Measurements.Where(m => m.Instrument == "cronlight.job.runs").Select(m => m.Tags["cronlight.run.outcome"]));
    }

    [Fact]
    public async Task The_health_check_reports_each_scheduler()
    {
        await using var host = await TestHost.Start(services: s => s.AddHealthChecks().AddCronlight());
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var scheduler = await host.Scheduler();

        var running = await health.CheckHealthAsync();
        Assert.Equal(HealthStatus.Healthy, running.Status);
        Assert.Equal("is running", running.Entries["cronlight"].Data[host.SchedulerName]);

        await scheduler.Standby();
        var standby = (await health.CheckHealthAsync()).Entries["cronlight"];
        Assert.Equal(HealthStatus.Degraded, standby.Status);
        Assert.Equal($"{host.SchedulerName} is in standby", standby.Description);

        await scheduler.Shutdown();
        var stopped = (await health.CheckHealthAsync()).Entries["cronlight"];
        Assert.Equal(HealthStatus.Unhealthy, stopped.Status);
        Assert.Equal($"{host.SchedulerName} has shut down", stopped.Description);
    }

    [Fact]
    public async Task The_health_check_fails_when_the_job_store_cannot_be_read()
    {
        await using var host = await TestHost.Start(services: s => s.AddHealthChecks().AddCronlight(failureStatus: HealthStatus.Degraded));
        // Renamed rather than dropped, and back again before the host stops, so the scheduler can shut down cleanly
        await host.Database.Execute("ALTER TABLE QRTZ_TRIGGERS RENAME TO QRTZ_TRIGGERS_AWAY");
        HealthReportEntry entry;
        try
        {
            entry = (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["cronlight"];
        }
        finally
        {
            await host.Database.Execute("ALTER TABLE QRTZ_TRIGGERS_AWAY RENAME TO QRTZ_TRIGGERS");
        }

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.StartsWith($"{host.SchedulerName} cannot read its job store", entry.Description);
    }
}
