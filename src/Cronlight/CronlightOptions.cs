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

    /// <summary>Assemblies scanned for IJob implementations. Empty = all loaded non-framework assemblies.</summary>
    public List<Assembly> JobAssemblies { get; } = new();

    /// <summary>JobDataMap keys containing any of these (case-insensitive) are masked in API responses.</summary>
    public List<string> SensitiveKeyPatterns { get; } =
        new() { "password", "secret", "token", "apikey", "connectionstring" };
}
