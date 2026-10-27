using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Quartz;

namespace Cronlight.Mcp;

/// <summary>
/// What the user calling a tool may see and change, by the dashboard's own rules. Checked in each tool rather than
/// only on the endpoint: if the app has an MCP server of its own, Cronlight's tools are served on its endpoint too.
/// </summary>
internal sealed class McpAccess(IHttpContextAccessor accessor, CronlightSchedulers schedulers, IOptions<CronlightOptions> options,
    IWebHostEnvironment environment)
{
    public CronlightOptions Options => options.Value;

    /// <summary>Execution history, which the tools about past runs need.</summary>
    public IJobHistoryStore History => Http.RequestServices.GetService<IJobHistoryStore>()
        ?? throw new McpException("Execution history is not enabled in this app, so there are no recorded runs.");

    /// <summary>The scheduler tools use when none is named: the app's own, whether or not the user may see it.</summary>
    public async Task<string> DefaultName(CancellationToken cancellationToken) =>
        (await schedulers.Default(cancellationToken)).SchedulerName;

    public HttpContext Http => accessor.HttpContext
        ?? throw new McpException("Cronlight's tools answer requests over HTTP only, where they know who is asking.");

    /// <summary>The scheduler by name (the host's own when null), if the user may see it.</summary>
    public async Task<IScheduler> View(string? scheduler, CancellationToken cancellationToken)
    {
        var http = Http;
        var o = options.Value;
        await RequireDashboard();
        var found = await schedulers.Find(scheduler, cancellationToken)
            ?? throw new McpException($"There is no scheduler '{scheduler}' here. list_schedulers lists those you may see.");
        if (!await DashboardApi.CanView(http, o, found.SchedulerName))
            throw new McpException($"Access denied: you may not see scheduler '{found.SchedulerName}'.");
        return found;
    }

    /// <summary>The dashboard's own rule: its ViewPolicy, or Development only without one.</summary>
    private async Task RequireDashboard()
    {
        var o = options.Value;
        if (o.ViewPolicy == null
                ? !environment.IsDevelopment() && !o.AllowAnonymousOutsideDevelopment
                : !await DashboardApi.Authorized(Http, o.ViewPolicy))
            throw new McpException("Access denied: you may not see this app's scheduled jobs.");
    }

    /// <summary>Whether the user may change jobs in the scheduler: also false in read-only mode.</summary>
    public Task<bool> CanManage(IScheduler scheduler) => DashboardApi.CanManage(Http, options.Value, scheduler.SchedulerName);

    /// <summary>The schedulers the user may see, the host's own first.</summary>
    public async Task<IReadOnlyList<IScheduler>> Visible(CancellationToken cancellationToken)
    {
        await RequireDashboard();
        var visible = new List<IScheduler>();
        foreach (var scheduler in await schedulers.All(cancellationToken))
            if (await DashboardApi.CanView(Http, options.Value, scheduler.SchedulerName))
                visible.Add(scheduler);
        return visible;
    }
}
