using System.Text.Json.Serialization;

namespace Cronlight;

[JsonConverter(typeof(JsonStringEnumConverter<JobExecutionStatus>))]
public enum JobExecutionStatus
{
    Running,
    Succeeded,
    Failed,
    Vetoed
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
}

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
}
