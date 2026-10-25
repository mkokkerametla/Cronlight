using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Cronlight.Tests;

/// <summary>Triggers that missed their fire time, as Quartz reports them when it catches up.</summary>
public class MisfireTests
{
    // A database store notices misfires on its misfire handler's passes, one every threshold
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A job whose every-ten-minutes trigger started 25 minutes ago, so its last three fire times were missed and
    /// the next one is five minutes away. Returns the first fire time missed.
    /// </summary>
    private static async Task<DateTimeOffset> ScheduleOverdue(IScheduler scheduler, string name, Action<SimpleScheduleBuilder> misfire)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-25);
        var job = JobBuilder.Create<PlainJob>().WithIdentity(name).StoreDurably().Build();
        var trigger = TriggerBuilder.Create().ForJob(job).WithIdentity(name).StartAt(start)
            .WithSimpleSchedule(s =>
            {
                s.WithIntervalInMinutes(10).RepeatForever();
                misfire(s);
            })
            .Build();
        await scheduler.ScheduleJob(job, [trigger], replace: false);
        return start;
    }

    private static string Status(JsonElement record) => record.GetProperty("status").GetString()!;

    private static Task<List<JsonElement>> WaitForMisfire(HttpClient client, string name) =>
        HistoryScenarios.WaitForHistory(client, $"?name={name}", r => r.Any(x => Status(x) == "Misfired"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missed_fire_time_is_recorded_in_history_with_what_Quartz_did_about_it(bool ramJobStore)
    {
        await using var host = await TestHost.Start(history: _ => { }, ramJobStore: ramJobStore, misfireThreshold: Threshold);
        var scheduler = await host.Scheduler();
        var skippedStart = await ScheduleOverdue(scheduler, "skipped", s => s.WithMisfireHandlingInstructionNextWithRemainingCount());
        var caughtUpStart = await ScheduleOverdue(scheduler, "caught-up", s => s.WithMisfireHandlingInstructionNowWithExistingCount());
        var client = host.Client();

        var skipped = Assert.Single(await WaitForMisfire(client, "skipped"));
        var error = skipped.GetProperty("error").GetString()!;
        Assert.StartsWith("Missed 3 fire times, the first by 25m", error);
        Assert.EndsWith("; Quartz skips it and waits for the next fire time (misfire instruction RescheduleNextWithRemainingCount).", error);
        // The scheduled time is the first one missed; the job's type is looked up, since the listener cannot read the store
        Assert.InRange(skipped.GetProperty("scheduledFireTimeUtc").GetDateTimeOffset() - skippedStart, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));
        Assert.Equal((typeof(PlainJob).FullName, "skipped", JsonValueKind.Null),
            (skipped.GetProperty("jobType").GetString(), skipped.GetProperty("triggerName").GetString(), skipped.GetProperty("durationMs").ValueKind));

        // Rescheduled to now, so it runs; under load it may misfire again before it does
        var caughtUp = await HistoryScenarios.WaitForHistory(client, "?name=caught-up",
            r => r.Any(x => Status(x) == "Misfired") && r.Any(x => Status(x) == "Succeeded"));
        Assert.EndsWith("; Quartz runs it once now (misfire instruction RescheduleNowWithExistingRepeatCount).",
            caughtUp.Last(r => Status(r) == "Misfired").GetProperty("error").GetString());
        // The run that catches up is recorded with the fire time it makes up for, so it shows how late it started
        var run = caughtUp.Single(r => Status(r) == "Succeeded");
        Assert.InRange(run.GetProperty("scheduledFireTimeUtc").GetDateTimeOffset() - caughtUpStart, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));

        // Filtered by outcome, as the dashboard's history filter does
        Assert.All(await HistoryScenarios.History(client, "?status=Misfired"), r => Assert.Equal("Misfired", Status(r)));
        Assert.DoesNotContain(await HistoryScenarios.History(client, "?name=skipped"), r => Status(r) != "Misfired");
    }

    [Fact]
    public async Task A_misfire_raises_an_alert_when_asked_to()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(ramJobStore: true, misfireThreshold: Threshold,
            services: AlertTests.Alerts(sink, o => o.NotifyOnMisfire = true));
        await ScheduleOverdue(await host.Scheduler(), "skipped", s => s.WithMisfireHandlingInstructionNextWithRemainingCount());

        await sink.WaitFor(1);
        var alert = Assert.Single(sink.Alerts);
        Assert.Equal((CronlightAlertKind.Misfired, "Job DEFAULT.skipped missed its fire time", typeof(PlainJob).FullName, 0),
            (alert.Kind, alert.Title, alert.JobType, alert.ConsecutiveFailures));
        Assert.StartsWith("Missed 3 fire times", alert.Error);
    }

    [Fact]
    public async Task Misfires_raise_no_alert_by_default()
    {
        var sink = new AlertTests.RecordingAlertSink();
        await using var host = await TestHost.Start(history: _ => { }, ramJobStore: true, misfireThreshold: Threshold,
            services: AlertTests.Alerts(sink));
        await ScheduleOverdue(await host.Scheduler(), "skipped", s => s.WithMisfireHandlingInstructionNextWithRemainingCount());

        await WaitForMisfire(host.Client(), "skipped");
        await Task.Delay(500);
        Assert.Empty(sink.Alerts);
        Assert.DoesNotContain((await host.Scheduler()).ListenerManager.GetTriggerListeners(), l => l.Name == "Cronlight.Alerts.Misfires");
    }

    [Fact]
    public async Task A_cron_trigger_made_in_the_dashboard_can_skip_missed_runs()
    {
        var audit = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(audit));
        var client = host.Client();
        var created = await client.PostJson("api/jobs", new
        {
            type = typeof(PlainJob).FullName,
            name = "nightly",
            triggers = new object[]
            {
                new { name = "skips", cronExpression = "0 0 2 * * ?", skipMissed = true },
                new { name = "runs", cronExpression = "0 0 3 * * ?" },
            }
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);

        var triggers = (await client.GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/nightly")).GetProperty("triggers").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("misfireInstruction").GetString());
        Assert.Equal(("DoNothing", "SmartPolicy"), (triggers["skips"], triggers["runs"]));
        Assert.Contains("skips missed", audit.Events[^1].Changes["triggers"].After);
    }
}
