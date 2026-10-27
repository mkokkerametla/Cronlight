using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Spi;

namespace Cronlight.Tests;

/// <summary>Triggers that missed their fire time, as Quartz reports them when it catches up.</summary>
public class MisfireTests
{
    // A database store notices misfires on its misfire handler's passes, one every threshold
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A job whose every-ten-minutes trigger is due 25 minutes ago, so its last three fire times were missed and
    /// the next one is five minutes away. Returns the first fire time missed.
    /// </summary>
    /// <remarks>
    /// The trigger fired once before, as one that was waiting while the scheduler was down. Since Quartz 3.17, a
    /// repeating trigger that has never fired starts now when it is scheduled with a start in the past.
    /// </remarks>
    private static Task<DateTimeOffset> ScheduleOverdue(IScheduler scheduler, string name, Action<SimpleScheduleBuilder> misfire) =>
        ScheduleOverdue(scheduler, name, misfire, late: TimeSpan.FromMinutes(25), interval: TimeSpan.FromMinutes(10));

    /// <summary>As above, with a trigger that repeats every <paramref name="interval"/> and was due <paramref name="late"/> ago.</summary>
    private static async Task<DateTimeOffset> ScheduleOverdue(IScheduler scheduler, string name, Action<SimpleScheduleBuilder> misfire,
        TimeSpan late, TimeSpan interval)
    {
        var start = DateTimeOffset.UtcNow - late;
        var job = JobBuilder.Create<PlainJob>().WithIdentity(name).StoreDurably().Build();
        var trigger = (IOperableTrigger)TriggerBuilder.Create().ForJob(job).WithIdentity(name).StartAt(start)
            .WithSimpleSchedule(s =>
            {
                s.WithInterval(interval).RepeatForever();
                misfire(s);
            })
            .Build();
        trigger.SetPreviousFireTimeUtc(start - interval);
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
    public async Task A_misfire_says_how_late_it_is_in_the_largest_units_that_fit()
    {
        await using var host = await TestHost.Start(history: _ => { }, ramJobStore: true, misfireThreshold: Threshold);
        var scheduler = await host.Scheduler();
        // Each misses one fire time: its next is an interval later, still to come
        await ScheduleOverdue(scheduler, "days", s => s.WithMisfireHandlingInstructionNextWithRemainingCount(),
            late: new TimeSpan(1, 2, 0, 0), interval: TimeSpan.FromDays(7));
        // Half a minute to spare, so the minutes do not tick over before Quartz notices
        await ScheduleOverdue(scheduler, "hours", s => s.WithMisfireHandlingInstructionNextWithRemainingCount(),
            late: new TimeSpan(3, 5, 30), interval: TimeSpan.FromDays(1));
        await ScheduleOverdue(scheduler, "seconds", s => s.WithMisfireHandlingInstructionNextWithRemainingCount(),
            late: TimeSpan.FromSeconds(20), interval: TimeSpan.FromHours(1));
        var client = host.Client();

        async Task<string> Error(string name) => Assert.Single(await WaitForMisfire(client, name)).GetProperty("error").GetString()!;
        Assert.StartsWith("Missed its fire time by 1d 2h; ", await Error("days"));
        Assert.StartsWith("Missed its fire time by 3h 5m; ", await Error("hours"));
        Assert.Matches(@"^Missed its fire time by \d+s; ", await Error("seconds"));
    }

    [Fact]
    public async Task Each_misfire_instruction_is_named_as_the_trigger_builders_spell_it()
    {
        await using var host = await TestHost.Start();
        var scheduler = await host.Scheduler();
        var job = JobBuilder.Create<PlainJob>().WithIdentity("instructions").StoreDurably().Build();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        TriggerBuilder Trigger(string name) => TriggerBuilder.Create().ForJob(job).WithIdentity(name).StartAt(start);
        await scheduler.ScheduleJob(job,
        [
            Trigger("fire-now").WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow()).Build(),
            Trigger("now-remaining").WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever()
                .WithMisfireHandlingInstructionNowWithRemainingCount()).Build(),
            Trigger("next-existing").WithSimpleSchedule(s => s.WithIntervalInHours(1).WithRepeatCount(5)
                .WithMisfireHandlingInstructionNextWithExistingCount()).Build(),
            Trigger("once-now").WithCronSchedule("0 0 3 * * ?", c => c.WithMisfireHandlingInstructionFireAndProceed()).Build(),
            Trigger("ignore").WithCronSchedule("0 0 4 * * ?", c => c.WithMisfireHandlingInstructionIgnoreMisfires()).Build(),
            // Calendar interval triggers share the cron trigger's instructions
            Trigger("calendar").WithCalendarIntervalSchedule(c => c.WithIntervalInDays(1).WithMisfireHandlingInstructionDoNothing()).Build(),
        ], replace: false);

        var triggers = (await host.Client().GetFromJsonAsync<JsonElement>("api/jobs/DEFAULT/instructions")).GetProperty("triggers")
            .EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!,
                t => (t.GetProperty("misfireInstruction").GetString(), t.GetProperty("skipsMissed").GetBoolean()));
        Assert.Equal(("FireNow", false), triggers["fire-now"]);
        Assert.Equal(("RescheduleNowWithRemainingRepeatCount", false), triggers["now-remaining"]);
        Assert.Equal(("RescheduleNextWithExistingCount", true), triggers["next-existing"]);
        Assert.Equal(("FireOnceNow", false), triggers["once-now"]);
        Assert.Equal(("IgnoreMisfirePolicy", false), triggers["ignore"]);
        Assert.Equal("DoNothing", triggers["calendar"].Item1);
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
