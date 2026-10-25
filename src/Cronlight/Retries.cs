using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight;

/// <summary>How often, and how long apart, a failed run is retried.</summary>
/// <param name="MaxRetries">Retries after the first attempt; 0 means none.</param>
/// <param name="DelaySeconds">Wait before the first retry.</param>
/// <param name="Exponential">Double the wait before each further retry.</param>
public sealed record JobRetryPolicy(int MaxRetries, int DelaySeconds, bool Exponential = false)
{
    public const int MaxAllowedRetries = 20;
    public const int MaxDelaySeconds = 24 * 60 * 60;

    /// <summary>What is wrong with the policy, or null when it can be used.</summary>
    public string? Problem() =>
        MaxRetries is < 0 or > MaxAllowedRetries ? $"Retries must be between 0 and {MaxAllowedRetries}"
        : MaxRetries > 0 && DelaySeconds is < 1 or > MaxDelaySeconds ? $"The retry delay must be between 1 and {MaxDelaySeconds} seconds"
        : null;

    /// <summary>The wait before the given retry (1 for the first), never longer than a day.</summary>
    public TimeSpan Delay(int retry) =>
        TimeSpan.FromSeconds(Exponential ? Math.Min(DelaySeconds * Math.Pow(2, retry - 1), MaxDelaySeconds) : DelaySeconds);
}

/// <summary>
/// Retries a failed run of this job type automatically. The dashboard can change it for a single job.
/// <c>[CronlightRetry(3, DelaySeconds = 30, Exponential = true)]</c> waits 30 s, 60 s, then 120 s.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CronlightRetryAttribute : Attribute
{
    public CronlightRetryAttribute(int maxRetries) => MaxRetries = maxRetries;

    public int MaxRetries { get; }
    public int DelaySeconds { get; set; } = 60;
    public bool Exponential { get; set; }

    internal JobRetryPolicy Policy => new(MaxRetries, DelaySeconds, Exponential);
}

/// <summary>
/// Where retries keep their state. Job and trigger data keys starting with "Cronlight." are reserved: the dashboard
/// hides them and refuses to set them.
/// </summary>
internal static class Retries
{
    public const string ReservedPrefix = "Cronlight.";

    /// <summary>Job data: the job's own policy, as JSON, when it differs from its type's.</summary>
    public const string PolicyKey = "Cronlight.Retry";

    /// <summary>Trigger data: which attempt this run is, from 2 for the first retry. Absent on a first attempt.</summary>
    public const string AttemptKey = "Cronlight.Attempt";

    /// <summary>Retry triggers are in this group, so the dashboard and people can tell them apart.</summary>
    public const string TriggerGroup = "Cronlight.Retries";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Type, JobRetryPolicy?> TypePolicies = new();

    public static bool IsReserved(string key) => key.StartsWith(ReservedPrefix, StringComparison.Ordinal);

    public static JobRetryPolicy? ForType(Type type) =>
        TypePolicies.GetOrAdd(type, t => t.GetCustomAttribute<CronlightRetryAttribute>()?.Policy is { MaxRetries: > 0 } p ? p : null);

    /// <summary>The policy a job runs with: its own when set, else its type's. Null when it is not retried.</summary>
    public static JobRetryPolicy? For(IJobDetail job)
    {
        var policy = job.JobDataMap.TryGetValue(PolicyKey, out var stored) && stored is string json ? Parse(json) : ForType(job.JobType);
        return policy is { MaxRetries: > 0 } ? policy : null;
    }

    /// <summary>The job data value for a policy, or null when the job should follow its type.</summary>
    public static string? Store(JobRetryPolicy policy, Type jobType) =>
        policy.MaxRetries == 0 && ForType(jobType) == null || policy == ForType(jobType)
            ? null
            : JsonSerializer.Serialize(policy, Json);

    private static JobRetryPolicy? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JobRetryPolicy>(json, Json) is { } policy && policy.Problem() == null ? policy : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int Attempt(IJobExecutionContext context) =>
        context.Trigger.JobDataMap.TryGetValue(AttemptKey, out var value) && int.TryParse(value?.ToString(), out var attempt) && attempt > 1
            ? attempt
            : 1;
}

/// <summary>
/// Decides whether a failed run is retried. History and alerts ask it too, so a failure that will be retried is
/// recorded as Retried and raises no alert; only the last attempt's outcome counts.
/// </summary>
internal sealed class RetryPlanner
{
    /// <summary>The policy and next attempt when this run is retried, or null.</summary>
    public (JobRetryPolicy Policy, int NextAttempt)? Next(IJobExecutionContext context, JobExecutionStatus status)
    {
        if (status != JobExecutionStatus.Failed || Retries.For(context.JobDetail) is not { } policy)
            return null;
        var attempt = Retries.Attempt(context);
        return attempt <= policy.MaxRetries ? (policy, attempt + 1) : null;
    }

    public bool WillRetry(IJobExecutionContext context, JobExecutionStatus status) => Next(context, status) != null;
}

/// <summary>
/// Schedules each retry as a one-off trigger of the same job. It carries the failed run's trigger data, so a run
/// started with different parameters is retried with them, and it is stored like any trigger, so in a cluster
/// any node may run it and it survives a restart.
/// </summary>
internal sealed class RetryListener(RetryPlanner planner, ILogger<RetryListener> logger) : IJobListener
{
    public string Name => "Cronlight.Retries";

    public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (planner.Next(context, RunOutcome.Status(context, jobException)) is not { } next)
            return;

        var (policy, attempt) = next;
        var key = context.JobDetail.Key;
        var delay = policy.Delay(attempt - 1);
        try
        {
            // The run's own trigger data, not its merged data: that holds resolved secret references
            var data = new JobDataMap();
            foreach (var (k, v) in context.Trigger.JobDataMap.Where(kv => !Retries.IsReserved(kv.Key)))
                data[k] = v;
            data[Retries.AttemptKey] = attempt.ToString();

            var trigger = TriggerBuilder.Create()
                .WithIdentity(Guid.NewGuid().ToString("N"), Retries.TriggerGroup)
                .WithDescription($"Retry {attempt - 1} of {policy.MaxRetries} for {key}")
                .ForJob(key)
                .UsingJobData(data)
                .WithPriority(context.Trigger.Priority)
                .StartAt(DateTimeOffset.UtcNow + delay)
                .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
                .Build();

            var scheduler = context.Scheduler;
            await scheduler.ScheduleJob(trigger, cancellationToken);
            // A job paused while it was failing stays paused, its retry included. Pausing it later pauses the
            // retry too, as one of its triggers; a manual run of a paused job is retried, as it was asked for.
            if (await scheduler.GetTriggerState(context.Trigger.Key, cancellationToken) == TriggerState.Paused)
                await scheduler.PauseTrigger(trigger.Key, cancellationToken);

            logger.LogInformation("Job {Job} failed on attempt {Attempt} of {Attempts}; retrying in {Delay}",
                key, attempt - 1, policy.MaxRetries + 1, delay);
        }
        catch (Exception ex) when (ex is SchedulerException or OperationCanceledException)
        {
            // History already shows the run as Retried and no alert was raised for it, so make the miss visible
            logger.LogError(ex, "Job {Job} failed and its retry could not be scheduled", key);
        }
    }

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RetryAttachment(RetryListener listener) : ISchedulerAttachment
{
    public Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        scheduler.AddJobListenerOnce(listener);
        return Task.CompletedTask;
    }
}
