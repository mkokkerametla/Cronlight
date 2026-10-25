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
    /// Shows a box next to cron expressions where users describe a schedule in plain language and
    /// open it as a question in ChatGPT or Google. What they type goes to that site from their own
    /// browser (Cronlight's server sends nothing), so it is off by default: turn it on where staff may
    /// use external AI or search tools.
    /// </summary>
    public bool ShowScheduleAssistant { get; set; }

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
    /// Null (the default) turns references off, and the API refuses to save them.
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

    /// <summary>Assemblies scanned for IJob implementations. Empty = all loaded non-framework assemblies.</summary>
    public List<Assembly> JobAssemblies { get; } = new();

    /// <summary>JobDataMap keys containing any of these (case-insensitive) are masked in API responses.</summary>
    public List<string> SensitiveKeyPatterns { get; } =
        new() { "password", "secret", "token", "apikey", "connectionstring" };
}
