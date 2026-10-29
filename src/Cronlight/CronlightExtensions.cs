using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cronlight;

public static class CronlightExtensions
{
    /// <summary>
    /// Registers dashboard services. Expects the host to have already called AddQuartz(),
    /// the dashboard reuses the host's ISchedulerFactory and never configures a store itself.
    /// </summary>
    public static IServiceCollection AddCronlight(
        this IServiceCollection services,
        Action<CronlightOptions>? configure = null
    )
    {
        services.AddOptions<CronlightOptions>();
        if (configure != null)
            services.Configure(configure);

        services.AddSingleton<JobTypeCatalog>();
        services.TryAddSingleton<QuartzDataSource>();
        services.TryAddSingleton<RunningJobsReader>();
        services.TryAddSingleton<ClusterNodesReader>();
        services.AddSingleton<JobIndexReader>();
        services.TryAddSingleton<SecretReferenceListener>();
        services.TryAddSingleton<RetryPlanner>();
        services.TryAddSingleton<RetryListener>();
        services.AddSchedulerAttachment<RetryAttachment>();
        services.AddSchedulerAttachment<SecretReferenceAttachment>();
        // Costs nothing until something listens to the meter (CronlightMetrics.MeterName)
        services.AddMetrics();
        services.TryAddSingleton<MetricsListener>();
        services.AddSchedulerAttachment<MetricsAttachment>();
        services.AddJobNotCreatedHandler<MetricsListener>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, SecurityHeadersStartupFilter>());
        services.AddCronlightOpenApi();
        return services;
    }

    /// <summary>
    /// Sends an alert when a job fails <see cref="CronlightAlertOptions.AfterConsecutiveFailures"/> times in a row,
    /// and when it recovers, to the configured webhooks (Slack, Teams or JSON), email and any registered
    /// <see cref="ICronlightAlertSink"/>, and optionally when a trigger misfires
    /// (<see cref="CronlightAlertOptions.NotifyOnMisfire"/>). Works without the dashboard being mapped. In a cluster
    /// with execution history registered, failure streaks are counted across every node.
    /// </summary>
    public static IServiceCollection AddCronlightAlerts(
        this IServiceCollection services,
        Action<CronlightAlertOptions>? configure = null
    )
    {
        services.AddOptions<CronlightAlertOptions>();
        if (configure != null)
            services.Configure(configure);

        // HttpClient's own logging writes each request's URL, and a webhook's URL is its password (Slack's carries its
        // secret in the path, which the logging's query-string redaction does not cover)
        services.AddHttpClient(AlertWorker.HttpClientName).RemoveAllLoggers();
        services.TryAddSingleton<AlertQueue>();
        services.TryAddSingleton<AlertListener>();
        services.AddSchedulerAttachment<AlertAttachment>();
        services.AddJobNotCreatedHandler<AlertListener>();
        // First in the collection: alerts are sent until Quartz has finished its last job
        if (!services.Any(d => d.ImplementationType == typeof(AlertWorker)))
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService, AlertWorker>());
        return services;
    }

    /// <summary>Maps the dashboard UI at {prefix}/ and its API at {prefix}/api.</summary>
    public static RouteGroupBuilder MapCronlight(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/cronlight"
    )
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<CronlightOptions>>().Value;
        var env = endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Cronlight");

        var group = endpoints.MapGroup(prefix);
        // Every response carries them, refusals included: see SecurityHeadersStartupFilter
        group.WithMetadata(new SecurityHeaders(options.ContentSecurityPolicy));

        if (options.ViewPolicy != null)
        {
            group.RequireAuthorization(options.ViewPolicy);
        }
        else if (!env.IsDevelopment() && !options.AllowAnonymousOutsideDevelopment)
        {
            logger.LogWarning(
                "Cronlight at {Prefix} has no ViewPolicy and is disabled outside Development",
                prefix
            );
            group.AddEndpointFilter((_, _) =>
                ValueTask.FromResult<object?>(Results.Problem(
                    "Cronlight requires a ViewPolicy outside Development.",
                    statusCode: StatusCodes.Status403Forbidden
                ))
            );
        }

        var api = group.MapGroup("/api");
        api.AddErrorHandling(logger);
        api.RequireSchedulerAccess(manage: false);
        api.MapOpenApiDocument();
        api.MapReadEndpoints();
        foreach (var extension in endpoints.ServiceProvider.GetServices<DashboardExtension>())
            extension.Map(api, options);

        var manage = api.MapGroup("");
        if (options.ManagePolicy != null)
            manage.RequireAuthorization(options.ManagePolicy);
        // First, so refused requests (forged, read-only mode) are audited too
        manage.AddAuditing(endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(Audit.LoggerCategory));
        manage.RequireDashboardRequests();
        manage.RequireSchedulerAccess(manage: true);
        if (options.ReadOnly)
        {
            manage.AddEndpointFilter((_, _) =>
                ValueTask.FromResult<object?>(Results.Problem(
                    "Cronlight is in read-only mode.",
                    statusCode: StatusCodes.Status403Forbidden
                ))
            );
        }
        manage.MapManageEndpoints();

        group.MapStaticUi();
        return group;
    }

    private static void MapStaticUi(this RouteGroupBuilder group)
    {
        var files = new ManifestEmbeddedFileProvider(typeof(CronlightExtensions).Assembly, "wwwroot");
        var contentTypes = new FileExtensionContentTypeProvider();

        // Handler takes HttpContext and returns IResult (not a raw RequestDelegate) so that
        // group endpoint filters, like the default-deny filter above, also apply to the UI.
        group.MapGet("/{**path}", (HttpContext ctx, string? path) =>
        {
            // Relative asset URLs need a trailing slash on the dashboard root
            if (string.IsNullOrEmpty(path) && !ctx.Request.Path.Value!.EndsWith('/'))
                return Results.Redirect($"{ctx.Request.PathBase}{ctx.Request.Path}/");

            if (path?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true)
                return Results.Problem(title: "Not found.", detail: $"There is no API endpoint {ctx.Request.Method} {path}.",
                    statusCode: StatusCodes.Status404NotFound);

            var file = string.IsNullOrEmpty(path) ? null : files.GetFileInfo(path);
            if (file is not { Exists: true })
            {
                // SPA fallback
                file = files.GetFileInfo("index.html");
                ctx.Response.Headers.CacheControl = "no-cache";
            }

            if (!contentTypes.TryGetContentType(file.Name, out var contentType))
                contentType = "application/octet-stream";

            return Results.Stream(file.CreateReadStream(), contentType);
        }).ExcludeFromDescription();
    }
}

