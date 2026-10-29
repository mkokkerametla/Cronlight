using Microsoft.AspNetCore.Routing;

namespace Cronlight;

/// <summary>
/// An area an add-on package adds to the dashboard, such as the assistant: its endpoints go in the dashboard's API, under
/// its permissions, and the dashboard shows the area when the scheduler endpoint lists <see cref="Feature"/>.
/// </summary>
internal abstract class DashboardExtension
{
    /// <summary>The name the dashboard knows the area by.</summary>
    public abstract string Feature { get; }

    /// <summary>Maps the area's endpoints on the API group, which already requires the dashboard's view permission.</summary>
    public abstract void Map(RouteGroupBuilder api, CronlightOptions options);
}
