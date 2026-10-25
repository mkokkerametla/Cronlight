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
/// <param name="Action">create, update, pause, resume, trigger or delete.</param>
/// <param name="User">The user's name, or "anonymous" when the dashboard allows anonymous access.</param>
/// <param name="Error">Why the request was refused or failed; null when it succeeded.</param>
/// <param name="Changes">What a create or update set, by field: description, triggers, or data.&lt;key&gt;.</param>
public sealed record CronlightAuditEvent(
    DateTimeOffset TimeUtc,
    string User,
    string Action,
    string JobGroup,
    string JobName,
    bool Succeeded,
    int StatusCode,
    string? Error,
    IReadOnlyDictionary<string, CronlightAuditChange> Changes
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
        var status = failure != null ? StatusCodes.Status500InternalServerError
            : (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var succeeded = status < 400;
        var (group, name) = Target(context);
        var changes = http.Items[ChangesKey] as IReadOnlyDictionary<string, CronlightAuditChange> ?? NoChanges;

        var auditEvent = new CronlightAuditEvent(
            DateTimeOffset.UtcNow,
            UserName(http.User),
            ActionName(http.Request),
            group,
            name,
            succeeded,
            status,
            succeeded ? null : failure?.Message ?? ErrorMessage(result) ?? ReasonPhrases.GetReasonPhrase(status),
            changes);

        logger.Log(succeeded ? LogLevel.Information : LogLevel.Warning,
            "Audit: {User} {AuditAction} {JobGroup}.{JobName}: {AuditOutcome} ({StatusCode}){AuditError} {AuditChanges}",
            auditEvent.User, auditEvent.Action, group, name, succeeded ? "succeeded" : "refused", status,
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
        if (route["group"] is string group && route["name"] is string name)
            return (group, name);
        // Creating: the job is named in the body
        var create = context.Arguments.OfType<CreateJobRequest>().FirstOrDefault();
        return (string.IsNullOrWhiteSpace(create?.Group) ? "DEFAULT" : create.Group, create?.Name ?? "");
    }

    private static string ActionName(HttpRequest request)
    {
        if (request.Method == HttpMethods.Put)
            return "update";
        if (request.Method == HttpMethods.Delete)
            return "delete";
        // POST jobs creates; POST jobs/{group}/{name}/{action} pauses, resumes or triggers
        var last = (request.Path.Value ?? "").TrimEnd('/').Split('/')[^1];
        return last == "jobs" ? "create" : last;
    }

    private static string UserName(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.Identity.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unnamed user"
            : "anonymous";

    private static string? ErrorMessage(object? result) => result switch
    {
        ProblemHttpResult problem => string.Join(" ", new[] { problem.ProblemDetails.Title, problem.ProblemDetails.Detail }.Where(s => !string.IsNullOrEmpty(s))),
        IValueHttpResult { Value: string message } => message,
        _ => null,
    };
}
