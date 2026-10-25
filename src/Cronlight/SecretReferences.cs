using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cronlight;

/// <summary>Keys Cronlight reads from or puts in <see cref="IJobExecutionContext"/>.</summary>
public static class CronlightContextKeys
{
    /// <summary>
    /// Why a run was vetoed, as a string. Cronlight sets it when a secret reference cannot be resolved, and
    /// execution history shows it as the run's error. A host's own trigger listener may set it too.
    /// </summary>
    public const string VetoReason = "Cronlight.VetoReason";
}

/// <summary>
/// Secret references: a sensitive job data value such as <c>secret:Smtp:Password</c> stores only a name,
/// and the value is read from configuration under <see cref="CronlightOptions.SecretsSection"/> when the job runs.
/// </summary>
internal static class SecretReferences
{
    public const string Prefix = "secret:";

    // Configuration key segments, separated by ':'. No empty segments, so a name cannot leave the section.
    private static readonly Regex Name = new(@"^[A-Za-z0-9_.\-]+(:[A-Za-z0-9_.\-]+)*$", RegexOptions.CultureInvariant);

    public static bool IsReference(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool Enabled(CronlightOptions options) => !string.IsNullOrWhiteSpace(options.SecretsSection);

    /// <summary>The value a reference names, or null with the reason it cannot be resolved.</summary>
    public static string? Resolve(string reference, CronlightOptions options, IConfiguration configuration, out string? error)
    {
        var name = reference[Prefix.Length..];
        if (!Enabled(options))
        {
            error = $"Secret references are not enabled, so '{reference}' cannot be used. Set Cronlight's SecretsSection to turn them on.";
            return null;
        }
        if (!Name.IsMatch(name))
        {
            error = $"'{reference}' is not a valid secret reference. Use secret:<name>, e.g. secret:Smtp:Password.";
            return null;
        }
        var value = configuration[$"{options.SecretsSection}:{name}"];
        if (string.IsNullOrEmpty(value))
        {
            error = $"No secret named '{name}' is configured (configuration key {options.SecretsSection}:{name}).";
            return null;
        }
        error = null;
        return value;
    }

    /// <summary>The first reference among sensitive values that cannot be resolved, as an error; null when all can.</summary>
    public static string? Check(IEnumerable<KeyValuePair<string, string>> data, Func<string, bool> isSensitive,
        CronlightOptions options, IConfiguration configuration)
    {
        foreach (var (key, value) in data)
        {
            if (isSensitive(key) && IsReference(value) && Resolve(value, options, configuration, out var error) == null)
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
    JobTypeCatalog catalog,
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
            var secretKeys = DashboardApi.SecretKeys(catalog.Describe(context.JobDetail.JobType));
            var data = context.MergedJobDataMap;
            foreach (var key in data.Keys.ToList())
            {
                if (data[key] is not string value || !SecretReferences.IsReference(value)
                    || !DashboardApi.IsSensitive(key, secretKeys, options.Value))
                    continue;
                var resolved = SecretReferences.Resolve(value, options.Value, configuration, out var error);
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

/// <summary>Attaches <see cref="SecretReferenceListener"/> before Quartz starts, when secret references are on.</summary>
internal sealed class SecretReferenceRegistration(
    ISchedulerFactory schedulerFactory,
    SecretReferenceListener listener,
    IOptions<CronlightOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!SecretReferences.Enabled(options.Value))
            return;
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        if (scheduler.ListenerManager.GetTriggerListeners().All(l => l.Name != listener.Name))
            scheduler.ListenerManager.AddTriggerListener(listener, GroupMatcher<TriggerKey>.AnyGroup());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
