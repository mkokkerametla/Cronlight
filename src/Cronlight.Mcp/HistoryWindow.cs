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
