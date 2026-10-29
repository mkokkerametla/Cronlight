using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cronlight;

/// <summary>
/// A change made through the dashboard or its API, or an attempt that was refused. Secret values are
/// never included: a changed secret shows as <c>********</c> on both sides. Control characters are escaped (a line
/// break as <c>\n</c>), as in the log, except in the changes, which a description's line breaks are part of.
/// </summary>
/// <param name="Action">
/// create, update, pause, resume, trigger, cancel, cancel-retry or delete for one job; pause-group or resume-group for every
/// job in <paramref name="JobGroup"/>, and pause-all or resume-all for every job in the scheduler, with an empty group;
/// import, or import-dry-run when it only checked the file, for a file of jobs. For these the job name is empty.
/// </param>
/// <param name="User">The user's name, or "anonymous" when the dashboard allows anonymous access.</param>
/// <param name="Error">Why the request was refused or failed; null when it succeeded.</param>
/// <param name="Changes">
/// What a create or update set, by field: description, triggers, retry, or data.&lt;key&gt;. For a run (trigger),
/// the data changed for that run only, and retryOf when it repeats a failed run. For a group or the whole scheduler,
/// jobs: how many jobs it held. For an import, &lt;group&gt;.&lt;name&gt; (created or updated) for each job it changed, and
/// &lt;group&gt;.&lt;name&gt;: &lt;field&gt; for what it changed; for a dry run the same, as to create or to update, for what
/// importing would change.
/// </param>
/// <param name="Scheduler">The scheduler the job is in.</param>
/// <param name="Via">How the change was asked for: null for the dashboard or its API, mcp for an MCP client's tool call.</param>
public sealed record CronlightAuditEvent(
    DateTimeOffset TimeUtc,
    string User,
    string Action,
    string JobGroup,
    string JobName,
    bool Succeeded,
    int StatusCode,
    string? Error,
    IReadOnlyDictionary<string, CronlightAuditChange> Changes,
    string? Scheduler = null,
    string? Via = null
);

/// <summary>A field's value before and after a change; null where it was not set.</summary>
public sealed record CronlightAuditChange(string? Before, string? After);

/// <summary>
/// Receives every audit event, in addition to the log (category <c>Cronlight.Audit</c>). Register one or
/// more with <c>services.AddScoped&lt;ICronlightAuditSink, MySink&gt;()</c> (or as a singleton) to keep events
/// in your own store. Sinks are resolved per request. A sink that throws is logged and does not fail the
/// request, which has already been carried out.
/// </summary>
public interface ICronlightAuditSink
{
    Task WriteAsync(CronlightAuditEvent auditEvent, CancellationToken cancellationToken);
}

internal static class Audit
{
    public const string LoggerCategory = "Cronlight.Audit";
    private const string ChangesKey = "Cronlight.Audit.Changes";
    private static readonly IReadOnlyDictionary<string, CronlightAuditChange> NoChanges = new Dictionary<string, CronlightAuditChange>();

    /// <summary>Lets an endpoint attach what it changed to the request's audit event.</summary>
    public static void RecordChanges(HttpContext http, IReadOnlyDictionary<string, CronlightAuditChange> changes) =>
        http.Items[ChangesKey] = changes;

    /// <summary>Writes an audit event for every request to these endpoints, whatever its outcome.</summary>
    public static void AddAuditing(this RouteGroupBuilder api, ILogger logger)
    {
        api.AddEndpointFilter(async (context, next) =>
        {
            object? result = null;
            Exception? failure = null;
            try
            {
                result = await next(context);
                return result;
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                await Write(context, result, failure, logger);
            }
        });
    }

    private static Task Write(EndpointFilterInvocationContext context, object? result, Exception? failure, ILogger logger)
    {
        var (group, name) = Target(context);
        var http = context.HttpContext;
        return Record(http, ActionName(http.Request), group, name, DashboardApi.RequestedScheduler(http), result, failure, logger);
    }

