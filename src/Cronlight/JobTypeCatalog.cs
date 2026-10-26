using System.Reflection;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight;

public record JobParameterInfo(
    string Key,
    string Type,
    bool Required,
    string? Description,
    string? DefaultValue,
    IReadOnlyList<string>? Choices
);

public record JobTypeInfo(
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
public class JobTypeCatalog
{
    // Framework and Quartz.NET assemblies: these names, and names under them ("System.Text.Json"). A host's own assembly
    // whose name only starts the same way ("QuartzJobs", "SystemSync") is scanned.
    private static readonly string[] FrameworkNames = { "System", "Microsoft", "Quartz", "netstandard", "mscorlib" };

    private readonly Lazy<Dictionary<string, (Type Type, JobTypeInfo Info)>> types;

    public JobTypeCatalog(IOptions<CronlightOptions> options)
    {
        types = new(() => Discover(options.Value));
    }

    public IEnumerable<JobTypeInfo> All => types.Value.Values.Select(t => t.Info);

    public bool TryGet(string typeName, out Type type, out JobTypeInfo info)
    {
        if (types.Value.TryGetValue(typeName, out var entry))
        {
            (type, info) = entry;
            return true;
        }
        type = null!;
        info = null!;
        return false;
    }

    public JobTypeInfo? Describe(Type type) =>
        types.Value.TryGetValue(type.FullName!, out var entry) ? entry.Info : null;

    private static Dictionary<string, (Type, JobTypeInfo)> Discover(CronlightOptions options)
    {
        var assemblies = options.JobAssemblies.Count > 0
            ? options.JobAssemblies
            : AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !IsFramework(a.GetName().Name!))
                .ToList();

        return assemblies
            .SelectMany(LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(IJob).IsAssignableFrom(t))
            .DistinctBy(t => t.FullName)
            .ToDictionary(t => t.FullName!, t => (t, ToInfo(t)));
    }

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
