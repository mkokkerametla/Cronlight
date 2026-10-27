using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Quartz;

namespace Cronlight.Mcp;

/// <summary>What a tool that changes a job did.</summary>
internal record ActionResult(string JobGroup, string JobName, string Message);

/// <summary>
/// Tools that change jobs, registered only with <see cref="CronlightMcpOptions.AllowActions"/>. Each needs the dashboard's
/// manage permission for the scheduler, goes through the same rules as the API (another app's jobs are read-only), and
/// is audited as the API's action is, with Via set to mcp. None deletes or edits a job.
/// </summary>
[McpServerToolType]
internal sealed class CronlightMcpActions(McpAccess access, JobTypeCatalog catalog, IServiceProvider services, ILoggerFactory loggers)
{
    private const string Via = "mcp";
    private readonly ILogger audit = loggers.CreateLogger(Audit.LoggerCategory);

    [McpServerTool(Name = "pause_job", Title = "Pause a job", ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true)]
    [Description("Pauses a job's triggers, so it does not run on schedule until resumed. A run going on now carries on.")]
    public Task<ActionResult> PauseJob(
        [Description("The job's group, e.g. DEFAULT.")] string group,
        [Description("The job's name.")] string name,
        [Description("The scheduler; the default one when left out.")] string? scheduler = null,
        CancellationToken cancellationToken = default) =>
        Act("pause", group, name, scheduler, async (s, key) =>
        {
            var (_, problem) = await DashboardApi.GetManageableJob(s, key, access.Options);
            if (problem != null)
                return problem;
            await s.PauseJob(key, cancellationToken);
            return Results.NoContent();
        }, "Paused.", cancellationToken);

    [McpServerTool(Name = "resume_job", Title = "Resume a job", ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true)]
    [Description("Resumes a paused job's triggers, and those Quartz stopped (Error status), so it runs on schedule again.")]
    public Task<ActionResult> ResumeJob(
        [Description("The job's group, e.g. DEFAULT.")] string group,
        [Description("The job's name.")] string name,
        [Description("The scheduler; the default one when left out.")] string? scheduler = null,
        CancellationToken cancellationToken = default) =>
        Act("resume", group, name, scheduler, async (s, key) =>
        {
            var (_, problem) = await DashboardApi.GetManageableJob(s, key, access.Options);
            if (problem != null)
                return problem;
            await DashboardApi.ResumeJob(s, key);
            return Results.NoContent();
        }, "Resumed.", cancellationToken);

    [McpServerTool(Name = "run_job", Title = "Run a job now", ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = false,
        UseStructuredContent = true)]
    [Description("Runs a job once now, with its saved data, as Run now in the dashboard does. Its outcome shows in query_runs once it finishes.")]
    public Task<ActionResult> RunJob(
        [Description("The job's group, e.g. DEFAULT.")] string group,
        [Description("The job's name.")] string name,
        [Description("The scheduler; the default one when left out.")] string? scheduler = null,
        CancellationToken cancellationToken = default) =>
        Act("trigger", group, name, scheduler, (s, key) => DashboardApi.RunJob(s, key, null, access.Http, catalog, access.Options),
            "Started.", cancellationToken);

    [McpServerTool(Name = "run_again", Title = "Run a failed run again", ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = false,
        UseStructuredContent = true)]
    [Description("Runs a failed, blocked (Vetoed) or cancelled run again, with the data that run had, as Run again in the " +
                 "dashboard's History does. Takes the run's id from query_runs.")]
    public async Task<ActionResult> RunAgain(
        [Description("The id of the run to repeat.")] long runId,
        [Description("The scheduler; the default one when left out.")] string? scheduler = null,
        CancellationToken cancellationToken = default)
    {
        var s = await access.View(scheduler, cancellationToken);
        var store = services.GetService<IJobHistoryStore>()
            ?? throw new McpException("Execution history is not enabled in this app, so there are no recorded runs.");
        var run = (await store.Query(s.SchedulerName, new JobHistoryQuery { BeforeId = runId + 1, Limit = 1 }, cancellationToken))
            .FirstOrDefault(r => r.Id == runId)
            ?? throw new McpException($"There is no run {runId}.");
        if (run.Status is not (JobExecutionStatus.Failed or JobExecutionStatus.Vetoed or JobExecutionStatus.Cancelled))
            throw new McpException($"Run {runId} {run.Status.ToString().ToLowerInvariant()}: only failed, blocked or cancelled runs " +
                                   "are run again. run_job runs the job with its saved data.");

        var request = new RunJobRequest(run.Data?.ToDictionary(kv => kv.Key, kv => kv.Value), run.FireInstanceId);
        return await Act("trigger", run.JobGroup, run.JobName, s.SchedulerName,
            (sc, key) => DashboardApi.RunJob(sc, key, request, access.Http, catalog, access.Options), "Started again.", cancellationToken);
    }

    /// <summary>Carries out an action if the user may manage the scheduler, and audits it whatever the outcome.</summary>
    private async Task<ActionResult> Act(string action, string group, string name, string? scheduler,
        Func<IScheduler, JobKey, Task<IResult>> carryOut, string done, CancellationToken cancellationToken)
    {
        var s = await access.View(scheduler, cancellationToken);
        var http = access.Http;
        // What this call changes, and nothing an earlier call on the same request recorded
        Audit.RecordChanges(http, new Dictionary<string, CronlightAuditChange>());
        IResult? result = null;
        Exception? failure = null;
        try
        {
            result = await access.CanManage(s)
                ? await carryOut(s, new JobKey(name, group))
                : Results.Problem(title: "Access denied.", detail: $"You cannot change jobs in scheduler '{s.SchedulerName}'.",
                    statusCode: StatusCodes.Status403Forbidden);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            await Audit.Record(http, action, group, name, s.SchedulerName, result, failure, audit, Via);
        }

        if (result is IStatusCodeHttpResult { StatusCode: >= 400 })
            throw new McpException((result as ProblemHttpResult)?.ProblemDetails is { } problem
                ? problem.Detail ?? problem.Title ?? "Refused."
                : "Refused.");
        return new ActionResult(group, name, done);
    }
}