    /// <summary>
    /// Writes the audit event for a change, or an attempt at one, that did not come through the API's endpoints, such as
    /// an MCP tool call. What it changed is what the code it ran recorded with <see cref="RecordChanges"/>.
    /// </summary>
    /// <param name="scheduler">The scheduler's name; the host's own when empty.</param>
    /// <param name="result">The outcome, as the API would answer it; its status code says whether it succeeded.</param>
    internal static async Task Record(HttpContext http, string action, string group, string name, string? scheduler, object? result,
        Exception? failure, ILogger logger, string? via = null)
    {
        var status = failure is UnknownSchedulerException ? StatusCodes.Status404NotFound
            : failure != null ? StatusCodes.Status500InternalServerError
            : (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var succeeded = status < 400;
        var changes = http.Items[ChangesKey] as IReadOnlyDictionary<string, CronlightAuditChange> ?? NoChanges;
        if (string.IsNullOrEmpty(scheduler))
            scheduler = (await http.RequestServices.GetRequiredService<CronlightSchedulers>().Default()).SchedulerName;

        // Escaped for sinks too, which often write lines of text as well. Changes are not: a description may have line
        // breaks, and the log writes them as JSON.
        var auditEvent = new CronlightAuditEvent(
            DateTimeOffset.UtcNow,
            Escape(UserName(http.User)),
            Escape(action),
            Escape(group),
            Escape(name),
            succeeded,
            status,
            Escape(succeeded ? null : failure?.Message ?? ErrorMessage(result) ?? ReasonPhrases.GetReasonPhrase(status)),
            changes,
            Escape(scheduler),
            via);

        logger.Log(succeeded ? LogLevel.Information : LogLevel.Warning,
            "Audit: {User} {AuditAction} {JobGroup}.{JobName} in {Scheduler}{AuditVia}: {AuditOutcome} ({StatusCode}){AuditError} {AuditChanges}",
            auditEvent.User, auditEvent.Action, auditEvent.JobGroup, auditEvent.JobName, auditEvent.Scheduler, via is null ? "" : $" via {via}",
            succeeded ? "succeeded" : "refused", status, auditEvent.Error is { } error ? $" {error}" : "",
            changes.Count > 0 ? JsonSerializer.Serialize(changes) : "");

        foreach (var sink in http.RequestServices.GetServices<ICronlightAuditSink>())
        {
            try
            {
                // Not the request's token: the change has happened, so record it even if the client went away
                await sink.WriteAsync(auditEvent, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit sink {Sink} failed for {AuditAction} {JobGroup}.{JobName}",
                    sink.GetType().Name, auditEvent.Action, auditEvent.JobGroup, auditEvent.JobName);
            }
        }
    }

    /// <summary>
    /// The text with control characters escaped, a line break as \n, so what a request names (a scheduler or a job that
    /// does not exist, and the errors that quote them) cannot start a line of its own in a log of plain text.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    private static string? Escape(string? text) =>
        text == null || !text.Any(Unsafe) ? text
        : string.Concat(text.Select(c => c switch
        {
            '\n' => @"\n",
            '\r' => @"\r",
            '\t' => @"\t",
            _ when Unsafe(c) => $@"\u{(int)c:x4}",
            _ => c.ToString(),
        }));

    // Unicode's line and paragraph separators end lines in some log viewers too
    private static bool Unsafe(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static (string Group, string Name) Target(EndpointFilterInvocationContext context)
    {
        var route = context.HttpContext.Request.RouteValues;
        if (route["group"] is string group)
            return (DashboardApi.FromRoute(group), route["name"] is string name ? DashboardApi.FromRoute(name) : "");
        // Creating: the job is named in the body. Pausing or resuming the whole scheduler names no job
        if (context.Arguments.OfType<CreateJobRequest>().FirstOrDefault() is not { } create)
            return ("", "");
        return (string.IsNullOrWhiteSpace(create.Group) ? "DEFAULT" : create.Group, create.Name ?? "");
    }

    private static string ActionName(HttpRequest request)
    {
        if (request.Method == HttpMethods.Put)
            return "update";
        if (request.Method == HttpMethods.Delete)
            return "delete";
        // POST jobs creates; POST jobs/{group}/{name}/{action} pauses, resumes or triggers; POST groups/{group}/{action}
        // and scheduler/{action} pause or resume many jobs at once
        var segments = (request.Path.Value ?? "").TrimEnd('/').Split('/');
        var last = segments[^1];
        var route = request.RouteValues;
        if (last == "import")
            return bool.TryParse(request.Query["dryRun"], out var dryRun) && dryRun ? "import-dry-run" : "import";
        return last == "jobs" ? "create"
            : route.ContainsKey("group") && !route.ContainsKey("name") ? $"{last}-group"
            : !route.ContainsKey("group") && segments.Length >= 2 && segments[^2] == "scheduler" ? $"{last}-all"
            : last;
    }

    internal static string UserName(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.Identity.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unnamed user"
            : "anonymous";

    private static string? ErrorMessage(object? result) => result switch
    {
        // The detail says what is wrong; the title only names the kind of error
        ProblemHttpResult problem => string.IsNullOrEmpty(problem.ProblemDetails.Detail) ? problem.ProblemDetails.Title : problem.ProblemDetails.Detail,
        IValueHttpResult { Value: string message } => message,
        _ => null,
    };
}
