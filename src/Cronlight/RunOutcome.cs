using Quartz;

namespace Cronlight;

/// <summary>How a finished run ended, as history and alerts report it.</summary>
internal static class RunOutcome
{
    /// <summary>
    /// Starts the error of a run history recorded as failed because it never recorded its end: the app stopped while it
    /// ran, or the node running it crashed. The run raised no alert, so a failure streak neither counts it nor ends at it.
    /// </summary>
    public const string DidNotFinish = "Did not finish: ";

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
            _ => Describe(exception!) + (context.Get(RetryPlanner.ProblemKey) is string problem ? $" (its retry could not be scheduled: {problem})" : ""),
        };

    // Quartz wraps what the job threw as JobExecutionException -> SchedulerException("Job threw an unhandled
    // exception") -> the real exception, so unwrap Quartz's layers. A JobExecutionException the job threw
    // itself has no inner exception and keeps its own message.
    // Only type and message are kept: stack traces are long and more likely to carry sensitive details.
    private static string Describe(JobExecutionException exception)
    {
        var cause = Cause(exception)!;
        return $"{cause.GetType().Name}: {cause.Message}";
    }

    /// <summary>What the job threw, inside Quartz's wrapping.</summary>
    public static Exception? Cause(Exception? exception)
    {
        while (exception is SchedulerException { InnerException: { } inner })
            exception = inner;
        return exception;
    }
}

/// <summary>
/// Quartz 3.17 and later do not tell job listeners that an attempt ended when the job asked to run again at once
/// (a JobExecutionException with RefireImmediately): the next attempt just begins, on the same context. Earlier
/// versions report the end first. A listener that pairs starts with ends calls <see cref="Begin"/> and
/// <see cref="End"/>, and learns from Begin that the attempt before ended unreported.
/// </summary>
internal static class Refires
{
    /// <summary>Marks an attempt as begun; true when the one before it on this context never reported its end.</summary>
    public static bool Begin(IJobExecutionContext context, string listener)
    {
        var key = Key(listener);
        var previousUnreported = context.RefireCount > 0 && context.Get(key) is true;
        context.Put(key, true);
        return previousUnreported;
    }

    public static void End(IJobExecutionContext context, string listener) => context.Put(Key(listener), false);

    private static string Key(string listener) => $"Cronlight.Attempting.{listener}";
}
