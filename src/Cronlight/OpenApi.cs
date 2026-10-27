using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cronlight;

/// <summary>
/// Describes the dashboard's API as an OpenAPI document of its own, served at {prefix}/api/openapi.json. Its endpoints
/// are in their own document group, so a host's own OpenAPI documents (Microsoft.AspNetCore.OpenApi's or Swashbuckle's,
/// which take endpoints with no group or their own) leave them out.
/// </summary>
internal static class CronlightOpenApi
{
    public const string DocumentName = "cronlight";

    private sealed class Registered;

    public static IServiceCollection AddCronlightOpenApi(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(Registered)))
            return services;
        services.AddSingleton<Registered>();
        services.AddOpenApi(DocumentName, o =>
        {
            o.ShouldInclude = description => description.GroupName == DocumentName;
            // JobDto is Job in the document
            o.CreateSchemaReferenceId = type => OpenApiOptions.CreateDefaultSchemaReferenceId(type) is { Length: > 3 } id && id.EndsWith("Dto", StringComparison.Ordinal)
                ? id[..^3]
                : OpenApiOptions.CreateDefaultSchemaReferenceId(type);
            o.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Cronlight",
                    Version = Version,
                    Description =
                        "The API of the Cronlight dashboard for Quartz.NET. Requests are authorized as the dashboard is, by the host's " +
                        "ViewPolicy and ManagePolicy. Requests that change jobs must send an X-Cronlight-Request header (any value), " +
                        "which keeps other sites from using a viewer's cookies. Every request may name a scheduler with ?scheduler=, " +
                        "and goes to the host's own scheduler without it. Errors are problem details, whose detail says what is wrong.",
                };
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((operation, context, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = DashboardApi.SchedulerParameter,
                    In = ParameterLocation.Query,
                    Description = "The scheduler, by name; the host's own when left out.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
                if (!HttpMethods.IsGet(context.Description.HttpMethod ?? ""))
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = DashboardApi.RequestHeader,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Any value. Without it, a request that changes jobs is refused with 403.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    });
                return Task.CompletedTask;
            });
        });
        return services;
    }

    /// <summary>Puts the API's endpoints in the document, and serves it at api/openapi.json.</summary>
    public static void MapOpenApiDocument(this RouteGroupBuilder api)
    {
        api.WithGroupName(DocumentName);
        api.MapGet("/openapi.json", async (HttpContext http, CancellationToken ct) =>
        {
            var document = await http.RequestServices.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName).GetOpenApiDocumentAsync(ct);
            return Results.Text(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, ct), "application/json; charset=utf-8");
        }).ExcludeFromDescription();
    }

    /// <summary>The package's version, without the commit it was built from.</summary>
    private static string Version =>
        typeof(CronlightOpenApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
