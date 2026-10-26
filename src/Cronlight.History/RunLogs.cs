using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Spi;

namespace Cronlight.History;

/// <summary>
/// What one run logs. Its job is wrapped by <see cref="RunLogJobFactory"/>, which makes this the run's log for the code
/// the job runs, awaited or not, and <see cref="RunLogProvider"/> adds what that code logs through ILogger.
/// </summary>
/// <param name="mask">Masks the run's secrets in a message. Applied before a long message is cut, so no part of a secret is kept.</param>
internal sealed class RunLog(int maxEntries, int maxMessageLength, Func<string, string> mask)
{
    public const string ContextKey = "Cronlight.RunLog";

    private static readonly AsyncLocal<RunLog?> current = new();

    private List<JobRunLogEntry> entries = [];
    private readonly object gate = new();
    private int dropped;
    private volatile bool closed;

    /// <summary>
    /// The log of the run whose code is executing, while that run is going on. Work the run started can outlive it (a
    /// timer or loop it set going), and still sees the log afterwards, closed.
    /// </summary>
    public static RunLog? Current => current.Value is { closed: false } log ? log : null;

    /// <summary>Makes this the current log for the rest of the caller's async method, and what it calls or starts.</summary>
    public void Enter() => current.Value = this;

    public void Add(LogLevel level, string category, string message)
    {
        lock (gate)
        {
            if (closed)
                return;
            if (entries.Count >= maxEntries)
            {
                dropped++;
                return;
            }
            message = mask(message);
            entries.Add(new JobRunLogEntry(DateTimeOffset.UtcNow, level.ToString(), category,
                message.Length <= maxMessageLength ? message : message[..(maxMessageLength - 1)] + "…"));
        }
    }

    /// <summary>
    /// Ends the log and returns it, or null when nothing was logged. What work the job left running logs after its
    /// run has finished is not kept, and the closed log lets go of its entries.
    /// </summary>
    public IReadOnlyList<JobRunLogEntry>? Close()
    {
        lock (gate)
        {
            closed = true;
            var result = entries;
            entries = [];
            if (result.Count == 0)
                return null;
            if (dropped > 0)
                result.Add(new JobRunLogEntry(DateTimeOffset.UtcNow, nameof(LogLevel.Warning), "Cronlight",
                    $"{dropped} more log entries were not kept: a run keeps at most {maxEntries} (JobHistoryOptions.MaxLogEntries)"));
            return result;
        }
    }
}

/// <summary>
/// Masks a run's sensitive values wherever its job logged them, as the API masks them in job data. The merged data is
/// used, since it holds the values the job reads, resolved secret references included.
/// </summary>
internal sealed class RunLogMasks(IServiceProvider services)
{
    // Registered by AddCronlight; without it, no job type says which values are secret
    private readonly JobTypeCatalog? catalog = services.GetService<JobTypeCatalog>();
    private readonly IOptions<CronlightOptions> cronlight = services.GetRequiredService<IOptions<CronlightOptions>>();

    public Func<string, string> For(IJobExecutionContext context)
    {
        var secretKeys = DashboardApi.SecretKeys(catalog?.Describe(context.JobDetail.JobType));
        var secrets = context.MergedJobDataMap
            .Where(kv => DashboardApi.IsSensitive(kv.Key, secretKeys, cronlight.Value))
            .Select(kv => kv.Value?.ToString())
            // Very short values would mask unrelated text, and are no secret worth the name
            .Where(v => v is { Length: >= 4 } && v != DashboardApi.Mask)
            .Distinct()
            .OrderByDescending(v => v!.Length)
            .ToList();
        return secrets.Count == 0
            ? message => message
            : message => secrets.Aggregate(message, (text, secret) => text.Replace(secret!, DashboardApi.Mask, StringComparison.Ordinal));
    }
}

/// <summary>
/// Adds what code logs to the log of the run it belongs to; logs outside runs are ignored. The host's logging filters
/// apply as to any provider, under the alias CronlightRunLog, e.g. <c>Logging:CronlightRunLog:LogLevel:Default</c>.
/// </summary>
[ProviderAlias("CronlightRunLog")]
internal sealed class RunLogProvider(IOptions<JobHistoryOptions> options) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RunLogger(categoryName, options.Value.MinimumLogLevel);

    public void Dispose()
    {
    }

    private sealed class RunLogger(string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None && RunLog.Current != null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || RunLog.Current is not { } log)
                return;
            var message = formatter(state, exception);
            // Type and message only, as history keeps errors: stack traces are long and more likely to carry sensitive details
            if (exception != null)
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            log.Add(logLevel, category, message);
        }
    }
}

/// <summary>Wraps each job the scheduler creates, so that what its run logs is kept with it in history.</summary>
internal sealed class RunLogJobFactory(IJobFactory inner, IOptions<JobHistoryOptions> options, RunLogMasks masks) : IJobFactory
{
    public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler) => new RunLogJob(inner.NewJob(bundle, scheduler), options.Value, masks);

    public void ReturnJob(IJob job) => inner.ReturnJob(job is RunLogJob wrapper ? wrapper.Job : job);
}

/// <summary>
/// Runs the job with a log of its own. The log is set inside this async method, so it reaches everything the job does
/// and is gone again once it returns. <see cref="IJobExecutionContext.JobInstance"/> is this wrapper; <see cref="Job"/>
/// is the job.
/// </summary>
internal sealed class RunLogJob(IJob job, JobHistoryOptions options, RunLogMasks masks) : IJob
{
    /// <summary>What an attempt that asked to run again at once threw: Quartz 3.17 and later do not pass it to listeners.</summary>
    public const string RefireErrorKey = "Cronlight.RefireError";

    public IJob Job => job;

    public async Task Execute(IJobExecutionContext context)
    {
        // Secret references are resolved by now: Quartz calls trigger listeners before the job runs
        var log = new RunLog(Math.Max(1, options.MaxLogEntries), Math.Max(1, options.MaxLogMessageLength), masks.For(context));
        context.Put(RunLog.ContextKey, log);
        log.Enter();
        try
        {
            await job.Execute(context);
        }
        catch (JobExecutionException ex) when (ex.RefireImmediately)
        {
            context.Put(RefireErrorKey, ex);
            throw;
        }
    }
}
