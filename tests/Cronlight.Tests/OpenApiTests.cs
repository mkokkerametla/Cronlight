using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cronlight.Tests;

/// <summary>The OpenAPI description of the API, at api/openapi.json.</summary>
public class OpenApiTests
{
    [Fact]
    public async Task Every_endpoint_is_described_with_a_summary_and_what_it_answers()
    {
        await using var host = await TestHost.Start();
        var document = await host.Client().GetFromJsonAsync<JsonElement>("api/openapi.json");
        Assert.StartsWith("3.1", document.GetProperty("openapi").GetString());
        Assert.Equal("Cronlight", document.GetProperty("info").GetProperty("title").GetString());

        var mapped = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/cronlight/api/") && e.RoutePattern.RawText != "/cronlight/api/openapi.json")
            // The document leaves out route constraints: {id:long} is {id}
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(m => $"{m} {Regex.Replace(e.RoutePattern.RawText!, @"\{(\w+):[^}]+\}", "{$1}")}"))
            .Order()
            .ToList();
        var paths = document.GetProperty("paths");
        var described = paths.EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => $"{op.Name.ToUpperInvariant()} {path.Name}"))
            .Order()
            .ToList();
        Assert.Equal(mapped, described);
        Assert.True(mapped.Count >= 25, $"Only {mapped.Count} endpoints were found");

        foreach (var path in paths.EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject())
        {
            var op = operation.Value;
            var name = $"{operation.Name} {path.Name}";
            Assert.False(string.IsNullOrEmpty(op.GetProperty("summary").GetString()), $"{name} has no summary");
            Assert.Single(op.GetProperty("tags").EnumerateArray());
            var responses = op.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToList();
            Assert.Contains(responses, r => r.StartsWith('2'));
            // Every request may name a scheduler; changes need the dashboard's header
            var parameters = op.TryGetProperty("parameters", out var p) ? p.EnumerateArray().ToList() : [];
            Assert.Contains(parameters, x => x.GetProperty("name").GetString() == "scheduler" && x.GetProperty("in").GetString() == "query");
            Assert.Equal(operation.Name != "get", parameters.Any(x => x.GetProperty("name").GetString() == "X-Cronlight-Request"
                && x.GetProperty("in").GetString() == "header" && x.GetProperty("required").GetBoolean()));
        }

        // Answers have their types, without the code's Dto suffix
        var search = paths.GetProperty("/cronlight/api/jobs/search").GetProperty("get").GetProperty("responses").GetProperty("200");
        Assert.Equal("#/components/schemas/JobPage",
            search.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var import = paths.GetProperty("/cronlight/api/jobs/import").GetProperty("post");
        Assert.Equal(["application/json", "application/yaml"],
            import.GetProperty("requestBody").GetProperty("content").EnumerateObject().Select(c => c.Name));
    }

    [Fact]
    public async Task The_hosts_own_OpenAPI_document_leaves_the_dashboards_endpoints_out()
    {
        await using var host = await TestHost.Start(services: s => s.AddOpenApi());

        var document = await host.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync();

        Assert.DoesNotContain(document.Paths?.Keys.ToList() ?? [], path => path.StartsWith("/cronlight"));
    }

    [Fact]
    public async Task The_description_is_for_those_who_may_see_the_dashboard()
    {
        await using var host = await TestHost.Start("Production", o =>
        {
            o.ViewPolicy = "Viewers";
            o.ManagePolicy = "Admins";
        });

        Assert.Equal(HttpStatusCode.OK, (await host.Client("viewer").GetAsync("api/openapi.json")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await host.Client().GetAsync("api/openapi.json")).StatusCode);
    }
}
