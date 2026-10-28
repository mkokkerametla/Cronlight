namespace Cronlight;

/// <summary>
/// Counts runs the way the dashboard, the MCP server and every history store count them: a run by its outcome, a
/// retried attempt as a retry, and durations over the attempts that ran.
/// </summary>
internal static class HistoryStatistics
{
    /// <summary>An attempt that began this long after its scheduled time started late.</summary>
    public static readonly TimeSpan LateStart = TimeSpan.FromSeconds(5);

    /// <summary>The outcomes of attempts that ran, whose durations are counted.</summary>
    public static readonly JobExecutionStatus[] Timed =
        [JobExecutionStatus.Succeeded, JobExecutionStatus.Failed, JobExecutionStatus.Retried, JobExecutionStatus.Cancelled];

    /// <summary>The figures for some runs of one job, or of many.</summary>
    public static JobRunStatistics Compute(IReadOnlyCollection<JobExecutionRecord> records, bool truncated = false)
    {
        var durations = records
            .Where(r => r.DurationMs != null && Timed.Contains(r.Status))
            .Select(r => r.DurationMs!.Value)
            .Order()
            .ToList();
        // A misfire's fired time is when Quartz noticed it, not a start
        var late = records
            .Where(r => r.Status != JobExecutionStatus.Misfired && r.ScheduledFireTimeUtc != null)
            .Select(r => (r.FiredTimeUtc - r.ScheduledFireTimeUtc!.Value).Ticks)
            .Where(ticks => ticks >= LateStart.Ticks)
            .ToList();
        int Count(JobExecutionStatus status) => records.Count(r => r.Status == status);

        return Create(Count, durations.Count, durations.Sum(), durations.Count == 0 ? null : durations[^1],
            k => durations[k], late.Count, late.Count == 0 ? null : late.Max(), truncated);
    }

    /// <summary>
    /// The figures from counts, as a store that counts in its database has them.
    /// </summary>
    /// <param name="count">Records by outcome.</param>
    /// <param name="timed">Attempts that ran and have a duration.</param>
    /// <param name="durationAt">The duration at this place, from 0, in the timed attempts' durations ordered shortest first.</param>
    /// <param name="maxLateTicks">The latest start of those that started late.</param>
    public static JobRunStatistics Create(Func<JobExecutionStatus, int> count, int timed, long totalDurationMs, long? maxDurationMs,
        Func<int, long> durationAt, int lateStarts, long? maxLateTicks, bool truncated = false)
    {
        var succeeded = count(JobExecutionStatus.Succeeded);
        var failed = count(JobExecutionStatus.Failed);
        var vetoed = count(JobExecutionStatus.Vetoed);
        var cancelled = count(JobExecutionStatus.Cancelled);
        var decided = succeeded + failed + vetoed;
        // Nearest rank
        long? Percentile(double p) => timed == 0 ? null : durationAt(Math.Max(0, (int)Math.Ceiling(p * timed) - 1));

        return new JobRunStatistics(decided + cancelled, succeeded, failed, vetoed, cancelled, count(JobExecutionStatus.Retried),
            count(JobExecutionStatus.Misfired), count(JobExecutionStatus.Running),
            decided == 0 ? null : Math.Round((double)succeeded / decided, 4),
            timed == 0 ? null : totalDurationMs / timed, Percentile(0.5), Percentile(0.95), maxDurationMs, lateStarts,
            maxLateTicks / TimeSpan.TicksPerMillisecond)
        {
            Truncated = truncated
        };
    }

    /// <summary>How many of a job's last runs a store reads at most, whatever it is asked for.</summary>
    public static int RecentRunLimit(int count) => Math.Clamp(count, 1, 50);

    /// <summary>
    /// A job's last runs from its records, newest first: each run by its final outcome, so a retried attempt is left out,
    /// unless it is the newest record, whose retry has not started yet.
    /// </summary>
    public static List<JobExecutionRecord> RecentRuns(IEnumerable<JobExecutionRecord> newestFirst, int count) =>
        newestFirst.Where((r, i) => i == 0 || r.Status != JobExecutionStatus.Retried).Take(RecentRunLimit(count)).ToList();

    /// <summary>The figures read from the store's records, for a store that does not count them itself.</summary>
    public static async Task<JobRunStatistics> Scan(IJobHistoryStore store, string schedulerName, JobHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var (records, truncated) = await HistoryScan.Read(store, schedulerName, query.FiredFromUtc, query.FiredBeforeUtc, query.JobGroup,
            query.JobName, query.Status, cancellationToken);
        return Compute(records, truncated);
    }
}

/// <summary>
/// Reads the runs history recorded in a window, newest first, asking the store for that window only, and stops at a cap:
/// past it, results are marked truncated rather than slow. Records are checked against the window here too, for a store
/// that does not filter by time.
/// </summary>
internal static class HistoryScan
{
    public const int MaxRecords = 20_000;
    private const int PageSize = 500;

    /// <param name="fromUtc">Runs that started at or after this time; every run when null.</param>
    /// <param name="toUtc">Runs that started before this time; up to now when null.</param>
    /// <param name="limit">
    /// Stop once this many runs in the window are read. Truncated then means another run follows them, not only that the
    /// limit was reached.
    /// </param>
    public static async Task<(List<JobExecutionRecord> Records, bool Truncated)> Read(IJobHistoryStore store, string scheduler,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? group, string? name, JobExecutionStatus? status,
        CancellationToken cancellationToken, int limit = MaxRecords, long? beforeId = null)
    {
        var records = new List<JobExecutionRecord>();
        var before = beforeId;
        var read = 0;
        while (true)
        {
            var page = await store.Query(scheduler, new JobHistoryQuery
            {
                JobGroup = group, JobName = name, Status = status, BeforeId = before, Limit = PageSize,
                FiredFromUtc = fromUtc, FiredBeforeUtc = toUtc,
            }, cancellationToken);
            read += page.Count;
            foreach (var record in page.Where(r => (fromUtc == null || r.FiredTimeUtc >= fromUtc) && (toUtc == null || r.FiredTimeUtc < toUtc)))
            {
                if (records.Count >= limit)
                    return (records, true);
                records.Add(record);
            }
            // Records are in the order they were made, which is the order runs started, give or take a moment
            if (page.Count < PageSize || (fromUtc != null && page[^1].FiredTimeUtc < fromUtc))
                return (records, false);
            // Past the cap by one page at most, which says whether a window holding exactly the cap has more
            if (read > MaxRecords)
                return (records, true);
            before = page[^1].Id;
        }
    }
}
