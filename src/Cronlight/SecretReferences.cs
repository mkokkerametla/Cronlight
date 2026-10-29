using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight;

/// <summary>
/// Secret references: a sensitive job data value such as <c>secret:Smtp:Password</c> stores only a name,
/// and the value is read from configuration under the scheduler's section (see <see cref="Section"/>) when the job runs.
/// </summary>
internal static class SecretReferences
{
    public const string Prefix = "secret:";

    // Configuration key segments, separated by ':'. No empty segments, so a name cannot leave the section.
    private static readonly Regex Name = new(@"^[A-Za-z0-9_.\-]+(:[A-Za-z0-9_.\-]+)*$", RegexOptions.CultureInvariant);

    public static bool IsReference(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The configuration section a scheduler's references are read from: its own
    /// <see cref="CronlightSchedulerOptions.SecretsSection"/>, else the dashboard's; null when references are off for it.
    /// </summary>
    public static string? Section(CronlightOptions options, string schedulerName) =>
        // TryGetValue, since reading Schedulers by name would add the scheduler
        options.Schedulers.TryGetValue(schedulerName, out var scheduler) && !string.IsNullOrWhiteSpace(scheduler.SecretsSection)
            ? scheduler.SecretsSection
            : string.IsNullOrWhiteSpace(options.SecretsSection) ? null : options.SecretsSection;

    public static bool Enabled(CronlightOptions options, string schedulerName) => Section(options, schedulerName) != null;

    /// <summary>The value a reference in the scheduler's jobs names, or null with the reason it cannot be resolved.</summary>
    public static string? Resolve(string reference, CronlightOptions options, string schedulerName, IConfiguration configuration,
        out string? error)
    {
        var name = reference[Prefix.Length..];
        if (Section(options, schedulerName) is not { } section)
        {
            error = $"Secret references are not enabled, so '{reference}' cannot be used. Set Cronlight's SecretsSection, or the " +
                    "scheduler's, to turn them on.";
            return null;
        }
        if (!Name.IsMatch(name))
        {
            error = $"'{reference}' is not a valid secret reference. Use secret:<name>, e.g. secret:Smtp:Password.";
            return null;
        }
        var value = configuration[$"{section}:{name}"];
        if (string.IsNullOrEmpty(value))
        {
            error = $"No secret named '{name}' is configured (configuration key {section}:{name}).";
            return null;
        }
        error = null;
        return value;
    }

    /// <summary>The first reference among sensitive values that cannot be resolved, as an error; null when all can.</summary>
    public static string? Check(IEnumerable<KeyValuePair<string, string>> data, Func<string, bool> isSensitive,
        CronlightOptions options, string schedulerName, IConfiguration configuration)
    {
        foreach (var (key, value) in data)
        {
            if (isSensitive(key) && IsReference(value) && Resolve(value, options, schedulerName, configuration, out var error) == null)
                return $"{key}: {error}";
        }
        return null;
    }
}

/// <summary>
/// Replaces secret references in each run's merged job data just before the job runs. The run's copy is
/// changed, never the stored job, so the value is not written back to the database. A reference that
/// cannot be resolved vetoes the run, rather than running the job with the reference as its password.
/// </summary>
internal sealed class SecretReferenceListener(
    IConfiguration configuration,
    IOptions<CronlightOptions> options,
    ILogger<SecretReferenceListener> logger) : ITriggerListener
{
    public string Name => "Cronlight.SecretReferences";

    public Task<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        // An exception here would stop Quartz from completing the trigger, so every failure becomes a veto
        try
        {
            var secretKeys = DashboardApi.SecretKeys(context.JobDetail.JobType);
            var data = context.MergedJobDataMap;
            foreach (var key in data.Keys.ToList())
            {
                if (data[key] is not string value || !SecretReferences.IsReference(value)
                    || !DashboardApi.IsSensitive(key, secretKeys, options.Value))
                    continue;
                var resolved = SecretReferences.Resolve(value, options.Value, context.Scheduler.SchedulerName, configuration, out var error);
                if (resolved == null)
                    return Task.FromResult(Veto(context, $"{key}: {error}"));
                data[key] = resolved;
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving secret references failed for job {Job}", context.JobDetail.Key);
            return Task.FromResult(Veto(context, $"Secret references could not be resolved: {ex.Message}"));
        }
    }

    private bool Veto(IJobExecutionContext context, string reason)
    {
        logger.LogError("Job {Job} was not run: {Reason}", context.JobDetail.Key, reason);
        context.Put(CronlightContextKeys.VetoReason, reason);
        return true;
    }

    public Task TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction triggerInstructionCode,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Attaches <see cref="SecretReferenceListener"/>, when secret references are on for the scheduler.</summary>
internal sealed class SecretReferenceAttachment(SecretReferenceListener listener, IOptions<CronlightOptions> options) : ISchedulerAttachment
{
    public Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        if (SecretReferences.Enabled(options.Value, scheduler.SchedulerName))
            scheduler.AddTriggerListenerOnce(listener);
        return Task.CompletedTask;
    }
}
