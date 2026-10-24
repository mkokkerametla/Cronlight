namespace Cronlight;

public enum JobHistoryStorage
{
    /// <summary>The Quartz database when the host uses a supported ADO store, otherwise in memory.</summary>
    Auto,

    /// <summary>The Quartz database; startup fails if the host's store cannot be used.</summary>
    Database,

    /// <summary>A bounded in-memory buffer per node. Lost on restart and not shared across a cluster.</summary>
    InMemory
}

public class JobHistoryOptions
{
    public JobHistoryStorage Storage { get; set; } = JobHistoryStorage.Auto;

    /// <summary>History table. Defaults to the Quartz table prefix + CRONLIGHT_HISTORY.</summary>
    public string? TableName { get; set; }

    /// <summary>
    /// Create the table on startup when it does not exist. Turn off where the app has no DDL rights
    /// and create it from the scripts in the package's Schema folder instead.
    /// </summary>
    public bool CreateTable { get; set; } = true;

    /// <summary>Records older than this are deleted. Null keeps everything.</summary>
    public TimeSpan? Retention { get; set; } = TimeSpan.FromDays(14);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Records kept per node by the in-memory store.</summary>
    public int InMemoryCapacity { get; set; } = 5000;

    /// <summary>Records waiting to be written. When full, new records are dropped rather than slowing jobs down.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Error messages are truncated to this length. Stack traces are never stored.</summary>
    public int MaxErrorLength { get; set; } = 2000;
}
