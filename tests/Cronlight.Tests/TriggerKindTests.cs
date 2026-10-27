using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Triggers;

namespace Cronlight.Tests;

/// <summary>One-time and interval triggers, created and edited from the dashboard alongside cron ones.</summary>
public class TriggerKindTests
{
    private static readonly string Plain = typeof(PlainJob).FullName!;

    private static async Task<Dictionary<string, JsonElement>> Triggers(HttpClient client, string name) =>
        (await client.GetFromJsonAsync<JsonElement>($"api/jobs/DEFAULT/{name}")).GetProperty("triggers").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);

    [Fact]
    public async Task One_time_and_interval_triggers_are_created_and_shown()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        var at = DateTimeOffset.UtcNow.AddDays(2);
        var from = DateTimeOffset.UtcNow.AddHours(1);

        var created = await client.PostJson("api/jobs", new
        {
            type = Plain, name = "mixed",
            triggers = new object[]
            {
                new { name = "launch", kind = "Once", startAtUtc = at },
                new { name = "poll", kind = "Interval", intervalSeconds = 300, startAtUtc = from, repeatCount = 9, skipMissed = true },
                new { name = "forever", kind = "interval", intervalSeconds = 3600 },
                new { name = "nightly", cronExpression = "0 0 2 * * ?" }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var triggers = await Triggers(client, "mixed");
        var launch = triggers["launch"];
        Assert.Equal("Once", launch.GetProperty("kind").GetString());
        Assert.Equal(at, launch.GetProperty("startTimeUtc").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(at, launch.GetProperty("nextFireTimeUtc").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(JsonValueKind.Null, launch.GetProperty("intervalSeconds").ValueKind);
        Assert.Equal("FireNow", launch.GetProperty("misfireInstruction").GetString());
        Assert.False(launch.GetProperty("skipsMissed").GetBoolean());

        var poll = triggers["poll"];
        Assert.Equal("Interval", poll.GetProperty("kind").GetString());
        Assert.Equal(300, poll.GetProperty("intervalSeconds").GetInt64());
        Assert.Equal(9, poll.GetProperty("repeatCount").GetInt32());
        Assert.True(poll.GetProperty("skipsMissed").GetBoolean());
        Assert.Equal(from, poll.GetProperty("nextFireTimeUtc").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));

        var forever = triggers["forever"];
        Assert.Equal("Interval", forever.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, forever.GetProperty("repeatCount").ValueKind);
        // Without a start it starts now
        Assert.InRange(forever.GetProperty("startTimeUtc").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

        Assert.Equal("Cron", triggers["nightly"].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Triggers_sent_back_unchanged_are_left_alone_and_changed_ones_keep_their_rhythm()
    {
        // Neither fired nor handled as a misfire while the test reads it, which would move its next fire time
        await using var host = await TestHost.Start(misfireThreshold: TimeSpan.FromHours(1));
        var client = host.Client();
        var started = DateTimeOffset.UtcNow.AddMinutes(-10).AddSeconds(-7);
        var scheduler = await host.Scheduler();
        await scheduler.Standby();
        await client.PostJson("api/jobs", new { type = Plain, name = "poller" });
        // As the host might have registered it: started in the past, so it has fired before
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("every-minute", "DEFAULT").ForJob("poller", "DEFAULT")
            .StartAt(started).WithSimpleSchedule(s => s.WithIntervalInSeconds(60).RepeatForever()).Build());
        var before = (await Triggers(client, "poller"))["every-minute"];
        var stored = before.GetProperty("startTimeUtc").GetDateTimeOffset();

        // Sent back exactly as shown: not rescheduled, though its start is long past
        var unchanged = await client.PutJson("api/jobs/DEFAULT/poller", new
        {
            triggers = new[] { new { name = "every-minute", kind = "Interval", intervalSeconds = 60L, startAtUtc = stored, skipMissed = before.GetProperty("skipsMissed").GetBoolean() } }
        });
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        var after = (await Triggers(client, "poller"))["every-minute"];
        Assert.Equal(stored, after.GetProperty("startTimeUtc").GetDateTimeOffset());
        Assert.Equal(before.GetProperty("nextFireTimeUtc").GetDateTimeOffset(), after.GetProperty("nextFireTimeUtc").GetDateTimeOffset());

        // A new interval from the same start moves on to its next run after now, rather than running at once
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/poller", new
        {
            triggers = new[] { new { name = "every-minute", kind = "Interval", intervalSeconds = 120L, startAtUtc = stored } }
        })).StatusCode);
        var changed = (await Triggers(client, "poller"))["every-minute"];
        var next = changed.GetProperty("nextFireTimeUtc").GetDateTimeOffset();
        Assert.InRange(next, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(121));
        Assert.Equal(0, (next - stored).Ticks % TimeSpan.FromSeconds(120).Ticks);
        Assert.Equal(120, changed.GetProperty("intervalSeconds").GetInt64());
    }

    [Fact]
    public async Task A_trigger_can_change_kind_and_keep_its_name()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = Plain, name = "switch", triggers = new[] { new { name = "main", cronExpression = "0 0 6 * * ?" } } });

        var response = await client.PutJson("api/jobs/DEFAULT/switch", new
        {
            triggers = new[] { new { name = "main", kind = "Interval", intervalSeconds = 3600L } },
            triggerKinds = new[] { "Cron", "Once", "Interval" }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var main = Assert.Single(await Triggers(client, "switch")).Value;
        Assert.Equal(("Interval", 3600L), (main.GetProperty("kind").GetString(), main.GetProperty("intervalSeconds").GetInt64()));
    }

    [Fact]
    public async Task Editing_replaces_one_time_and_interval_triggers_but_keeps_waiting_retries()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = Plain, name = "edited",
            triggers = new object[] { new { name = "launch", kind = "Once", startAtUtc = DateTimeOffset.UtcNow.AddDays(1) } }
        });
        var scheduler = await host.Scheduler();
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity("waiting", "Cronlight.Retries").ForJob("edited", "DEFAULT")
            .StartAt(DateTimeOffset.UtcNow.AddHours(1)).Build());

        var response = await client.PutJson("api/jobs/DEFAULT/edited", new
        {
            triggers = new[] { new { name = "poll", kind = "Interval", intervalSeconds = 600L } },
            triggerKinds = new[] { "Cron", "Once", "Interval" }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { "poll", "waiting" }, (await Triggers(client, "edited")).Keys.Order());
    }

    [Fact]
    public async Task A_client_that_sends_only_cron_triggers_keeps_the_jobs_other_triggers()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new
        {
            type = Plain, name = "kept",
            triggers = new object[]
            {
                new { name = "nightly", cronExpression = "0 0 2 * * ?" },
                new { name = "poll", kind = "Interval", intervalSeconds = 600L },
                new { name = "launch", kind = "Once", startAtUtc = DateTimeOffset.UtcNow.AddDays(1) }
            }
        });

        // As a script written before one-time and interval triggers would send it
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/kept", new
        {
            triggers = new[] { new { name = "hourly", cronExpression = "0 0 * * * ?" } }
        })).StatusCode);
        Assert.Equal(new[] { "hourly", "launch", "poll" }, (await Triggers(client, "kept")).Keys.Order());

        // Sending an interval trigger replaces interval triggers, and still keeps the one-time one
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/kept", new
        {
            triggers = new object[] { new { name = "hourly", cronExpression = "0 0 * * * ?" }, new { name = "fast", kind = "Interval", intervalSeconds = 60L } }
        })).StatusCode);
        Assert.Equal(new[] { "fast", "hourly", "launch" }, (await Triggers(client, "kept")).Keys.Order());

        // Naming every kind, as the dashboard does, replaces them all
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/kept", new
        {
            triggers = Array.Empty<object>(), triggerKinds = new[] { "Cron", "Once", "Interval" }
        })).StatusCode);
        Assert.Empty(await Triggers(client, "kept"));

        var unknown = await client.PutJson("api/jobs/DEFAULT/kept", new { triggers = Array.Empty<object>(), triggerKinds = new[] { "Daily" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Unknown trigger kind 'Daily' in triggerKinds", await unknown.Content.ReadAsStringAsync());
        var missing = await client.PutJson("api/jobs/DEFAULT/kept", new
        {
            triggers = new[] { new { kind = "Interval", intervalSeconds = 60L } }, triggerKinds = new[] { "Cron" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("triggerKinds does not name Interval", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pending_manual_runs_are_shown_but_never_edited()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = Plain, name = "waiting" });
        var scheduler = await host.Scheduler();
        // In standby the run stays waiting, as one behind a run of a job that does not run concurrently would
        await scheduler.Standby();
        await client.PostJson("api/jobs/DEFAULT/waiting/trigger", new { data = new Dictionary<string, string> { ["region"] = "eu" } });

        var manual = Assert.Single((await Triggers(client, "waiting")).Values);
        Assert.Equal("RunNow", manual.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, manual.GetProperty("startTimeUtc").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/waiting", new
        {
            triggers = Array.Empty<object>(), triggerKinds = new[] { "Cron", "Once", "Interval" }
        })).StatusCode);
        var kept = Assert.Single(await scheduler.GetTriggersOfJob(new JobKey("waiting", "DEFAULT")));
        Assert.Equal(manual.GetProperty("name").GetString(), kept.Key.Name);
        // With the run's own data still on it
        Assert.Equal("eu", kept.JobDataMap.GetString("region"));
    }

    [Fact]
    public async Task Intervals_the_editor_cannot_send_back_are_shown_by_class_and_kept()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = Plain, name = "fine" });
        var scheduler = await host.Scheduler();
        await scheduler.Standby();
        foreach (var (name, interval) in new[] { ("half-second", TimeSpan.FromMilliseconds(500)), ("one-and-a-half", TimeSpan.FromSeconds(1.5)) })
            await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(name).ForJob("fine", "DEFAULT")
                .StartAt(DateTimeOffset.UtcNow.AddHours(1)).WithSimpleSchedule(s => s.WithInterval(interval).RepeatForever()).Build());

        var triggers = await Triggers(client, "fine");
        Assert.All(triggers.Values, t =>
        {
            Assert.Equal(nameof(SimpleTriggerImpl), t.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, t.GetProperty("intervalSeconds").ValueKind);
        });

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/fine", new
        {
            triggers = Array.Empty<object>(), triggerKinds = new[] { "Cron", "Once", "Interval" }
        })).StatusCode);
        var kept = (await scheduler.GetTriggersOfJob(new JobKey("fine", "DEFAULT"))).Cast<ISimpleTrigger>().ToDictionary(t => t.Key.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(500), kept["half-second"].RepeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(1.5), kept["one-and-a-half"].RepeatInterval);
    }

    [Fact]
    public async Task Changing_an_interval_with_runs_left_keeps_counting_the_runs_it_has_had()
    {
        // Late by less than the misfire threshold, so Quartz leaves the trigger as it is meanwhile
        await using var host = await TestHost.Start(misfireThreshold: TimeSpan.FromHours(2));
        var client = host.Client();
        await client.PostJson("api/jobs", new { type = Plain, name = "counted" });
        var scheduler = await host.Scheduler();
        await scheduler.Standby();
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var trigger = (SimpleTriggerImpl)TriggerBuilder.Create().WithIdentity("ten-runs").ForJob("counted", "DEFAULT")
            .StartAt(started).WithSimpleSchedule(s => s.WithIntervalInMinutes(5).WithRepeatCount(9)).Build();
        // Six of its ten runs are done
        trigger.TimesTriggered = 6;
        await scheduler.ScheduleJob(trigger);
        var stored = (await Triggers(client, "counted"))["ten-runs"].GetProperty("startTimeUtc").GetDateTimeOffset();

        // Every 10 minutes instead, still ten runs in all, its start sent back as it was
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutJson("api/jobs/DEFAULT/counted", new
        {
            triggers = new[] { new { name = "ten-runs", kind = "Interval", intervalSeconds = 600L, startAtUtc = stored, repeatCount = 9 } }
        })).StatusCode);

        var changed = (await Triggers(client, "counted"))["ten-runs"];
        // Four runs left: the next and three more, from the next run on its rhythm rather than at once
        Assert.Equal(3, changed.GetProperty("repeatCount").GetInt32());
        Assert.InRange(changed.GetProperty("nextFireTimeUtc").GetDateTimeOffset(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));

        var done = (SimpleTriggerImpl)TriggerBuilder.Create().WithIdentity("all-done").ForJob("counted", "DEFAULT")
            .StartAt(started).WithSimpleSchedule(s => s.WithIntervalInMinutes(5).WithRepeatCount(2)).Build();
        done.TimesTriggered = 3;
        await scheduler.ScheduleJob(done);
        var exhausted = await client.PutJson("api/jobs/DEFAULT/counted", new
        {
            triggers = new[] { new { name = "all-done", kind = "Interval", intervalSeconds = 600L, startAtUtc = started, repeatCount = 2 } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, exhausted.StatusCode);
        Assert.Contains("has already run 3 times", await exhausted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_one_time_and_interval_triggers_get_clear_errors()
    {
        await using var host = await TestHost.Start();
        var client = host.Client();

        async Task<string> Refused(object trigger)
        {
            var response = await client.PostJson("api/jobs", new { type = Plain, name = $"bad-{Guid.NewGuid():N}", triggers = new[] { trigger } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("needs the time it runs", await Refused(new { kind = "Once" }));
        Assert.Contains("is in the past", await Refused(new { kind = "Once", startAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        Assert.Contains("between 1 second and 366 days", await Refused(new { kind = "Interval", intervalSeconds = 0L }));
        Assert.Contains("between 1 second and 366 days", await Refused(new { kind = "Interval" }));
        Assert.Contains("cannot be negative", await Refused(new { kind = "Interval", intervalSeconds = 60L, repeatCount = -1 }));
        // One run in all would be stored as a one-time trigger, losing its interval
        Assert.Contains("use kind Once", await Refused(new { kind = "Interval", intervalSeconds = 60L, repeatCount = 0 }));
        Assert.Contains("Unknown trigger kind 'Daily'", await Refused(new { kind = "Daily" }));
    }

    [Fact]
    public async Task Trigger_changes_are_audited_in_words()
    {
        var sink = new AuditTests.RecordingSink();
        await using var host = await TestHost.Start(services: s => s.AddSingleton<ICronlightAuditSink>(sink));
        var at = new DateTimeOffset(2099, 3, 1, 9, 30, 0, TimeSpan.Zero);

        await host.Client().PostJson("api/jobs", new
        {
            type = Plain, name = "audited",
            triggers = new object[]
            {
                new { kind = "Once", startAtUtc = at, skipMissed = true },
                new { kind = "Interval", intervalSeconds = 90L, startAtUtc = at, repeatCount = 2 }
            }
        });

        Assert.Equal("once at 2099-03-01 09:30:00 UTC, skips missed; every 90s from 2099-03-01 09:30:00 UTC, 3 runs",
            Assert.Single(sink.Events).Changes["triggers"].After);
    }
}
