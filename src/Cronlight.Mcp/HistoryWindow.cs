using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace Cronlight.Mcp;

/// <summary>A span of time the tools report on, as they echo it back so an answer can say what it covered.</summary>
internal record TimeWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc)
{
    public const int MaxDays = 90;

    [JsonIgnore]
    public TimeSpan Length => ToUtc - FromUtc;

    public bool Contains(DateTimeOffset time) => time >= FromUtc && time < ToUtc;

    /// <summary>The window of the same length just before this one, to compare with.</summary>
    [JsonIgnore]
    public TimeWindow Previous => new(FromUtc - Length, FromUtc);

    /// <summary>
    /// From fromUtc to toUtc when given; else the last <paramref name="hours"/> hours (<paramref name="defaultHours"/> when
    /// that is left out too), up to now or to toUtc.
    /// </summary>
    public static TimeWindow Resolve(int? hours, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int defaultHours = 24)
    {
        var to = toUtc ?? DateTimeOffset.UtcNow;
        var from = fromUtc ?? to.AddHours(-Math.Clamp(hours ?? defaultHours, 1, MaxDays * 24));
        if (from >= to)
            throw new McpException($"The window starts ({from:u}) at or after it ends ({to:u}).");
        if (to - from > TimeSpan.FromDays(MaxDays))
            throw new McpException($"The window is longer than {MaxDays} days.");
        return new TimeWindow(from, to);
    }
}

/// <summary>
/// Reads the runs history recorded in a window. The history store lists runs newest first, so this pages back from the
/// newest until the window's start, and stops at a cap: past it, results are marked truncated rather than slow.
/// </summary>
internal static class HistoryScan
{
    public const int MaxRecords = 20_000;
    private const int PageSize = 500;

    /// <param name="limit">Stop once this many runs in the window are read.</param>
    public static async Task<(List<JobExecutionRecord> Records, bool Truncated)> Read(IJobHistoryStore store, string scheduler,
        TimeWindow window, string? group, string? name, JobExecutionStatus? status, CancellationToken cancellationToken,
        int limit = MaxRecords, long? beforeId = null)
    {
        var records = new List<JobExecutionRecord>();
        var before = beforeId;
        var read = 0;
        while (true)
        {
            var page = await store.Query(scheduler,
                new JobHistoryQuery { JobGroup = group, JobName = name, Status = status, BeforeId = before, Limit = PageSize },
                cancellationToken);
            read += page.Count;
            foreach (var record in page.Where(r => window.Contains(r.FiredTimeUtc)))
            {
                records.Add(record);
                if (records.Count >= limit)
                    return (records, true);
            }
            // Records are in the order they were made, which is the order runs started, give or take a moment
            if (page.Count < PageSize || page[^1].FiredTimeUtc < window.FromUtc)
                return (records, false);
            if (read >= MaxRecords)
                return (records, true);
            before = page[^1].Id;
        }
    }
}
