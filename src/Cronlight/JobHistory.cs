using System.Text.Json.Serialization;

namespace Cronlight;

[JsonConverter(typeof(JsonStringEnumConverter<JobExecutionStatus>))]
public enum JobExecutionStatus
{
    Running,
    Succeeded,
    Failed,
    Vetoed,

    /// <summary>Cancelled while running: its cancellation token was cancelled, from the dashboard or by the host.</summary>
    Cancelled,

    /// <summary>Failed, and another attempt is scheduled by the job's retry policy.</summary>
    Retried,

    /// <summary>
    /// Not a run: the trigger missed its fire time by more than Quartz's misfire threshold. The record's
    /// scheduled time is the fire time missed, its fired time when Quartz noticed, and its error says what Quartz
    /// did about it.
    /// </summary>
    Misfired
}

/// <summary>One execution of a job, as recorded by the Cronlight.History listener.</summary>
public record JobExecutionRecord
{
    public long Id { get; init; }
    public required string SchedulerName { get; init; }
    public required string FireInstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required string JobName { get; init; }
    public required string JobGroup { get; init; }
    public string? JobType { get; init; }
    public string? TriggerName { get; init; }
    public string? TriggerGroup { get; init; }
    public DateTimeOffset? ScheduledFireTimeUtc { get; init; }
    public required DateTimeOffset FiredTimeUtc { get; init; }
    public DateTimeOffset? FinishedTimeUtc { get; init; }
    public long? DurationMs { get; init; }
    public required JobExecutionStatus Status { get; init; }
    public string? Error { get; init; }
    public int RefireCount { get; init; }

    /// <summary>1 for a first attempt, 2 for its first automatic retry, and so on.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// The run's own data: its trigger's, such as the values of a run with different data, which its retries carry
    /// too. Null when the run used only the job's saved data. Sensitive values are masked, except secret references.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>Whether the run's log was kept: what the job logged while it ran. Read it with <see cref="IJobHistoryStore.GetLog"/>.</summary>
    public bool HasLog { get; init; }

    /// <summary>
    /// What the job logged while it ran, given to the store when the run finishes. Queries leave it out, since logs can be
    /// long, and set <see cref="HasLog"/> instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JobRunLogEntry>? Log { get; init; }
}

/// <summary>One line a job logged through <c>ILogger</c> while it ran.</summary>
/// <param name="Level">Trace, Debug, Information, Warning, Error or Critical.</param>
/// <param name="Category">The logger's category, usually the class that logged it.</param>
/// <param name="Message">
/// The formatted message, with the exception's type and message after it when one was logged (not its stack trace).
/// The run's sensitive job data values are masked.
/// </param>
public record JobRunLogEntry(DateTimeOffset TimeUtc, string Level, string Category, string Message);

public record JobHistoryQuery
{
    public string? JobGroup { get; init; }
    public string? JobName { get; init; }
    public JobExecutionStatus? Status { get; init; }

    /// <summary>Keyset paging: only records with an Id lower than this.</summary>
    public long? BeforeId { get; init; }

    public int Limit { get; init; } = 50;
}

public record JobHistorySummary(DateTimeOffset SinceUtc, int Succeeded, int Failed, int Vetoed, int Running);

/// <summary>
/// Storage for execution history. Implemented by the Cronlight.History package; the dashboard
/// shows history whenever an implementation is registered.
/// </summary>
public interface IJobHistoryStore
{
    /// <summary>Called once at startup; creates the storage if needed.</summary>
    Task Initialize(CancellationToken cancellationToken);

    Task RecordStarted(JobExecutionRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Completes the record started earlier for the same attempt, or inserts it when the start was never
    /// recorded. An attempt is identified by <see cref="JobExecutionRecord.FireInstanceId"/> together with
    /// <see cref="JobExecutionRecord.RefireCount"/>: a job refired immediately reuses its fire instance id.
    /// </summary>
    Task RecordFinished(JobExecutionRecord record, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<JobExecutionRecord>> Query(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken);

    Task<JobHistorySummary> Summarize(string schedulerName, DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes this scheduler's records fired before the cutoff and returns how many were removed.
    /// Scoped by scheduler so apps sharing a database keep their own retention.
    /// </summary>
    Task<int> DeleteFiredBefore(string schedulerName, DateTimeOffset cutoffUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The log kept with a run (<see cref="JobExecutionRecord.Log"/>), by its record's id, or null when it has none. A store
    /// that keeps no logs need not implement it.
    /// </summary>
    Task<IReadOnlyList<JobRunLogEntry>?> GetLog(string schedulerName, long id, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobRunLogEntry>?>(null);
}
