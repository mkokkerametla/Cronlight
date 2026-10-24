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
    IReadOnlyList<JobParameterInfo> Parameters
);

/// <summary>
/// Discovers IJob implementations in the host. Only types in this catalog can be created
/// through the API, so a caller can never instantiate an arbitrary type by name.
/// </summary>
public class JobTypeCatalog
{
    private static readonly string[] FrameworkPrefixes = { "System", "Microsoft", "Quartz", "netstandard" };

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
                .Where(a => !a.IsDynamic && !FrameworkPrefixes.Any(p => a.GetName().Name!.StartsWith(p)))
                .ToList();

        return assemblies
            .SelectMany(LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(IJob).IsAssignableFrom(t))
            .DistinctBy(t => t.FullName)
            .ToDictionary(t => t.FullName!, t => (t, ToInfo(t)));
    }

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
            parameters
        );
    }
}
