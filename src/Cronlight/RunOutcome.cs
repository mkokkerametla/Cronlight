using Quartz;

namespace Cronlight;

/// <summary>How a finished run ended, as history and alerts report it.</summary>
internal static class RunOutcome
{
    /// <summary>
    /// A run whose cancellation token was cancelled counts as cancelled however it ended: Quartz swallows the
    /// OperationCanceledException a cancelled job throws, so it cannot be told apart from a job that returned
    /// early because it was asked to stop.
    /// </summary>
    public static JobExecutionStatus Status(IJobExecutionContext context, JobExecutionException? exception) =>
        context.CancellationToken.IsCancellationRequested ? JobExecutionStatus.Cancelled
        : exception == null ? JobExecutionStatus.Succeeded
        : JobExecutionStatus.Failed;

    /// <summary>Why the run did not succeed, or null when it did.</summary>
    public static string? Error(IJobExecutionContext context, JobExecutionException? exception) =>
        Status(context, exception) switch
        {
            JobExecutionStatus.Succeeded => null,
            JobExecutionStatus.Cancelled => context.Get(CronlightContextKeys.CancelledBy) is string user ? $"Cancelled by {user}" : "Cancelled",
            _ => Describe(exception!),
        };

    // Quartz wraps what the job threw as JobExecutionException -> SchedulerException("Job threw an unhandled
    // exception") -> the real exception, so unwrap Quartz's layers. A JobExecutionException the job threw
    // itself has no inner exception and keeps its own message.
    // Only type and message are kept: stack traces are long and more likely to carry sensitive details.
    private static string Describe(JobExecutionException exception)
    {
        Exception cause = exception;
        while (cause is SchedulerException { InnerException: { } inner })
            cause = inner;
        return $"{cause.GetType().Name}: {cause.Message}";
    }
}
