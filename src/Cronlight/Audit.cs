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
/// never included: a changed secret shows as <c>********</c> on both sides.
/// </summary>
/// <param name="Action">
/// create, update, pause, resume, trigger, cancel, cancel-retry or delete for one job; pause-group or resume-group for every
/// job in <paramref name="JobGroup"/>, and pause-all or resume-all for every job in the scheduler, with an empty group.
/// For these the job name is empty.
/// </param>
/// <param name="User">The user's name, or "anonymous" when the dashboard allows anonymous access.</param>
/// <param name="Error">Why the request was refused or failed; null when it succeeded.</param>
/// <param name="Changes">
/// What a create or update set, by field: description, triggers, retry, or data.&lt;key&gt;. For a run (trigger),
/// the data changed for that run only, and retryOf when it repeats a failed run. For a group or the whole scheduler,
/// jobs: how many jobs it held.
/// </param>
/// <param name="Scheduler">The scheduler the job is in.</param>
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
    string? Scheduler = null
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

    private static async Task Write(EndpointFilterInvocationContext context, object? result, Exception? failure, ILogger logger)
    {
        var http = context.HttpContext;
        var status = failure is UnknownSchedulerException ? StatusCodes.Status404NotFound
            : failure != null ? StatusCodes.Status500InternalServerError
            : (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var succeeded = status < 400;
        var (group, name) = Target(context);
        var changes = http.Items[ChangesKey] as IReadOnlyDictionary<string, CronlightAuditChange> ?? NoChanges;
        var scheduler = DashboardApi.RequestedScheduler(http) is { Length: > 0 } requested
            ? requested
            : (await http.RequestServices.GetRequiredService<CronlightSchedulers>().Default()).SchedulerName;

        var auditEvent = new CronlightAuditEvent(
            DateTimeOffset.UtcNow,
            UserName(http.User),
            ActionName(http.Request),
            group,
            name,
            succeeded,
            status,
            succeeded ? null : failure?.Message ?? ErrorMessage(result) ?? ReasonPhrases.GetReasonPhrase(status),
            changes,
            scheduler);

        logger.Log(succeeded ? LogLevel.Information : LogLevel.Warning,
            "Audit: {User} {AuditAction} {JobGroup}.{JobName} in {Scheduler}: {AuditOutcome} ({StatusCode}){AuditError} {AuditChanges}",
            auditEvent.User, auditEvent.Action, group, name, scheduler, succeeded ? "succeeded" : "refused", status,
            auditEvent.Error is { } error ? $" {error}" : "", changes.Count > 0 ? JsonSerializer.Serialize(changes) : "");

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
                    sink.GetType().Name, auditEvent.Action, group, name);
            }
        }
    }

    private static (string Group, string Name) Target(EndpointFilterInvocationContext context)
    {
        var route = context.HttpContext.Request.RouteValues;
        if (route["group"] is string group)
            return (group, route["name"] as string ?? "");
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
