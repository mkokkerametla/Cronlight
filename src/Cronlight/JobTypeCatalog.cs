using System.Reflection;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight;

internal record JobParameterInfo(
    string Key,
    string Type,
    bool Required,
    string? Description,
    string? DefaultValue,
    IReadOnlyList<string>? Choices
);

internal record JobTypeInfo(
    string TypeName,
    string DisplayName,
    string? Description,
    bool Hidden,
    IReadOnlyList<JobParameterInfo> Parameters,
    // From [CronlightRetry]: what new jobs of this type start with, and what a job follows unless changed
    JobRetryPolicy? Retry = null
);

/// <summary>
/// Discovers IJob implementations in the host. Only types in this catalog can be created
/// through the API, so a caller can never instantiate an arbitrary type by name.
/// </summary>
internal class JobTypeCatalog
{
    // Framework and Quartz.NET assemblies: these names, and names under them ("System.Text.Json"). A host's own assembly
    // whose name only starts the same way ("QuartzJobs", "SystemSync") is scanned.
    private static readonly string[] FrameworkNames = { "System", "Microsoft", "Quartz", "netstandard", "mscorlib" };

    private readonly Lazy<Catalog> catalog;

    /// <param name="Types">Every type found, for the host's assemblies and each scheduler's own.</param>
    /// <param name="Shared">What schedulers without their own job assemblies offer.</param>
    /// <param name="BySchedulerName">What each scheduler with its own job assemblies offers.</param>
    private sealed record Catalog(
        Dictionary<string, (Type Type, JobTypeInfo Info)> Types,
        IReadOnlySet<string> Shared,
        Dictionary<string, IReadOnlySet<string>> BySchedulerName);

    public JobTypeCatalog(IOptions<CronlightOptions> options)
    {
        catalog = new(() => Discover(options.Value));
    }

    /// <summary>Every job type the dashboard knows, whichever scheduler offers it.</summary>
    public IEnumerable<JobTypeInfo> All => catalog.Value.Types.Values.Select(t => t.Info);

    /// <summary>The job types a scheduler offers for new jobs.</summary>
    public IEnumerable<JobTypeInfo> For(string schedulerName) =>
        Offered(schedulerName).Select(name => catalog.Value.Types[name].Info);

    /// <summary>Whether a scheduler offers a job type for new jobs.</summary>
    public bool Offers(string schedulerName, string typeName) => Offered(schedulerName).Contains(typeName);

    private IReadOnlySet<string> Offered(string schedulerName) =>
        catalog.Value.BySchedulerName.GetValueOrDefault(schedulerName) ?? catalog.Value.Shared;

    public bool TryGet(string typeName, out Type type, out JobTypeInfo info)
    {
        if (catalog.Value.Types.TryGetValue(typeName, out var entry))
        {
            (type, info) = entry;
            return true;
        }
        type = null!;
        info = null!;
        return false;
    }

    public JobTypeInfo? Describe(Type type) =>
        catalog.Value.Types.TryGetValue(type.FullName!, out var entry) ? entry.Info : null;

    private static Catalog Discover(CronlightOptions options)
    {
        var shared = JobTypes(options.JobAssemblies.Count > 0
            ? options.JobAssemblies
            : AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !IsFramework(a.GetName().Name!))
                .ToList());
        var bySchedulerName = options.Schedulers
            .Where(s => s.Value.JobAssemblies.Count > 0)
            .ToDictionary(s => s.Key, s => JobTypes(s.Value.JobAssemblies), StringComparer.OrdinalIgnoreCase);

        var types = shared.Concat(bySchedulerName.Values.SelectMany(t => t))
            .DistinctBy(t => t.FullName)
            .ToDictionary(t => t.FullName!, t => (t, ToInfo(t)));
        return new Catalog(types, Names(shared),
            bySchedulerName.ToDictionary(s => s.Key, s => Names(s.Value), StringComparer.OrdinalIgnoreCase));

        static IReadOnlySet<string> Names(List<Type> types) => types.Select(t => t.FullName!).ToHashSet();
    }

    private static List<Type> JobTypes(IEnumerable<Assembly> assemblies) =>
        assemblies
            .SelectMany(LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(IJob).IsAssignableFrom(t))
            .DistinctBy(t => t.FullName)
            .ToList();

    private static bool IsFramework(string assemblyName) =>
        FrameworkNames.Any(name => assemblyName == name || assemblyName.StartsWith(name + ".", StringComparison.Ordinal));

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    private static JobTypeInfo ToInfo(Type type)
    {
        var meta = type.GetCustomAttribute<CronlightJobAttribute>();
        var parameters = type.GetCustomAttributes<JobParameterAttribute>()
            .Select(p => new JobParameterInfo(p.Key, p.Type.ToString(), p.Required, p.Description, p.DefaultValue, p.Choices))
            .ToList();

        return new JobTypeInfo(
            type.FullName!,
            meta?.DisplayName ?? type.Name,
            meta?.Description,
            meta?.Hidden ?? false,
            parameters,
            Retries.ForType(type)
        );
    }
}
