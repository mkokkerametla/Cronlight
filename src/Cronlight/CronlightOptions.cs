using System.Reflection;

namespace Cronlight;

public class CronlightOptions
{
    /// <summary>Authorization policy required to view the dashboard and read the API.</summary>
    public string? ViewPolicy { get; set; }

    /// <summary>Authorization policy required for mutations. Falls back to <see cref="ViewPolicy"/>.</summary>
    public string? ManagePolicy { get; set; }

    /// <summary>When true, every mutating endpoint returns 403.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Settings for single schedulers, by scheduler name: who may see and change each, on top of
    /// <see cref="ViewPolicy"/> and <see cref="ManagePolicy"/>, and which job types it offers. E.g.
    /// <c>o.Schedulers["Billing"].ViewPolicy = "BillingTeam"</c> shows the Billing scheduler only to that policy, and
    /// <c>o.Schedulers["Billing"].JobAssemblies.Add(typeof(InvoiceJob).Assembly)</c> offers only its app's job types.
    /// A name is added when first used. Schedulers not listed are open to everyone the dashboard admits and offer
    /// every job type.
    /// </summary>
    public CronlightSchedulerOptionsByName Schedulers { get; } = new();

    /// <summary>
    /// Without a policy the dashboard is only reachable in the Development environment.
    /// Set this to opt out of that safety net (e.g. when protected by a reverse proxy).
    /// </summary>
    public bool AllowAnonymousOutsideDevelopment { get; set; }

    /// <summary>
    /// Jobs whose type cannot be loaded in this process (typically owned by another app sharing
    /// the scheduler) are read-only by default. Set to true to allow pause/resume/delete on them.
    /// </summary>
    public bool AllowManagingUnloadableJobs { get; set; }

    /// <summary>
    /// How often an open dashboard asks for fresh jobs, running jobs and history, in seconds (1 to 300). Each
    /// open tab polls, while visible, so a shorter interval puts more load on the scheduler's database.
    /// </summary>
    public int RefreshSeconds { get; set; } = 10;

    /// <summary>
    /// Turns on secret references. A secret parameter (or one matching <see cref="SensitiveKeyPatterns"/>) set to
    /// <c>secret:Smtp:Password</c> stores only that name, and the value is read from configuration key
    /// <c>{SecretsSection}:Smtp:Password</c> just before each run, so the database never holds the secret.
    /// Any configuration source works: appsettings, user secrets, environment variables or Azure Key Vault
    /// (a vault secret named <c>JobSecrets--Smtp--Password</c> for section <c>JobSecrets</c>). Only keys in this
    /// section can be referenced, so people who can edit jobs cannot read the rest of the configuration.
    /// Null (the default) turns references off, and the API refuses to save them. A scheduler given its own
    /// <see cref="CronlightSchedulerOptions.SecretsSection"/> reads that instead.
    /// </summary>
    public string? SecretsSection { get; set; }

    /// <summary>The Content-Security-Policy the dashboard sends unless changed.</summary>
    public const string DefaultContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// Content-Security-Policy for the dashboard's pages and API. The default allows only the dashboard's
    /// own files and API, and no embedding in other sites. To show the dashboard inside your own portal,
    /// change frame-ancestors, e.g. <c>DefaultContentSecurityPolicy.Replace("frame-ancestors 'none'",
    /// "frame-ancestors https://portal.contoso.com")</c>. Null sends none, for hosts that set their own.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } = DefaultContentSecurityPolicy;

    /// <summary>
    /// Assemblies scanned for IJob implementations. Empty = all loaded non-framework assemblies. A scheduler given
    /// its own <see cref="CronlightSchedulerOptions.JobAssemblies"/> offers those instead.
    /// </summary>
    public List<Assembly> JobAssemblies { get; } = new();

    /// <summary>JobDataMap keys containing any of these (case-insensitive) are masked in API responses.</summary>
    public List<string> SensitiveKeyPatterns { get; } =
        new() { "password", "secret", "token", "apikey", "connectionstring" };
}

/// <summary>
/// Settings per scheduler, by scheduler name, whatever its case: configuration keys ignore case, and a name that did not
/// match would leave the scheduler open. Reading a name not yet listed adds it.
/// </summary>
public class CronlightSchedulerOptionsByName() : Dictionary<string, CronlightSchedulerOptions>(StringComparer.OrdinalIgnoreCase)
{
    /// <summary>The scheduler's settings, added when first read, so they can be set without creating them first.</summary>
    public new CronlightSchedulerOptions this[string schedulerName]
    {
        get => TryGetValue(schedulerName, out var options) ? options : base[schedulerName] = new();
        set => base[schedulerName] = value;
    }
}

/// <summary>
/// Who may see and change one scheduler, in addition to the dashboard-wide policies, the job types it offers, and where
/// its secret references are read from.
/// </summary>
public class CronlightSchedulerOptions
{
    /// <summary>Required to see the scheduler: it is left out of the scheduler picker, and its API refuses others.</summary>
    public string? ViewPolicy { get; set; }

    /// <summary>Required to change its jobs. Falls back to <see cref="ViewPolicy"/>.</summary>
    public string? ManagePolicy { get; set; }

    /// <summary>
    /// The configuration section its jobs' secret references are read from, in place of
    /// <see cref="CronlightOptions.SecretsSection"/>: people who may change only this scheduler's jobs can reference only
    /// its secrets. Also turns references on for this scheduler alone. Null = the dashboard's section.
    /// </summary>
    public string? SecretsSection { get; set; }

    /// <summary>
    /// Assemblies whose job types this scheduler offers in New job, in place of <see cref="CronlightOptions.JobAssemblies"/>:
    /// for a dashboard that manages the schedulers of several apps, each app's job library. Empty = the same as every
    /// other scheduler. Jobs of other types already in the scheduler are still shown and edited.
    /// </summary>
    public List<Assembly> JobAssemblies { get; } = new();
}
