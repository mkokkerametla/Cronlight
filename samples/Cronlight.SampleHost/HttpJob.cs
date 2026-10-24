using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight.SampleHost;

public class HttpJobOptions
{
    /// <summary>
    /// Hosts the job may call. Empty allows any host, which is fine for a demo but lets anyone who can
    /// create jobs make this server call arbitrary URLs (including internal ones). Restrict it in real apps.
    /// </summary>
    public List<string> AllowedHosts { get; } = new();
}

/// <summary>
/// Calls an HTTP API on a schedule. Everything about the request is job data, so people can schedule
/// any API call from the Cronlight UI. A response with an unexpected status fails the run, which then
/// shows up in Cronlight's execution history.
/// </summary>
[CronlightJob("HTTP request", Description = "Calls an HTTP API on a schedule. Fails when the response status is not expected.")]
[JobParameter("url", Type = JobParameterType.Url, Required = true, Description = "Absolute http(s) URL")]
[JobParameter("method", DefaultValue = "GET", Choices = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" })]
[JobParameter("headers", Type = JobParameterType.Text, Description = "One header per line, e.g. X-Api-Version: 2")]
[JobParameter("authorization", Type = JobParameterType.Secret, Description = "Authorization header value, e.g. Bearer <token>. Masked in the UI.")]
[JobParameter("body", Type = JobParameterType.Text, Description = "Request body for POST, PUT and PATCH")]
[JobParameter("contentType", DefaultValue = "application/json")]
[JobParameter("timeoutSeconds", Type = JobParameterType.Int, DefaultValue = "30")]
[JobParameter("expectedStatusCodes", DefaultValue = "2xx", Description = "Comma-separated codes or ranges like 2xx, e.g. 200,202")]
public class HttpJob(IHttpClientFactory httpClientFactory, IOptions<HttpJobOptions> options, ILogger<HttpJob> logger) : IJob
{
    public const string ClientName = "Cronlight.HttpJob";

    public async Task Execute(IJobExecutionContext context)
    {
        var map = context.MergedJobDataMap;
        // JobDataMap.GetString throws for missing keys, and optional parameters may be absent
        string? Get(string key) => map.TryGetValue(key, out var value) ? value?.ToString() : null;

        var url = new Uri(Get("url") ?? throw new JobExecutionException("url is required"));
        var method = new HttpMethod((Get("method") ?? "GET").ToUpperInvariant());

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            throw new JobExecutionException($"Only http and https URLs are allowed, not {url.Scheme}");
        var allowed = options.Value.AllowedHosts;
        if (allowed.Count > 0 && !allowed.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            throw new JobExecutionException($"Host {url.Host} is not in HttpJob:AllowedHosts");

        using var request = new HttpRequestMessage(method, url);
        if (Get("body") is { Length: > 0 } body && method != HttpMethod.Get && method != HttpMethod.Delete)
            request.Content = new StringContent(body, null, Get("contentType") ?? "application/json");

        foreach (var line in (Get("headers") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new JobExecutionException($"Header line '{line}' is not in 'Name: value' form");
            var (name, value) = (line[..separator].Trim(), line[(separator + 1)..].Trim());
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        if (Get("authorization") is { Length: > 0 } authorization)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);

        var timeout = TimeSpan.FromSeconds(int.TryParse(Get("timeoutSeconds"), out var seconds) && seconds > 0 ? seconds : 30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        cancellation.CancelAfter(timeout);

        var client = httpClientFactory.CreateClient(ClientName);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{method} {url} timed out after {timeout.TotalSeconds:0}s");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            logger.LogInformation("{Method} {Url} returned {Status} in {Elapsed} ms", method, url, status, stopwatch.ElapsedMilliseconds);
            context.Result = status;

            if (!IsExpected(status, Get("expectedStatusCodes") ?? "2xx"))
                throw new HttpRequestException($"{method} {url} returned {status} {response.ReasonPhrase}", null, response.StatusCode);
        }
    }

    private static bool IsExpected(int status, string expected) =>
        expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(code =>
            code.Length == 3 && code.EndsWith("xx", StringComparison.OrdinalIgnoreCase)
                ? status / 100 == code[0] - '0'
                : int.TryParse(code, out var exact) && exact == status);
}
