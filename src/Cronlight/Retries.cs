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
/// <param name="RetryOn">
/// Retry only failures with one of these exception types, by full or short name, or a type derived from one. Null or
/// empty retries every failure.
/// </param>
public sealed record JobRetryPolicy(int MaxRetries, int DelaySeconds, bool Exponential = false, IReadOnlyList<string>? RetryOn = null)
{
    public const int MaxAllowedRetries = 20;
    public const int MaxDelaySeconds = 24 * 60 * 60;
    private const int MaxRetryOn = 20;

    /// <summary>What is wrong with the policy, or null when it can be used.</summary>
    public string? Problem() =>
        MaxRetries is < 0 or > MaxAllowedRetries ? $"Retries must be between 0 and {MaxAllowedRetries}"
        : MaxRetries > 0 && DelaySeconds is < 1 or > MaxDelaySeconds ? $"The retry delay must be between 1 and {MaxDelaySeconds} seconds"
        : RetryOn is { Count: > MaxRetryOn } ? $"Retries can be limited to at most {MaxRetryOn} exception types"
        : RetryOn?.FirstOrDefault(n => !IsTypeName(n)) is { } bad ? $"'{bad}' is not an exception type name"
        : null;

    /// <summary>Whether a failure with this exception is retried.</summary>
    public bool Covers(Exception? exception)
    {
        if (RetryOn is not { Count: > 0 })
            return true;
        for (var type = RunOutcome.Cause(exception)?.GetType(); type != null && type != typeof(object); type = type.BaseType)
            if (RetryOn.Any(n => n == type.FullName || n == type.Name))
                return true;
        return false;
    }

    private static bool IsTypeName(string name) =>
        name.Length is > 0 and <= 200 && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '`');

    // Compares the exception types by value, as the rest of the record is
    public bool Equals(JobRetryPolicy? other) =>
        other is not null && MaxRetries == other.MaxRetries && DelaySeconds == other.DelaySeconds && Exponential == other.Exponential
        && (RetryOn ?? []).SequenceEqual(other.RetryOn ?? []);

    public override int GetHashCode() => HashCode.Combine(MaxRetries, DelaySeconds, Exponential, RetryOn?.Count ?? 0);

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

    /// <summary>Retry only failures with these exception types, or types derived from them. Null retries every failure.</summary>
    public Type[]? RetryOn { get; set; }

    internal JobRetryPolicy Policy => new(MaxRetries, DelaySeconds, Exponential,
        RetryOn is { Length: > 0 } types ? types.Select(t => t.FullName ?? t.Name).ToList() : null);
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
/// Retries failed runs. The retry listener, history and alerts all ask it about each finished run, in whatever order
/// Quartz calls them; the first to ask schedules the retry and the others get the same answer. So a failure is
/// recorded as Retried, and raises no alert, only once its retry is really scheduled: when it cannot be, the run
/// counts as failed.
/// </summary>
internal sealed class RetryPlanner(ILogger<RetryPlanner> logger)
{
    // Context keys: whether this run's retry was scheduled, and why it could not be
    private const string ScheduledKey = "Cronlight.RetryScheduled";
    public const string ProblemKey = "Cronlight.RetryProblem";

    /// <summary>Whether this run is retried, scheduling the retry the first time it is asked.</summary>
    public async Task<bool> Retry(IJobExecutionContext context, JobExecutionException? exception, CancellationToken cancellationToken)
    {
        if (context.Get(ScheduledKey) is bool decided)
            return decided;
        var scheduled = Next(context, exception) is { } next && await Schedule(context, next.Policy, next.Attempt, cancellationToken);
        context.Put(ScheduledKey, scheduled);
        return scheduled;
    }

    /// <summary>The policy and next attempt when this run should be retried, or null.</summary>
    private static (JobRetryPolicy Policy, int Attempt)? Next(IJobExecutionContext context, JobExecutionException? exception)
    {
        if (RunOutcome.Status(context, exception) != JobExecutionStatus.Failed || Retries.For(context.JobDetail) is not { } policy
            || !policy.Covers(exception))
            return null;
        var attempt = Retries.Attempt(context);
        return attempt <= policy.MaxRetries ? (policy, attempt + 1) : null;
    }

    /// <summary>
    /// Schedules the retry as a one-off trigger of the same job. It carries the failed run's trigger data, so a run
    /// started with different parameters is retried with them, and it is stored like any trigger, so in a cluster
    /// any node may run it and it survives a restart.
    /// </summary>
    private async Task<bool> Schedule(IJobExecutionContext context, JobRetryPolicy policy, int attempt, CancellationToken cancellationToken)
    {
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
            return true;
        }
        catch (Exception ex) when (ex is SchedulerException or OperationCanceledException)
        {
            // The run then counts as failed: history records it so, with this reason, and alerts count it
            logger.LogError(ex, "Job {Job} failed and its retry could not be scheduled", key);
            context.Put(ProblemKey, ex.Message);
            return false;
        }
    }
}

/// <summary>Has every failed run considered for a retry, also when neither history nor alerts are registered.</summary>
internal sealed class RetryListener(RetryPlanner planner) : IJobListener
{
    public string Name => "Cronlight.Retries";

    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default) =>
        planner.Retry(context, jobException, cancellationToken);

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
