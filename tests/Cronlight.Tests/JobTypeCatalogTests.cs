using Microsoft.Extensions.Options;
using QuartzSample.Jobs;

namespace Cronlight.Tests;

/// <summary>Which job types the catalog finds when the host does not list its assemblies (JobAssemblies left empty).</summary>
public class JobTypeCatalogTests
{
    private static JobTypeCatalog Scanning() => new(Options.Create(new CronlightOptions()));

    [Fact]
    public void Finds_job_types_in_a_host_assembly_whose_name_starts_like_a_framework_one()
    {
        // Loaded, as a host's own job types are by the time the dashboard is used
        Assert.Equal("QuartzSample.Jobs", typeof(PartnerCallJob).Assembly.GetName().Name);

        var info = Scanning().All.SingleOrDefault(t => t.TypeName == typeof(PartnerCallJob).FullName);

        // Found, so its secret parameter is masked and the type can be created from the dashboard
        Assert.NotNull(info);
        Assert.Equal("Partner call", info.DisplayName);
        Assert.Equal(nameof(JobParameterType.Secret), Assert.Single(info.Parameters).Type);
    }

    [Fact]
    public void Leaves_out_job_types_that_are_not_public()
    {
        var names = Scanning().All.Select(t => t.TypeName).ToList();

        Assert.DoesNotContain(typeof(InternalJob).FullName, names);
        // Cronlight's own wrapper for run logs
        Assert.DoesNotContain("Cronlight.History.RunLogJob", names);
        Assert.Contains(typeof(PlainJob).FullName, names);
    }

    [Fact]
    public void Leaves_out_framework_and_Quartz_assemblies()
    {
        var catalog = Scanning();
        var assemblies = catalog.All
            .Select(t => catalog.TryGet(t.TypeName, out var type, out _) ? type.Assembly.GetName().Name! : "")
            .ToHashSet();

        Assert.DoesNotContain(assemblies, name => name is "Quartz" or "netstandard"
            || name.StartsWith("Quartz.") || name.StartsWith("System.") || name.StartsWith("Microsoft."));
    }
}
