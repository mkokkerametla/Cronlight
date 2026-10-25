using Quartz;

namespace Cronlight;

/// <summary>Keys Cronlight reads from or puts in <see cref="IJobExecutionContext"/>.</summary>
public static class CronlightContextKeys
{
    /// <summary>
    /// Why a run was vetoed, as a string. Cronlight sets it when a secret reference cannot be resolved, and
    /// execution history shows it as the run's error. A host's own trigger listener may set it too.
    /// </summary>
    public const string VetoReason = "Cronlight.VetoReason";

    /// <summary>Who cancelled the run from the dashboard, as a string. Execution history shows it.</summary>
    public const string CancelledBy = "Cronlight.CancelledBy";
}