/// <summary>The security headers of the dashboard's responses, as metadata on its endpoints.</summary>
internal sealed record SecurityHeaders(string? ContentSecurityPolicy)
{
    public void Apply(IHeaderDictionary headers)
    {
        headers.XContentTypeOptions = "nosniff";
        // Links out of the dashboard do not reveal where it is hosted
        headers["Referrer-Policy"] = "no-referrer";
        if (string.IsNullOrEmpty(ContentSecurityPolicy))
            return;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        // For browsers without frame-ancestors; only when it says the same, so it cannot contradict a changed policy
        if (ContentSecurityPolicy.Contains("frame-ancestors 'none'"))
            headers.XFrameOptions = "DENY";
    }
}

/// <summary>
/// Adds the security headers to every response for a dashboard endpoint, as it starts. A middleware rather than an
/// endpoint filter, since the authorization middleware's challenges and refusals, and requests whose body cannot be
/// read, are answered before the endpoint's filters run.
/// </summary>
internal sealed class SecurityHeadersStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Response.OnStarting(() =>
            {
                context.GetEndpoint()?.Metadata.GetMetadata<SecurityHeaders>()?.Apply(context.Response.Headers);
                return Task.CompletedTask;
            });
            return nextMiddleware(context);
        });
        next(app);
    };
}
