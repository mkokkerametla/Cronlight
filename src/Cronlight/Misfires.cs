using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Spi;

namespace Cronlight;

/// <summary>A trigger missed its fire time by more than the misfire threshold, and Quartz is about to handle it.</summary>
/// <param name="MissedFireTimeUtc">The first fire time it missed.</param>
/// <param name="DetectedUtc">When Quartz noticed: when the scheduler restarted, or when a thread came free.</param>
/// <param name="Description">How many fire times it missed, by how long, and what Quartz does about it.</param>
/// <param name="Id">
/// The same for the same trigger and missed fire time. A database store whose misfire handling fails rolls it back
/// and reports the misfire again on its next pass, so this keeps a repeat from being recorded or alerted twice.
/// </param>
internal sealed record Misfire(
    string Id,
    string SchedulerName,
    string InstanceName,
    JobKey Job,
    TriggerKey Trigger,
    DateTimeOffset MissedFireTimeUtc,
    DateTimeOffset DetectedUtc,
    string Description);

/// <summary>
/// Hears one scheduler's misfires. Quartz passes a misfired trigger without its scheduler, so each scheduler gets
/// a listener of its own.
/// </summary>
internal sealed class MisfireListener(string name, IScheduler scheduler, Action<Misfire> onMisfire, ILogger logger) : ITriggerListener
{
    public string Name => name;

    // A database job store calls this inside its transaction, holding its trigger lock: an exception would roll
    // back the misfire handling, to be retried on every pass, and calling back into the scheduler could wait on
    // that lock. So nothing here throws or reads the store.
    public Task TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default)
    {
        try
        {
            onMisfire(Misfires.Describe(scheduler.SchedulerName, scheduler.SchedulerInstanceId, trigger, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the misfire of trigger {Trigger}", trigger.Key);
        }
        return Task.CompletedTask;
    }

    public Task TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction triggerInstructionCode,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal static class Misfires
{
    // Counting further would walk a frequent trigger's whole outage
    private const int MaxCounted = 100;

    /// <summary>
    /// Quartz reports a trigger before applying its misfire instruction to it, so the trigger's next fire time is
    /// still the first one it missed. Fire times are counted without the trigger's calendar, which cannot be read
    /// safely here, so a calendar's excluded times count as missed too.
    /// </summary>

    public static Misfire Describe(string schedulerName, string instanceName, ITrigger trigger, DateTimeOffset nowUtc)
    {
        var missed = trigger.GetNextFireTimeUtc() ?? nowUtc;
        var count = 1;
        // One past the cap, to tell exactly MaxCounted from more
        for (var next = trigger.GetFireTimeAfter(missed); next <= nowUtc && count <= MaxCounted; next = trigger.GetFireTimeAfter(next))
            count++;

        var late = Duration(nowUtc - missed);
        var what = count == 1
            ? $"Missed its fire time by {late}"
            : count > MaxCounted
                ? $"Missed {MaxCounted}+ fire times, the first by {late}"
                : $"Missed {count} fire times, the first by {late}";
        return new Misfire(Id(trigger.Key, missed), schedulerName, instanceName, trigger.JobKey, trigger.Key, missed, nowUtc,
            $"{what}; {Outcome(trigger, nowUtc)} (misfire instruction {Instruction(trigger)}).");
    }

    /// <summary>
    /// Applies the misfire instruction to a copy, as Quartz is about to apply it to the trigger. The trigger's
    /// calendar is left out, since reading it could wait on the job store: a calendar only moves a later fire time.
    /// </summary>
    private static string Outcome(ITrigger trigger, DateTimeOffset nowUtc)
    {
        if (trigger.Clone() is not IOperableTrigger copy)
            return "Quartz applies its misfire instruction";
        copy.UpdateAfterMisfire(null);
        // Running now puts the next fire time ahead of the trigger's own next one; skipping lands on that one or later
        var next = copy.GetNextFireTimeUtc();
        var regular = trigger.GetFireTimeAfter(nowUtc);
        return next == null ? "it has no fire times left, so it will not run again"
            : regular == null || next < regular ? "Quartz runs it once now"
            : "Quartz skips it and waits for the next fire time";
    }

    // Short enough for the history table's entry id column with any trigger key
    private static string Id(TriggerKey trigger, DateTimeOffset missed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{trigger.Group}\n{trigger.Name}"));
        return $"misfire-{missed.UtcTicks}-{Convert.ToHexString(hash, 0, 8)}";
    }

    /// <summary>The instruction's name, as the trigger builders spell it.</summary>
    private static string Instruction(ITrigger trigger)
    {
        var instruction = trigger.MisfireInstruction;
        if (instruction == MisfireInstruction.SmartPolicy)
            return "SmartPolicy";
        // Cron, calendar interval and daily time interval triggers share theirs
        return (trigger is ISimpleTrigger, instruction) switch
        {
            (true, MisfireInstruction.SimpleTrigger.FireNow) => "FireNow",
            (true, MisfireInstruction.SimpleTrigger.RescheduleNowWithExistingRepeatCount) => "RescheduleNowWithExistingRepeatCount",
            (true, MisfireInstruction.SimpleTrigger.RescheduleNowWithRemainingRepeatCount) => "RescheduleNowWithRemainingRepeatCount",
            (true, MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount) => "RescheduleNextWithRemainingCount",
            (true, MisfireInstruction.SimpleTrigger.RescheduleNextWithExistingCount) => "RescheduleNextWithExistingCount",
            (false, MisfireInstruction.CronTrigger.FireOnceNow) => "FireOnceNow",
            (false, MisfireInstruction.CronTrigger.DoNothing) => "DoNothing",
            _ => instruction.ToString(),
        };
    }

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{span.Hours}h {span.Minutes}m",
        { TotalMinutes: >= 1 } => $"{span.Minutes}m {span.Seconds}s",
        _ => $"{Math.Max(0, span.Seconds)}s",
    };
}
