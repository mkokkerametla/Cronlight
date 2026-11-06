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

    /// <summary>Only records of runs that started at or after this time.</summary>
    public DateTimeOffset? FiredFromUtc { get; init; }

    /// <summary>Only records of runs that started before this time.</summary>
    public DateTimeOffset? FiredBeforeUtc { get; init; }

    public int Limit { get; init; } = 50;
}

/// <summary>
/// Runs counted by outcome, with durations and late starts. A store of its own that implements
/// <see cref="IJobHistoryStore.Statistics"/> sets the figures it has; figures added in later versions are left at their default.
/// </summary>
public record JobRunStatistics
{
    /// <summary>Runs that came to an end: succeeded, failed, blocked or cancelled. Retried attempts are not runs of their own.</summary>
    public int Runs { get; init; }

    public int Succeeded { get; init; }

    public int Failed { get; init; }

    /// <summary>Blocked before they ran, e.g. by a secret that could not be found.</summary>
    public int Vetoed { get; init; }

    public int Cancelled { get; init; }

    /// <summary>Attempts that failed and were retried; the run's outcome is its last attempt's.</summary>
    public int Retries { get; init; }

    /// <summary>Fire times the scheduler missed.</summary>
    public int Misfires { get; init; }

    /// <summary>Attempts still running.</summary>
    public int Running { get; init; }

    /// <summary>Succeeded out of succeeded, failed and blocked, from 0 to 1; null without any.</summary>
    public double? SuccessRate { get; init; }

    /// <summary>Over attempts that ran: succeeded, failed, retried and cancelled.</summary>
    public long? AverageDurationMs { get; init; }

    /// <summary>Half of those attempts took this long or less (nearest rank).</summary>
    public long? MedianDurationMs { get; init; }

    /// <summary>95% of those attempts took this long or less (nearest rank).</summary>
    public long? P95DurationMs { get; init; }

    public long? MaxDurationMs { get; init; }

    /// <summary>Attempts that started 5 seconds or more after their scheduled time.</summary>
    public int LateStarts { get; init; }

    /// <summary>How late the latest of them started.</summary>
    public long? MaxLateMs { get; init; }

    /// <summary>
    /// The store read the newest 20,000 records only, as one that does not count in its database does, so the figures
    /// cover those.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; init; }
}

/// <summary>
/// Storage for execution history. Implemented by the Cronlight.History package; the dashboard
/// shows history whenever an implementation is registered.
/// </summary>
/// <remarks>
/// Members added in a 1.x version come with a default body, so a store of its own keeps compiling; only a major version
/// adds one it must implement.
/// </remarks>
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

    /// <summary>
    /// Figures over the records the query picks (job, group, outcome and fired time; its paging is ignored). A store that
    /// does not implement it gets them from <see cref="Query"/>, reading up to 20,000 records, newest first.
    /// </summary>
    Task<JobRunStatistics> Statistics(string schedulerName, JobHistoryQuery query, CancellationToken cancellationToken) =>
        HistoryStatistics.Scan(this, schedulerName, query, cancellationToken);

    /// <summary>
    /// The last <paramref name="count"/> runs of each job (from 1 to 50), newest first, by its final outcome: attempts
    /// that were retried are left out, since a later attempt has the run's outcome, except the job's newest record, whose
    /// retry has not started yet. Jobs without runs are missing from the answer. A store that does not implement it reads
    /// each job's runs with <see cref="Query"/>, a few jobs at a time.
    /// </summary>
    async Task<IReadOnlyDictionary<(string Group, string Name), IReadOnlyList<JobExecutionRecord>>> RecentRuns(
        string schedulerName, IReadOnlyCollection<(string Group, string Name)> jobs, int count, CancellationToken cancellationToken)
    {
        var runs = new System.Collections.Concurrent.ConcurrentDictionary<(string Group, string Name), IReadOnlyList<JobExecutionRecord>>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(jobs.Distinct(), parallel, async (job, ct) =>
        {
            // Enough to fill the count past a few retried attempts
            var records = await Query(schedulerName,
                new JobHistoryQuery { JobGroup = job.Group, JobName = job.Name, Limit = HistoryStatistics.RecentRunLimit(count) * 4 }, ct);
            // Checked here too, for a store that does not filter by job
            var recent = HistoryStatistics.RecentRuns(records.Where(r => r.JobGroup == job.Group && r.JobName == job.Name), count);
            if (recent.Count > 0)
                runs[job] = recent;
        });
        return runs;
    }

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
