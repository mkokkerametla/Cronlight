using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.Matchers;
using Quartz.Impl.Triggers;
using Quartz.Spi;

namespace Cronlight;

/// <param name="Kind">
/// Cron, Once (runs one time), Interval (runs every so often), Retry (a waiting retry), or the trigger's class name for
/// other kinds, which the dashboard shows but does not edit.
/// </param>
/// <param name="MisfireInstruction">What Quartz does when the trigger misses a fire time, e.g. FireOnceNow or DoNothing.</param>
/// <param name="Attempt">For a waiting retry, the attempt it will be: 2 for the first retry.</param>
/// <param name="StartTimeUtc">For Once and Interval triggers: when it runs, or first runs.</param>
/// <param name="IntervalSeconds">For Interval triggers: the time between runs.</param>
/// <param name="RepeatCount">For Interval triggers: how many runs follow the first; null when it repeats forever.</param>
/// <param name="SkipsMissed">A fire time the scheduler misses is skipped, rather than run as soon as it can.</param>
/// <param name="CalendarName">The Quartz calendar whose excluded times the trigger skips, e.g. holidays.</param>
internal record TriggerDto(
    string Name,
    string Group,
    string Kind,
    string? CronExpression,
    string? TimeZone,
    string State,
    DateTimeOffset? NextFireTimeUtc,
    DateTimeOffset? PreviousFireTimeUtc,
    string MisfireInstruction,
    int? Attempt = null,
    DateTimeOffset? StartTimeUtc = null,
    long? IntervalSeconds = null,
    int? RepeatCount = null,
    bool SkipsMissed = false,
    string? CalendarName = null
);

/// <summary>A trigger's next fire times, from its next one on, skipping the times its calendar excludes.</summary>
internal record TriggerFireTimesDto(string Group, string Name, IReadOnlyList<DateTimeOffset> FireTimesUtc);

internal record JobDto(
    string Name,
    string Group,
    string? Description,
    string? JobType,
    string? DisplayName,
    bool Durable,
    bool Loadable,
    string? LoadError,
    IDictionary<string, string?> Data,
    IReadOnlyList<TriggerDto> Triggers,
    // The policy the job runs with, its own or its type's; null when failed runs are not retried
    JobRetryPolicy? Retry = null,
    // The [CronlightJob] description of the job's type, as opposed to Description, which is this job's own
    string? TypeDescription = null
);

/// <summary>One page of the job list, with counts over every job for the dashboard's stat cards.</summary>
/// <param name="Total">Jobs matching the search and filters, on every page.</param>
/// <param name="Types">Every job type label in the scheduler, for the type filter.</param>
internal record JobPageDto(
    IReadOnlyList<JobDto> Jobs,
    int Total,
    int Page,
    int PageSize,
    JobCountsDto Counts,
    IReadOnlyList<string> Types
);

/// <summary>Counts over every job, whatever the search and filters.</summary>
/// <param name="Error">Jobs Quartz stopped because it could not create them to run (a trigger in its Error state).</param>
internal record JobCountsDto(int All, int Active, int Paused, int Unloadable, int Error = 0);

/// <param name="CronExpression">For Cron triggers.</param>
/// <param name="TimeZone">For Cron triggers: the zone the expression is read in; UTC when left out.</param>
/// <param name="SkipMissed">
/// When the scheduler misses a fire time (it was down, or had no free thread), skip it and wait for the next one,
/// instead of running once as soon as it can (Quartz's default). A Once trigger that skips its fire time never runs.
/// </param>
/// <param name="Kind">Cron (the default), Once or Interval.</param>
/// <param name="StartAtUtc">
/// For Once triggers, when it runs, which must be in the future. For Interval triggers, its first run: now when left
/// out, and a time already past moves on to the next run after now, keeping the same rhythm.
/// </param>
/// <param name="IntervalSeconds">For Interval triggers: the time between runs.</param>
/// <param name="RepeatCount">For Interval triggers: how many runs follow the first; null (the default) repeats forever.</param>
internal record TriggerRequest(string? Name, string? CronExpression, string? TimeZone, bool SkipMissed = false,
    string? Kind = null, DateTimeOffset? StartAtUtc = null, long? IntervalSeconds = null, int? RepeatCount = null);

/// <param name="Type">The job's class, as listed by the job types endpoint. Required, unless JobType is sent.</param>
/// <param name="JobType">The same as Type, under the name jobs are read back with.</param>
internal record CreateJobRequest(
    string? Type,
    string? Name,
    string? Group,
    string? Description,
    Dictionary<string, string>? Data,
    List<TriggerRequest>? Triggers,
    // Null follows the job type's [CronlightRetry], if any
    JobRetryPolicy? Retry = null,
    string? JobType = null
);

/// <summary>
/// Null properties are left unchanged. Triggers replaces the job's triggers of the kinds in TriggerKinds; waiting retries,
/// pending manual runs and other kinds of trigger are always kept. Retry with MaxRetries 0 turns retries off, even when
/// the job type has a [CronlightRetry] policy.
/// </summary>
/// <param name="Data">
/// The job data to change: each key it holds is set to its value, or removed when the value is null, and keys it does not
/// hold are kept. A masked secret (********) keeps the saved value.
/// </param>
/// <param name="TriggerKinds">
/// The kinds of trigger Triggers replaces: Cron, Once and/or Interval. Left out, it replaces cron triggers and the kinds
/// Triggers holds, so a client that only sends cron triggers keeps the job's others. The dashboard sends all three.
/// </param>
internal record UpdateJobRequest(string? Description, Dictionary<string, string?>? Data, List<TriggerRequest>? Triggers,
    JobRetryPolicy? Retry = null, IReadOnlyList<string>? TriggerKinds = null);

/// <summary>Runs a job now. Data overrides the saved job data for this run (and its retries) only.</summary>
/// <param name="RetryOf">The fire instance id of the failed run this one retries, for the audit log.</param>
internal record RunJobRequest(Dictionary<string, string>? Data, string? RetryOf = null);

/// <summary>A create or update, checked and ready to carry out.</summary>
/// <param name="Changes">What it changes, by field, as the audit log records it.</param>
/// <param name="Triggers">The job's triggers of the kinds it sets, once carried out.</param>
internal sealed record JobChange(JobKey Key, Dictionary<string, CronlightAuditChange> Changes, IReadOnlyList<TriggerKey> Triggers,
    Func<IScheduler, Task> Apply);

/// <summary>The scheduler the dashboard shows, and what the user may do in it.</summary>
/// <param name="ClusterNodes">The cluster's nodes, for a dashboard whose own process may not run the scheduler at all; null when not read from the job store.</param>
/// <param name="CanManage">Whether the user may change jobs: lets the dashboard hide what they cannot use. The server checks it anyway.</param>
/// <param name="SecretReferences">Whether secret parameters may name a secret from configuration (secret:Name).</param>
/// <param name="RefreshSeconds">How often the dashboard refreshes.</param>
/// <param name="Schedulers">Every scheduler served that the user may see, the host's own first.</param>
internal record SchedulerDto(
    string SchedulerName,
    string SchedulerInstanceId,
    string Version,
    bool Started,
    bool InStandbyMode,
    string JobStore,
    bool JobStoreClustered,
    bool JobStoreSupportsPersistence,
    int ThreadPoolSize,
    int NumberOfJobsExecuted,
    DateTimeOffset? RunningSince,
    IReadOnlyList<ClusterNodeDto>? ClusterNodes,
    bool CanManage,
    bool HistoryEnabled,
    bool SecretReferences,
    int RefreshSeconds,
    IReadOnlyList<string> Schedulers
);

/// <summary>One page of history, newest first.</summary>
/// <param name="NextBefore">The before= value of the next page; null on the last page.</param>
internal record HistoryPageDto(IReadOnlyList<JobExecutionRecord> Records, long? NextBefore);

internal record JobKeyDto(string Name, string Group);

internal record MessageDto(string Message);

internal record CronPreviewDto(bool IsValid, string Expression, string? Description, IReadOnlyList<DateTimeOffset> NextFireTimes, string? ErrorMessage);

internal static class DashboardApi
{
    public const string Mask = "********";

    /// <summary>How many fire times api/jobs/{group}/{name}/fire-times lists for each trigger.</summary>
    private const int UpcomingFireTimes = 5;

    /// <summary>
    /// Header the dashboard sends with every request. Requests that change jobs must carry it: another
    /// site cannot add a custom header to a cross-site request without the host allowing it through
    /// CORS, so a page elsewhere cannot use the viewer's cookies to pause, run or delete jobs.
    /// </summary>
    public const string RequestHeader = "X-Cronlight-Request";

    /// <summary>Query parameter that picks a scheduler. Without it, requests go to the host's own scheduler.</summary>
    public const string SchedulerParameter = "scheduler";

    public static string RequestedScheduler(HttpContext http) => http.Request.Query[SchedulerParameter].ToString();

    /// <summary>The scheduler the request asks for, else the host's own. An unknown name ends the request with 404.</summary>
    internal static async Task<IScheduler> Scheduler(HttpContext http, CancellationToken cancellationToken = default) =>
        await http.RequestServices.GetRequiredService<CronlightSchedulers>().Find(RequestedScheduler(http), cancellationToken)
        ?? throw new UnknownSchedulerException(RequestedScheduler(http));

    /// <summary>
    /// Refuses requests for a scheduler the user may not see (<see cref="CronlightOptions.Schedulers"/>), or for
    /// changes to one they may not manage. Reads are checked here; changes on the manage group, after auditing, so
    /// refusals are audited.
    /// </summary>
    public static void RequireSchedulerAccess(this RouteGroupBuilder api, bool manage)
    {
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var options = http.RequestServices.GetRequiredService<IOptions<CronlightOptions>>().Value;
            if (options.Schedulers.Count == 0 || !manage && !HttpMethods.IsGet(http.Request.Method))
                return await next(context);
            var name = (await Scheduler(http)).SchedulerName;
            if (!await CanView(http, options, name))
                return Results.Problem(title: "Access denied.", detail: $"You do not have access to scheduler '{name}'.",
                    statusCode: StatusCodes.Status403Forbidden);
            if (manage && !await CanManage(http, options, name))
                return Results.Problem(title: "Access denied.", detail: $"You cannot change jobs in scheduler '{name}'.",
                    statusCode: StatusCodes.Status403Forbidden);
            return await next(context);
        });
    }

    /// <summary>Refuses requests that change jobs unless they come from the dashboard or another same-site client.</summary>
    public static void RequireDashboardRequests(this RouteGroupBuilder api)
    {
        api.AddEndpointFilter((context, next) =>
        {
            var request = context.HttpContext.Request;
            // Browsers set Sec-Fetch-Site themselves and scripts cannot change it; other clients do not send it
            var crossSite = request.Headers["Sec-Fetch-Site"] == "cross-site";
            if (crossSite || !request.Headers.ContainsKey(RequestHeader))
                return ValueTask.FromResult<object?>(Results.Problem(
                    title: "Request refused.",
                    detail: crossSite
                        ? "Jobs cannot be changed from another site."
                        : $"Requests that change jobs must send the {RequestHeader} header, as the dashboard does.",
                    statusCode: StatusCodes.Status403Forbidden));
            return next(context);
        });
    }

    /// <summary>Turns unhandled exceptions into ProblemDetails so no stack trace ever reaches the client.</summary>
    public static void AddErrorHandling(this RouteGroupBuilder api, ILogger logger)
    {
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (UnknownSchedulerException ex)
            {
                return Results.Problem(title: "Scheduler not found.", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                logger.LogError(ex, "Cronlight request {Method} {Path} failed",
                    context.HttpContext.Request.Method, context.HttpContext.Request.Path);

                // Scheduler messages are useful to operators ("Job's trigger is not in a state to ...") and contain no
                // internals, but a job store's quote the database (its tables, its values), so those stay in the log;
                // everything comes with a trace id to find it there.
                return Results.Problem(
                    title: "The scheduler operation failed.",
                    detail: ex switch
                    {
                        JobPersistenceException => "The job store failed; the app's log says why, under this trace id.",
                        SchedulerException => ex.Message,
                        _ => null,
                    },
                    statusCode: StatusCodes.Status500InternalServerError,
                    extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier }
                );
            }
        });
    }

    public static void MapReadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/scheduler", async (HttpContext http, CronlightSchedulers schedulers, IOptions<CronlightOptions> options,
            ClusterNodesReader cluster, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var scheduler = await Scheduler(http);
            var meta = await scheduler.GetMetaData();
            var all = await schedulers.All();
            WarnAboutSettingsForNoScheduler(options.Value, all, loggers);
            var visible = new List<string>();
            foreach (var s in all)
                if (await CanView(http, options.Value, s.SchedulerName))
                    visible.Add(s.SchedulerName);
            return Results.Ok(new SchedulerDto(
                meta.SchedulerName,
                meta.SchedulerInstanceId,
                meta.Version,
                meta.Started,
                meta.InStandbyMode,
                meta.JobStoreType.Name,
                meta.JobStoreClustered,
                meta.JobStoreSupportsPersistence,
                meta.ThreadPoolSize,
                meta.NumberOfJobsExecuted,
                meta.RunningSince,
                await cluster.Read(scheduler, ct),
                await CanManage(http, options.Value, scheduler.SchedulerName),
                http.RequestServices.GetService<IJobHistoryStore>() != null,
                SecretReferences.Enabled(options.Value),
                Math.Clamp(options.Value.RefreshSeconds, 1, 300),
                // For the dashboard's scheduler picker
                visible));
        })
            .WithTags(Tags.Scheduler).WithSummary("The scheduler, and what the user may do in it")
            .Produces<SchedulerDto>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/job-types", async (HttpContext http, JobTypeCatalog catalog) =>
            Results.Ok(catalog.For((await Scheduler(http)).SchedulerName).Where(t => !t.Hidden).OrderBy(t => t.DisplayName)))
            .WithTags(Tags.Jobs).WithSummary("The job types new jobs can be created with, and their parameters")
            .Produces<IEnumerable<JobTypeInfo>>();

        api.MapGet("/jobs", async (HttpContext http, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await Scheduler(http);
            var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
            var jobs = new List<JobDto>();
            foreach (var key in keys.OrderBy(k => k.Group).ThenBy(k => k.Name))
            {
                if (await LoadJob(scheduler, key, catalog, options.Value) is { } job)
                    jobs.Add(job);
            }
            return Results.Ok(jobs);
        })
            .WithTags(Tags.Jobs).WithSummary("Every job, with its triggers")
            .WithDescription("Loads every job one by one, which suits small schedulers. The dashboard uses jobs/search.")
            .Produces<List<JobDto>>();

        // What the dashboard uses: searches, filters, counts and pages over a cheap index of every job, and
        // loads full details only for the page shown. GET /jobs loads every job and suits small schedulers.
        api.MapGet("/jobs/search", async (string? search, string? type, string? status, int? page, int? pageSize,
            HttpContext http, JobIndexReader index, JobTypeCatalog catalog, IOptions<CronlightOptions> options, CancellationToken ct) =>
        {
            if (status != null && !JobStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                return Invalid($"Unknown status '{status}'; expected one of {string.Join(", ", JobStatuses)}");

            var scheduler = await Scheduler(http, ct);
            var all = await index.Read(scheduler, ct);
            var text = search?.Trim();
            var matching = all
                .Where(j => string.IsNullOrEmpty(text)
                    || j.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || j.Group.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Where(j => string.IsNullOrEmpty(type) || j.TypeLabel == type)
                .Where(j => string.IsNullOrEmpty(status) || string.Equals(j.Status, status, StringComparison.OrdinalIgnoreCase))
                .OrderBy(j => j.Group).ThenBy(j => j.Name)
                .ToList();

            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var pages = Math.Max(1, (matching.Count + size - 1) / size);
            var current = Math.Clamp(page ?? 1, 1, pages);
            var jobs = new List<JobDto>();
            foreach (var entry in matching.Skip((current - 1) * size).Take(size))
            {
                if (await LoadJob(scheduler, entry.Key, catalog, options.Value) is { } job)
                    jobs.Add(job);
            }

            var counts = new JobCountsDto(all.Count, all.Count(j => j.Status == "Active"), all.Count(j => j.Status == "Paused"),
                all.Count(j => j.Status == "Unloadable"), all.Count(j => j.Status == "Error"));
            var types = all.Select(j => j.TypeLabel).Distinct().Order().ToList();
            return Results.Ok(new JobPageDto(jobs, matching.Count, current, size, counts, types));
        })
            .WithTags(Tags.Jobs).WithSummary("Searches, filters and pages the jobs")
            .WithDescription("search matches names and groups; type is a job type label; status is Active, Paused, Unloadable or Error. " +
                "Pages hold 50 jobs unless pageSize says otherwise, at most 200. The counts are over every job, whatever the filters.")
            .Produces<JobPageDto>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/jobs/{group}/{name}", async (string group, string name, HttpContext http, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await Scheduler(http);
            var key = JobKeyFrom(group, name);
            return await LoadJob(scheduler, key, catalog, options.Value) is { } job
                ? Results.Ok(job)
                : NoSuchJob(key);
        })
            .WithTags(Tags.Jobs).WithSummary("A job, with its triggers")
            .Produces<JobDto>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/jobs/{group}/{name}/fire-times", async (string group, string name, HttpContext http) =>
        {
            var scheduler = await Scheduler(http);
            var key = JobKeyFrom(group, name);
            if (!await scheduler.CheckExists(key))
                return NoSuchJob(key);
            var result = new List<TriggerFireTimesDto>();
            foreach (var trigger in await scheduler.GetTriggersOfJob(key))
            {
                // Skipping the times the trigger's calendar excludes, as Quartz will
                var calendar = trigger.CalendarName == null ? null : await scheduler.GetCalendar(trigger.CalendarName);
                result.Add(new TriggerFireTimesDto(trigger.Key.Group, trigger.Key.Name,
                    TriggerUtils.ComputeFireTimes((IOperableTrigger)trigger, calendar, UpcomingFireTimes)));
            }
            return Results.Ok(result);
        })
            .WithTags(Tags.Jobs).WithSummary("Each trigger's next five fire times, skipping those its calendar excludes")
            .Produces<List<TriggerFireTimesDto>>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/running", async (HttpContext http, RunningJobsReader reader, CancellationToken ct) =>
            Results.Ok(await reader.Read(await Scheduler(http, ct), ct)))
            .WithTags(Tags.Runs).WithSummary("The runs going on now")
            .WithDescription("Read from the job store, which covers every node of a cluster (source: cluster), or else this node's runs (source: local).")
            .Produces<RunningJobsDto>();

        // History is optional: these return 404 unless the Cronlight.History package is registered
        api.MapGet("/history", async (HttpContext http, string? group, string? name, string? status, long? before, int? limit, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IJobHistoryStore>() is not { } store)
                return Missing("Execution history is not enabled.");

            JobExecutionStatus? statusFilter = null;
            if (status != null)
            {
                if (!Enum.TryParse<JobExecutionStatus>(status, ignoreCase: true, out var parsed))
                    return Invalid($"Unknown status '{status}'");
                statusFilter = parsed;
            }

            var scheduler = await Scheduler(http, ct);
            var pageSize = Math.Clamp(limit ?? 50, 1, 500);
            var records = await store.Query(scheduler.SchedulerName, new JobHistoryQuery
            {
                JobGroup = group,
                JobName = name,
                Status = statusFilter,
                BeforeId = before,
                Limit = pageSize
            }, ct);

            return Results.Ok(new HistoryPageDto(records, records.Count == pageSize ? records[^1].Id : null));
        })
            .WithTags(Tags.History).WithSummary("Recorded runs, newest first")
            .WithDescription("group and name pick one job's runs, and status one outcome. Pages hold 50 runs unless limit says otherwise, " +
                "at most 500; before= takes the last page's nextBefore. 404 when execution history is not enabled.")
            .Produces<HistoryPageDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/history/{id:long}/log", async (long id, HttpContext http, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IJobHistoryStore>() is not { } store)
                return Missing("Execution history is not enabled.");
            var scheduler = await Scheduler(http, ct);
            return await store.GetLog(scheduler.SchedulerName, id, ct) is { } log
                ? Results.Ok(log)
                : Missing("This run has no log.");
        })
            .WithTags(Tags.History).WithSummary("What a run logged while it ran")
            .Produces<IReadOnlyList<JobRunLogEntry>>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/history/summary", async (HttpContext http, int? hours, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IJobHistoryStore>() is not { } store)
                return Missing("Execution history is not enabled.");

            var scheduler = await Scheduler(http, ct);
            var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 90));
            return Results.Ok(await store.Summarize(scheduler.SchedulerName, since, ct));
        })
            .WithTags(Tags.History).WithSummary("How many runs succeeded and failed in the last hours (24 unless hours says otherwise)")
            .Produces<JobHistorySummary>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/jobs/export", JobTransfer.Export)
            .WithTags(Tags.Transfer).WithSummary("Exports the jobs to a JSON or YAML file")
            .WithDescription("format is json (the default) or yaml, and group picks one group. Secrets are masked, except secret references.")
            .Produces<JobFile>(StatusCodes.Status200OK, "application/json", JobTransfer.YamlContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/cron/preview", (string expression, string? timeZone, int? count) =>
            Results.Ok(PreviewCron(expression, timeZone, Math.Clamp(count ?? 5, 1, 10))))
            .WithTags(Tags.Cron).WithSummary("Checks a cron expression and lists its next fire times")
            .WithDescription("timeZone is the zone it is read in, UTC when left out; count is how many fire times, 5 unless it says otherwise, at most 10.")
            .Produces<CronPreviewDto>();
    }

    public static void MapManageEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs", async (CreateJobRequest request, HttpContext http, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await Scheduler(http);
            var (change, problem) = await PlanCreate(scheduler, request, http, catalog, options.Value);
            if (change == null)
                return problem!;
            Audit.RecordChanges(http, change.Changes);
            await change.Apply(scheduler);
            return Results.Created($"jobs/{Uri.EscapeDataString(change.Key.Group)}/{Uri.EscapeDataString(change.Key.Name)}",
                new JobKeyDto(change.Key.Name, change.Key.Group));
        })
            .WithTags(Tags.Jobs).WithSummary("Creates a job")
            .Produces<JobKeyDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/jobs/{group}/{name}", async (string group, string name, UpdateJobRequest request, HttpContext http, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await Scheduler(http);
            var (change, problem) = await PlanUpdate(scheduler, JobKeyFrom(group, name), request, http, catalog, options.Value);
            if (change == null)
                return problem!;
            Audit.RecordChanges(http, change.Changes);
            await change.Apply(scheduler);
            return Results.NoContent();
        })
            .WithTags(Tags.Jobs).WithSummary("Changes a job")
            .WithDescription("Properties left out are left as they are. Job data keys sent are set, or removed when null, and others kept; " +
                "a masked secret (********) keeps its value. Triggers replaces the job's triggers of the kinds triggerKinds names.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/jobs/import", JobTransfer.Import)
            .WithTags(Tags.Transfer).WithSummary("Imports jobs from a JSON or YAML file")
            .WithDescription("Creates the file's jobs the scheduler does not have and updates the others to match it; never deletes a job. " +
                "Every job is checked before any is changed: if one cannot be imported, none is, and the 400 answer lists each job's problem " +
                "under jobs. dryRun=true answers with what importing would do, and changes nothing. Send YAML as application/yaml.")
            .Accepts<JobFile>("application/json", JobTransfer.YamlContentType)
            .Produces<ImportResultDto>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/jobs/{group}/{name}/pause", (string group, string name, HttpContext http, IOptions<CronlightOptions> o) =>
            OnJob(http, o.Value, group, name, (s, k) => s.PauseJob(k)))
            .WithTags(Tags.Jobs).WithSummary("Pauses a job's triggers").OnOneJob();
        api.MapPost("/jobs/{group}/{name}/resume", (string group, string name, HttpContext http, IOptions<CronlightOptions> o) =>
            OnJob(http, o.Value, group, name, ResumeJob))
            .WithTags(Tags.Jobs).WithSummary("Resumes a job's triggers, and those Quartz stopped (Error)").OnOneJob();
        api.MapPost("/jobs/{group}/{name}/trigger", RunJobEndpoint)
            .WithTags(Tags.Runs).WithSummary("Runs a job now, optionally with some of its data replaced for this run only")
            .OnOneJob().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapDelete("/jobs/{group}/{name}", (string group, string name, HttpContext http, IOptions<CronlightOptions> o) =>
            OnJob(http, o.Value, group, name, DeleteJob))
            .WithTags(Tags.Jobs).WithSummary("Deletes a job and its triggers").OnOneJob();

        api.MapPost("/jobs/{group}/{name}/runs/{fireInstanceId}/cancel", CancelRun)
            .WithTags(Tags.Runs).WithSummary("Asks a running job to stop")
            .WithDescription("Cancels the run's cancellation token; the job stops when it next checks it. Only the node running it can, so a " +
                "run on another node is refused with 409.")
            .Produces<MessageDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/jobs/{group}/{name}/retries/{trigger}/cancel-retry", CancelRetry)
            .WithTags(Tags.Runs).WithSummary("Cancels a waiting retry").OnOneJob();

        api.MapPost("/groups/{group}/pause", (string group, HttpContext http, JobIndexReader index, IOptions<CronlightOptions> o, CancellationToken ct) =>
            OnJobs(http, index, o.Value, FromRoute(group), (s, m) => s.PauseJobs(m, ct), resume: false, ct))
            .WithTags(Tags.Jobs).WithSummary("Pauses every job in a group").OnOneJob();
        api.MapPost("/groups/{group}/resume", (string group, HttpContext http, JobIndexReader index, IOptions<CronlightOptions> o, CancellationToken ct) =>
            OnJobs(http, index, o.Value, FromRoute(group), (s, m) => s.ResumeJobs(m, ct), resume: true, ct))
            .WithTags(Tags.Jobs).WithSummary("Resumes every job in a group").OnOneJob();
        api.MapPost("/scheduler/pause", (HttpContext http, JobIndexReader index, IOptions<CronlightOptions> o, CancellationToken ct) =>
            OnJobs(http, index, o.Value, null, (s, _) => s.PauseAll(ct), resume: false, ct))
            .WithTags(Tags.Scheduler).WithSummary("Pauses every job in the scheduler")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/scheduler/resume", (HttpContext http, JobIndexReader index, IOptions<CronlightOptions> o, CancellationToken ct) =>
            OnJobs(http, index, o.Value, null, (s, _) => s.ResumeAll(ct), resume: true, ct))
            .WithTags(Tags.Scheduler).WithSummary("Resumes every job in the scheduler")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Checks a new job, and returns what creating it changes, or the problem to answer with. Changes nothing itself, so
    /// an import can check every job before creating any.
    /// </summary>
    internal static async Task<(JobChange? Change, IResult? Problem)> PlanCreate(IScheduler scheduler, CreateJobRequest request,
        HttpContext http, JobTypeCatalog catalog, CronlightOptions options)
    {
        var typeName = string.IsNullOrWhiteSpace(request.Type) ? request.JobType : request.Type;
        if (string.IsNullOrWhiteSpace(typeName))
            return (null, Invalid("A job type is required: set 'type' to one of the types listed by api/job-types"));
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, Invalid("A job name is required"));
        if (HasControlCharacters(request.Name) || HasControlCharacters(request.Group))
            return (null, Invalid("Job names and groups cannot contain control characters, such as line breaks or tabs"));
        if (!catalog.TryGet(typeName, out var type, out var info))
            return (null, Invalid($"Unknown job type '{typeName}'"));
        if (!catalog.Offers(scheduler.SchedulerName, typeName))
            return (null, Invalid($"Job type '{typeName}' is not offered for scheduler '{scheduler.SchedulerName}'"));

        var data = request.Data ?? new();
        foreach (var p in info.Parameters.Where(p => p.DefaultValue != null && !data.ContainsKey(p.Key)))
            data[p.Key] = p.DefaultValue!;

        if (ReservedKey(data.Keys) is { } reserved)
            return (null, Invalid(reserved));
        if (ValidateParameters(info, data) is { } invalid)
            return (null, Invalid(invalid));
        if (request.Retry?.Problem() is { } badRetry)
            return (null, Invalid(badRetry));
        var secretKeys = SecretKeys(type);
        if (CheckSecretReferences(http, data, secretKeys, options) is { } unresolved)
            return (null, Invalid(unresolved));

        var key = new JobKey(request.Name, string.IsNullOrWhiteSpace(request.Group) ? "DEFAULT" : request.Group);
        if (!TryBuildTriggers(key, request.Triggers ?? [], [], out var triggers, out var error))
            return (null, Invalid(error));

        if (await scheduler.CheckExists(key))
            return (null, Refused($"Job {key} already exists"));
        if (await FindTriggerConflict(scheduler, triggers, new HashSet<TriggerKey>()) is { } conflict)
            return (null, Refused(conflict));

        var jobData = ToJobDataMap(data);
        if (request.Retry != null && Retries.Store(request.Retry, type) is { } policy)
            jobData[Retries.PolicyKey] = policy;
        var job = JobBuilder.Create(type)
            .WithIdentity(key)
            .WithDescription(request.Description)
            .SetJobData(jobData)
            .StoreDurably()
            .Build();

        var changes = DataChanges(new Dictionary<string, object>(), job.JobDataMap, secretKeys, options);
        AddChange(changes, "description", null, request.Description);
        AddChange(changes, "triggers", null, DescribeTriggers(triggers));
        AddChange(changes, "retry", null, DescribeRetry(Retries.For(job)));

        // One call stores the job and its triggers together, so a failure leaves nothing behind
        return (new JobChange(key, changes, triggers.Select(t => t.Key).ToList(), s => s.ScheduleJob(job, triggers, replace: false)), null);
    }

    /// <summary>Checks a change to a job, and returns what it changes, or the problem to answer with. Changes nothing itself.</summary>
    internal static async Task<(JobChange? Change, IResult? Problem)> PlanUpdate(IScheduler scheduler, JobKey key, UpdateJobRequest request,
        HttpContext http, JobTypeCatalog catalog, CronlightOptions options)
    {
        var (existing, problem) = await GetManageableJob(scheduler, key, options);
        if (problem != null)
            return (null, problem);
        if (existing == null)
            return (null, ReadOnlyJob(key, "edited"));

        var info = catalog.Describe(existing.JobType);
        var secretKeys = SecretKeys(existing.JobType);
        var setValues = request.Data?.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!);
        if (request.Data != null && ReservedKey(request.Data.Keys) is { } reserved)
            return (null, Invalid(reserved));
        // The data the job will have, so a required parameter cannot be removed either
        if (request.Data != null && info != null && ValidateParameters(info, Merged(existing.JobDataMap, request.Data)) is { } invalid)
            return (null, Invalid(invalid));
        if (request.Retry?.Problem() is { } badRetry)
            return (null, Invalid(badRetry));
        if (setValues != null && CheckSecretReferences(http, setValues, secretKeys, options) is { } unresolved)
            return (null, Invalid(unresolved));

        var requested = new List<ITrigger>();
        IReadOnlyCollection<ITrigger> current = [];
        IReadOnlySet<string> kinds = EditableKinds;
        if (request.Triggers != null)
        {
            if (!TryReplacedKinds(request, out kinds, out var badKinds))
                return (null, Invalid(badKinds));
            current = await scheduler.GetTriggersOfJob(key);
            if (!TryBuildTriggers(key, request.Triggers, current, out requested, out var error))
                return (null, Invalid(error));
            if (await FindExcludedTrigger(scheduler, requested) is { } excluded)
                return (null, Invalid(excluded));
            var own = current.Where(t => kinds.Contains(Kind(t))).Select(t => t.Key).ToHashSet();
            if (await FindTriggerConflict(scheduler, requested, own) is { } conflict)
                return (null, Refused(conflict));
        }

        var data = request.Data == null ? new JobDataMap((IDictionary<string, object>)existing.JobDataMap) : MergeJobData(existing.JobDataMap, request.Data);
        if (request.Retry != null)
        {
            data.Remove(Retries.PolicyKey);
            if (Retries.Store(request.Retry, existing.JobType) is { } policy)
                data[Retries.PolicyKey] = policy;
        }
        var changes = DataChanges(existing.JobDataMap, data, secretKeys, options);
        if (request.Description != null)
            AddChange(changes, "description", existing.Description, request.Description);
        if (request.Triggers != null)
            AddChange(changes, "triggers", DescribeTriggers(current, kinds), DescribeTriggers(requested));
        var retryBefore = Retries.For(existing);
        var retryAfter = Retries.For(existing.GetJobBuilder().SetJobData(data).Build());
        if (retryBefore != retryAfter)
            AddChange(changes, "retry", DescribeRetry(retryBefore), DescribeRetry(retryAfter));

        return (new JobChange(key, changes, requested.Select(t => t.Key).ToList(), async s =>
        {
            if (request.Data != null || request.Description != null || request.Retry != null)
            {
                var updated = existing.GetJobBuilder()
                    .WithDescription(request.Description ?? existing.Description)
                    .SetJobData(data)
                    .Build();
                await s.AddJob(updated, replace: true, storeNonDurableWhileAwaitingScheduling: true);
            }

            if (request.Triggers != null)
                await ReplaceTriggers(s, key, requested, kinds);
        }), null);
    }

    /// <summary>Whether a cron expression is valid, and its next fire times in the time zone (UTC when null).</summary>
    internal static CronPreviewDto PreviewCron(string expression, string? timeZone, int count)
    {
        if (!TryResolveTimeZone(timeZone, out var tz))
            return new CronPreviewDto(false, expression, null, [], $"Unknown time zone '{timeZone}'");
        try
        {
            var cron = new CronExpression(expression) { TimeZone = tz };
            var times = new List<DateTimeOffset>();
            DateTimeOffset? next = DateTimeOffset.UtcNow;
            for (var i = 0; i < count && next != null; i++)
            {
                next = cron.GetNextValidTimeAfter(next.Value);
                if (next != null)
                    times.Add(next.Value);
            }
            // Quartz has no human-readable summary (GetExpressionSummary is a debug dump), so leave it to the UI
            return new CronPreviewDto(true, expression, null, times, null);
        }
        catch (FormatException ex)
        {
            return new CronPreviewDto(false, expression, null, [], ex.Message);
        }
    }

    /// <summary>The groups of endpoints the OpenAPI description lists.</summary>
    private static class Tags
    {
        public const string Scheduler = "Scheduler";
        public const string Jobs = "Jobs";
        public const string Runs = "Runs";
        public const string History = "History";
        public const string Transfer = "Export and import";
        public const string Cron = "Cron";
    }

    /// <summary>What an action on one job, or one group, answers.</summary>
    private static RouteHandlerBuilder OnOneJob(this RouteHandlerBuilder endpoint) =>
        endpoint.Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Pauses or resumes every job in a group, or with a null group every job in the scheduler. Quartz does it in the
    /// job store, so it reaches every node. Jobs another app owns (their type cannot be loaded here) are not this
    /// dashboard's to change, so unless AllowManagingUnloadableJobs is on, a group or scheduler holding any is refused
    /// rather than half done. Resuming also brings back jobs in Error, as resuming one job does.
    /// </summary>
    private static async Task<IResult> OnJobs(HttpContext http, JobIndexReader index, CronlightOptions options, string? group,
        Func<IScheduler, GroupMatcher<JobKey>, Task> action, bool resume, CancellationToken cancellationToken)
    {
        var scheduler = await Scheduler(http, cancellationToken);
        var jobs = (await index.Read(scheduler, cancellationToken)).Where(j => group == null || j.Group == group).ToList();
        if (group != null && jobs.Count == 0)
            return Missing($"Group '{group}' has no jobs.");

        var foreign = jobs.Where(j => !j.Loadable).ToList();
        if (foreign.Count > 0 && !options.AllowManagingUnloadableJobs)
            return Results.Problem(
                title: "Some jobs are read-only in this dashboard.",
                detail: $"{(group == null ? "The scheduler" : $"Group '{group}'")} has {foreign.Count} job(s) whose type cannot be loaded in " +
                        $"this application, such as {foreign[0].Group}.{foreign[0].Name}. They probably belong to another application " +
                        "sharing this scheduler, so they cannot be paused or resumed here. Pause or resume this application's jobs one " +
                        "by one instead.",
                statusCode: StatusCodes.Status409Conflict);

        Audit.RecordChanges(http, new Dictionary<string, CronlightAuditChange> { ["jobs"] = new(null, jobs.Count.ToString()) });
        // Before resuming, which wakes the scheduler to look for triggers due
        if (resume)
            foreach (var job in jobs.Where(j => j.Error))
                await ResetErrors(scheduler, job.Key, cancellationToken);
        await action(scheduler, GroupMatcher<JobKey>.GroupEquals(group ?? ""));
        return Results.NoContent();
    }

    /// <summary>
    /// Resumes the job's paused triggers, and sets those in Quartz's Error state back to normal: Quartz puts every trigger
    /// of a job it could not create in Error, and they never fire again until reset. Once the cause is fixed (a missing
    /// dependency registered, say), resuming the job lets it run.
    /// </summary>
    internal static async Task ResumeJob(IScheduler scheduler, JobKey key)
    {
        // Resetting does not wake the scheduler, which could sleep for 30 seconds before it sees the triggers; resuming does
        await ResetErrors(scheduler, key, CancellationToken.None);
        await scheduler.ResumeJob(key);
    }

    private static async Task ResetErrors(IScheduler scheduler, JobKey key, CancellationToken cancellationToken)
    {
        var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken);
        var left = triggers.Count;
        foreach (var trigger in triggers)
        {
            if (await scheduler.GetTriggerState(trigger.Key, cancellationToken) != TriggerState.Error)
                continue;
            // A one-off trigger that has fired (Run now's, a retry's) has nothing left to fire, and reset it would wait
            // forever: it goes, as after a run, unless it would take a job that is not durable with it
            if (trigger.GetNextFireTimeUtc() == null && (left > 1 || await IsDurable(scheduler, key, cancellationToken)))
            {
                await scheduler.UnscheduleJob(trigger.Key, cancellationToken);
                left--;
            }
            else
                await scheduler.ResetTriggerFromErrorState(trigger.Key, cancellationToken);
        }
    }

    private static async Task<bool> IsDurable(IScheduler scheduler, JobKey key, CancellationToken cancellationToken)
    {
        try
        {
            return (await scheduler.GetJobDetail(key, cancellationToken))?.Durable == true;
        }
        catch (JobPersistenceException)
        {
            // Its type cannot be loaded here
            return false;
        }
    }

    /// <summary>
    /// Unschedules a waiting retry. The attempt that failed stays in history as Retried, and no alert is sent for it:
    /// cancelling is a decision someone made.
    /// </summary>
    private static async Task<IResult> CancelRetry(string group, string name, string trigger, HttpContext http,
        IOptions<CronlightOptions> options)
    {
        var scheduler = await Scheduler(http);
        var key = JobKeyFrom(group, name);
        var (_, problem) = await GetManageableJob(scheduler, key, options.Value);
        if (problem != null)
            return problem;

        var triggerKey = new TriggerKey(trigger, Retries.TriggerGroup);
        if (await scheduler.GetTrigger(triggerKey) is not { } retry || !retry.JobKey.Equals(key))
            return Missing($"Job {key} has no waiting retry {trigger}; it may have run already.");
        // Quartz deletes a non-durable job together with its last trigger
        if (await scheduler.GetJobDetail(key) is { Durable: false } && (await scheduler.GetTriggersOfJob(key)).Count == 1)
            return Refused($"The retry is job {key}'s only trigger, and the job is not durable: cancelling it would delete the job.");

        await scheduler.UnscheduleJob(triggerKey);
        return Results.NoContent();
    }

    /// <summary>
    /// Runs the job now, optionally with some of its data replaced for this run. The replacements go in the run's
    /// trigger data, so the saved job is unchanged and the job reads them as usual from its merged data.
    /// </summary>
    private static async Task<IResult> RunJobEndpoint(string group, string name, RunJobRequest? request, HttpContext http,
        JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        await RunJob(await Scheduler(http), JobKeyFrom(group, name), request, http, catalog, options.Value);

    /// <summary>Runs the job now, as api/jobs/{group}/{name}/trigger does, in the scheduler given.</summary>
    internal static async Task<IResult> RunJob(IScheduler scheduler, JobKey key, RunJobRequest? request, HttpContext http,
        JobTypeCatalog catalog, CronlightOptions options)
    {
        var (existing, problem) = await GetManageableJob(scheduler, key, options);
        if (problem != null)
            return problem;

        var changes = new Dictionary<string, CronlightAuditChange>();
        if (request?.RetryOf is { Length: > 0 } retryOf)
            changes["retryOf"] = new(null, retryOf.Length <= 200 ? retryOf : retryOf[..200]);

        var data = new JobDataMap();
        var requested = request?.Data ?? new();
        if (requested.Count > 0)
        {
            if (existing == null)
                return ReadOnlyJob(key, "run with different data");
            if (ReservedKey(requested.Keys) is { } reserved)
                return Invalid(reserved);

            var saved = existing.JobDataMap;
            // A masked or unchanged value is the saved one: nothing to replace
            var overrides = requested
                .Where(kv => kv.Value != Mask && (!saved.TryGetValue(kv.Key, out var old) || old?.ToString() != kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var info = catalog.Describe(existing.JobType);
            var secretKeys = SecretKeys(existing.JobType);
            if (info != null && ValidateParameters(info, Merged(saved, overrides!)) is { } invalid)
                return Invalid(invalid);
            if (CheckSecretReferences(http, overrides, secretKeys, options) is { } unresolved)
                return Invalid(unresolved);

            foreach (var (k, v) in overrides)
            {
                var old = saved.TryGetValue(k, out var o) ? o : null;
                // Keeps the saved value's type, as editing the job does
                data[k] = old is not (null or string) ? TryConvert(v, old.GetType()) ?? v : v;
                changes[$"data.{k}"] = new(Display(k, old?.ToString(), secretKeys, options), Display(k, v, secretKeys, options));
            }
        }
        Audit.RecordChanges(http, changes);

        await scheduler.TriggerJob(key, data);
        return Results.NoContent();
    }

    /// <summary>
    /// Asks a running job to stop by cancelling its <see cref="IJobExecutionContext.CancellationToken"/>.
    /// Quartz can only interrupt runs on its own node, so a run on another cluster node is refused.
    /// </summary>
    private static async Task<IResult> CancelRun(string group, string name, string fireInstanceId, HttpContext http,
        RunningJobsReader reader, IOptions<CronlightOptions> options, CancellationToken ct)
    {
        var scheduler = await Scheduler(http, ct);
        var key = JobKeyFrom(group, name);
        var (_, problem) = await GetManageableJob(scheduler, key, options.Value);
        if (problem != null)
            return problem;

        var local = (await scheduler.GetCurrentlyExecutingJobs(ct))
            .FirstOrDefault(c => c.FireInstanceId == fireInstanceId && c.JobDetail.Key.Equals(key));
        if (local != null)
        {
            if (local.CancellationToken.IsCancellationRequested)
                return Results.Accepted(value: new MessageDto("Cancellation was already requested."));
            local.Put(CronlightContextKeys.CancelledBy, Audit.UserName(http.User));
            await scheduler.Interrupt(fireInstanceId, ct);
            return Results.Accepted(value: new MessageDto("Cancellation requested. The job stops when it next checks its cancellation token."));
        }

        var elsewhere = (await reader.Read(scheduler, ct)).Jobs
            .FirstOrDefault(j => j.FireInstanceId == fireInstanceId && j.Group == group && j.Name == name);
        return elsewhere != null
            ? Results.Problem(
                title: "This run is on another node.",
                detail: $"This run is on node {elsewhere.InstanceName}. Only the node running a job can cancel it; open the dashboard there.",
                statusCode: StatusCodes.Status409Conflict)
            : Results.Problem(
                title: "This run is not running.",
                detail: "This run has already finished, or was never started.",
                statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Quartz lists the job's triggers, then unschedules each, and fails if one has gone in between: a one-off trigger
    /// that has just fired, such as Run now's, removes itself. Asked again, it no longer lists that trigger.
    /// </summary>
    private static async Task DeleteJob(IScheduler scheduler, JobKey key)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await scheduler.DeleteJob(key);
                return;
            }
            catch (SchedulerException) when (attempt < 3)
            {
                // A job that is not durable goes with its last trigger
                if (!await scheduler.CheckExists(key))
                    return;
            }
        }
    }

    internal static async Task<IResult> OnJob(HttpContext http, CronlightOptions options, string group, string name, Func<IScheduler, JobKey, Task> action)
    {
        var scheduler = await Scheduler(http);
        var key = JobKeyFrom(group, name);
        var (_, problem) = await GetManageableJob(scheduler, key, options);
        if (problem != null)
            return problem;
        await action(scheduler, key);
        return Results.NoContent();
    }

    /// <summary>
    /// Returns the job detail, or a 404/409 result. Detail is null (with no problem) only for an
    /// unloadable job when AllowManagingUnloadableJobs is on.
    /// </summary>
    internal static async Task<(IJobDetail? Detail, IResult? Problem)> GetManageableJob(IScheduler scheduler, JobKey key, CronlightOptions options)
    {
        if (!await scheduler.CheckExists(key))
            return (null, NoSuchJob(key));
        try
        {
            return (await scheduler.GetJobDetail(key), null);
        }
        catch (JobPersistenceException ex)
        {
            if (options.AllowManagingUnloadableJobs)
                return (null, null);
            return (null, ReadOnlyJob(key, "changed", ex.InnerException?.Message ?? ex.Message));
        }
    }

    // Every error is ProblemDetails: its title names the kind of error and its detail says what is wrong, so a client
    // can show the detail alone

    internal static IResult Invalid(string detail) =>
        Results.Problem(title: "Invalid request.", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Missing(string detail) =>
        Results.Problem(title: "Not found.", detail: detail, statusCode: StatusCodes.Status404NotFound);

    private static IResult Refused(string detail) =>
        Results.Problem(title: "Not possible as things stand.", detail: detail, statusCode: StatusCodes.Status409Conflict);

    private static IResult NoSuchJob(JobKey key) => Missing($"Job {key} does not exist.");

    /// <summary>A job of another application sharing the scheduler, whose type cannot be loaded here.</summary>
    /// <param name="cannotBe">What cannot be done to it, e.g. "edited".</param>
    /// <param name="reason">Why its type cannot be loaded, when known.</param>
    private static IResult ReadOnlyJob(JobKey key, string cannotBe, string? reason = null) =>
        Results.Problem(
            title: "Job is read-only in this dashboard.",
            detail: $"The type of job {key} cannot be loaded in this application{(reason == null ? "" : $" ({reason})")}. It probably " +
                    $"belongs to another application sharing this scheduler, so it cannot be {cannotBe} here.",
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>The kinds of trigger the dashboard creates and edits (see <see cref="Kind"/>).</summary>
    internal static readonly IReadOnlySet<string> EditableKinds = new HashSet<string> { "Cron", "Once", "Interval" };

    private static string? EditableKind(string? kind) =>
        EditableKinds.FirstOrDefault(k => string.Equals(k, kind ?? "Cron", StringComparison.OrdinalIgnoreCase));

    /// <summary>The kinds of trigger an update replaces: those it names, else cron and the kinds it sends.</summary>
    private static bool TryReplacedKinds(UpdateJobRequest request, out IReadOnlySet<string> kinds, [NotNullWhen(false)] out string? error)
    {
        error = null;
        var sent = request.Triggers!.Select(t => EditableKind(t.Kind)).OfType<string>().ToHashSet();
        if (request.TriggerKinds == null)
        {
            kinds = sent.Append("Cron").ToHashSet();
            return true;
        }

        kinds = request.TriggerKinds.Select(EditableKind).OfType<string>().ToHashSet();
        if (request.TriggerKinds.FirstOrDefault(k => EditableKind(k) == null) is { } unknown)
            error = $"Unknown trigger kind '{unknown}' in triggerKinds; expected Cron, Once or Interval";
        else if (sent.Except(kinds).FirstOrDefault() is { } missing)
            error = $"Triggers holds a {missing} trigger, but triggerKinds does not name {missing}";
        return error == null;
    }

    private static async Task ReplaceTriggers(IScheduler scheduler, JobKey key, List<ITrigger> requested, IReadOnlySet<string> kinds)
    {
        var all = await scheduler.GetTriggersOfJob(key);
        var current = all.Where(t => kinds.Contains(Kind(t))).ToDictionary(t => t.Key);
        var stale = current.Keys.Where(k => requested.All(r => !r.Key.Equals(k))).ToList();

        // Quartz deletes a non-durable job (the default for jobs the host registers) together with its
        // last trigger. When the edit leaves no triggers, make the job durable so it stays for manual runs.
        if (requested.Count == 0 && stale.Count == all.Count && await scheduler.GetJobDetail(key) is { Durable: false } job)
            await scheduler.AddJob(job.GetJobBuilder().StoreDurably().Build(), replace: true);

        // Add and update first, remove last: the job keeps a trigger throughout
        foreach (var trigger in requested)
        {
            if (!current.TryGetValue(trigger.Key, out var old))
            {
                await scheduler.ScheduleJob(trigger);
                continue;
            }
            if (SameSchedule(old, trigger))
                continue;

            // Rescheduling replaces the trigger, so carry over a paused state explicitly
            var wasPaused = await scheduler.GetTriggerState(old.Key) == TriggerState.Paused;
            await scheduler.RescheduleJob(old.Key, trigger);
            if (wasPaused)
                await scheduler.PauseTrigger(trigger.Key);
        }

        foreach (var staleKey in stale)
            await scheduler.UnscheduleJob(staleKey);
    }

    /// <summary>Whether a trigger sent back by the editor still says what the stored one does, so it is left alone.</summary>
    private static bool SameSchedule(ITrigger old, ITrigger requested) => (old, requested) switch
    {
        (ICronTrigger o, ICronTrigger r) => o.CronExpressionString == r.CronExpressionString && o.TimeZone.Id == r.TimeZone.Id
            && Misfires.SkipsMissed(o) == Misfires.SkipsMissed(r),
        (ISimpleTrigger o, ISimpleTrigger r) => o.RepeatInterval == r.RepeatInterval && o.RepeatCount == r.RepeatCount
            && o.StartTimeUtc == r.StartTimeUtc && Misfires.SkipsMissed(o) == Misfires.SkipsMissed(r),
        _ => false,
    };

    /// <summary>
    /// A trigger keeps its calendar when its schedule changes, so a new schedule may fall only on times the calendar
    /// excludes (a one-time run on a holiday). Quartz would refuse it halfway through the save; checked up front instead.
    /// </summary>
    private static async Task<string?> FindExcludedTrigger(IScheduler scheduler, List<ITrigger> triggers)
    {
        foreach (var trigger in triggers.Where(t => t.CalendarName != null))
        {
            var calendar = await scheduler.GetCalendar(trigger.CalendarName!);
            if (TriggerUtils.ComputeFireTimes((IOperableTrigger)trigger, calendar, 1).Count == 0)
                return $"Trigger '{trigger.Key.Name}' would never fire: its calendar '{trigger.CalendarName}' excludes every time it runs";
        }
        return null;
    }

    /// <summary>
    /// Trigger keys are unique across all jobs, so a name another job's trigger already uses would make
    /// the save fail halfway. Checked up front instead.
    /// </summary>
    private static async Task<string?> FindTriggerConflict(IScheduler scheduler, List<ITrigger> triggers, IReadOnlySet<TriggerKey> own)
    {
        foreach (var trigger in triggers.Where(t => !own.Contains(t.Key)))
        {
            if (await scheduler.CheckExists(trigger.Key))
                return $"Trigger name '{trigger.Key.Name}' is already in use in group '{trigger.Key.Group}'";
        }
        return null;
    }

    /// <summary>
    /// The saved job data with the requested changes: keys requested are set, or removed when requested as null, and the
    /// rest are kept. Values come as the text the dashboard showed (ToString(), secrets masked): a masked value or one that
    /// comes back unchanged keeps its original object, and a changed value keeps the original primitive type when it
    /// converts to it, so non-string job data survives an edit.
    /// </summary>
    private static JobDataMap MergeJobData(JobDataMap existing, Dictionary<string, string?> requested)
    {
        var data = new JobDataMap((IDictionary<string, object>)existing);
        foreach (var (k, v) in requested)
        {
            var known = existing.TryGetValue(k, out var old);
            if (v == null)
                data.Remove(k);
            else if (known && (v == Mask || (old is not (null or string) && v == old.ToString())))
                data[k] = old;
            else if (known && old is not (null or string))
                data[k] = TryConvert(v, old.GetType()) ?? v;
            else
                data[k] = v;
        }
        return data;
    }

    /// <summary>The job data as text, as [JobParameter] rules check it, with the requested values set or (null) removed.</summary>
    private static Dictionary<string, string> Merged(JobDataMap saved, IReadOnlyDictionary<string, string?> changes)
    {
        var merged = saved.Where(kv => !Retries.IsReserved(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        foreach (var (k, v) in changes)
        {
            if (v == null)
                merged.Remove(k);
            else
                merged[k] = v;
        }
        return merged;
    }

    private static object? TryConvert(string value, Type type)
    {
        if (type.IsEnum)
            return Enum.TryParse(type, value, ignoreCase: true, out var parsed) ? parsed : null;
        if (!typeof(IConvertible).IsAssignableFrom(type))
            return null;
        try
        {
            // Current culture, matching the ToString() the value was shown with
            return Convert.ChangeType(value, type, CultureInfo.CurrentCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <param name="existing">
    /// The job's current triggers. A requested name matching one of them keeps that trigger's group
    /// (host-registered triggers are often in DEFAULT rather than the job's group), and unnamed
    /// triggers never take one of their names.
    /// </param>
    private static bool TryBuildTriggers(JobKey key, List<TriggerRequest> requests, IReadOnlyCollection<ITrigger> existing,
        out List<ITrigger> triggers, [NotNullWhen(false)] out string? error)
    {
        triggers = new();
        error = null;
        var taken = requests.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => r.Name!.Trim())
            .Concat(existing.Select(t => t.Key.Name))
            .ToHashSet();
        var next = 1;
        foreach (var r in requests)
        {
            string name;
            if (!string.IsNullOrWhiteSpace(r.Name))
                name = r.Name.Trim();
            else
            {
                do name = $"{key.Name}-trigger-{next++}";
                while (!taken.Add(name));
            }
            if (HasControlCharacters(name))
            {
                error = "Trigger names cannot contain control characters, such as line breaks or tabs";
                return false;
            }
            if (triggers.Any(t => t.Key.Name == name))
            {
                error = $"Duplicate trigger name '{name}'";
                return false;
            }

            var stored = existing.FirstOrDefault(t => t.Key.Name == name);
            var builder = TriggerBuilder.Create()
                .WithIdentity(name, stored?.Key.Group ?? key.Group)
                .ForJob(key);
            // The editor changes only the schedule: what the host set in code stays, so a trigger whose calendar
            // excludes holidays does not start firing on them
            if (stored != null)
                builder.ModifiedByCalendar(stored.CalendarName).WithPriority(stored.Priority)
                    .WithDescription(stored.Description).UsingJobData(stored.JobDataMap);
            error = (r.Kind ?? "Cron").ToLowerInvariant() switch
            {
                "cron" => Cron(r, builder),
                "once" => Once(r, builder, stored),
                "interval" => Interval(r, builder, stored),
                _ => $"Unknown trigger kind '{r.Kind}'; expected Cron, Once or Interval",
            };
            if (error != null)
                return false;
            triggers.Add(builder.Build());
        }
        return true;
    }

    private static string? Cron(TriggerRequest r, TriggerBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(r.CronExpression) || !CronExpression.IsValidExpression(r.CronExpression))
            return $"Invalid cron expression '{r.CronExpression}'";
        if (!TryResolveTimeZone(r.TimeZone, out var tz))
            return $"Unknown time zone '{r.TimeZone}'";
        if (new CronExpression(r.CronExpression.Trim()) { TimeZone = tz }.GetNextValidTimeAfter(DateTimeOffset.UtcNow) == null)
            return $"Cron expression '{r.CronExpression}' never fires again";

        builder.WithCronSchedule(r.CronExpression.Trim(), c =>
        {
            c.InTimeZone(tz);
            if (r.SkipMissed)
                c.WithMisfireHandlingInstructionDoNothing();
        });
        return null;
    }

    /// <param name="stored">The job's trigger by this name, if it has one: sent back unchanged, its time may have passed.</param>
    private static string? Once(TriggerRequest r, TriggerBuilder builder, ITrigger? stored)
    {
        if (r.StartAtUtc is not { } start)
            return "A one-time trigger needs the time it runs (startAtUtc)";
        if (start <= DateTimeOffset.UtcNow && (stored as ISimpleTrigger)?.StartTimeUtc != start)
            return $"The time {start.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC is in the past";

        builder.StartAt(start).WithSimpleSchedule(s =>
        {
            // Explicit rather than Quartz's smart policy, so the dashboard shows exactly what happens
            if (r.SkipMissed)
                s.WithMisfireHandlingInstructionNextWithRemainingCount();
            else
                s.WithMisfireHandlingInstructionFireNow();
        });
        return null;
    }

    private const long MaxIntervalSeconds = 366L * 24 * 60 * 60;

    private static string? Interval(TriggerRequest r, TriggerBuilder builder, ITrigger? stored)
    {
        if (r.IntervalSeconds is not { } seconds || seconds < 1 || seconds > MaxIntervalSeconds)
            return "The interval must be between 1 second and 366 days, in seconds (intervalSeconds)";
        if (r.RepeatCount is < 0)
            return "The repeat count cannot be negative; leave it out to repeat forever";
        // Quartz would store it as a one-time trigger, and the interval would be lost
        if (r.RepeatCount == 0)
            return "An interval trigger that runs once is a one-time trigger: use kind Once, or a repeat count of 1 or more";
        var interval = TimeSpan.FromSeconds(seconds);

        var now = DateTimeOffset.UtcNow;
        var start = r.StartAtUtc ?? now;
        var repeat = r.RepeatCount ?? SimpleTriggerImpl.RepeatIndefinitely;
        var old = stored != null && Kind(stored) == "Interval" ? (ISimpleTrigger)stored : null;
        // Sent back as it is stored: left alone (ReplaceTriggers does not reschedule it), however long ago it started
        var unchanged = old != null && old.StartTimeUtc == start && old.RepeatInterval == interval && old.RepeatCount == repeat
            && Misfires.SkipsMissed(old) == r.SkipMissed;
        if (!unchanged && start < now)
        {
            // A start already past, as an edited trigger sends back, moves to the next run on the same rhythm, so that
            // changing the trigger does not run the job at once
            start += interval * Math.Ceiling((now - start) / interval);
            // Quartz counts runs from the start, so the runs this trigger has had come off the runs left
            if (old != null && repeat != SimpleTriggerImpl.RepeatIndefinitely)
            {
                var left = repeat + 1 - old.TimesTriggered;
                if (left < 1)
                    return $"Trigger '{old.Key.Name}' has already run {old.TimesTriggered} times, as many as it was asked to; " +
                           "ask for more runs, or remove it";
                repeat = left - 1;
            }
        }

        builder.StartAt(start).WithSimpleSchedule(s =>
        {
            s.WithInterval(interval);
            if (repeat == SimpleTriggerImpl.RepeatIndefinitely)
                s.RepeatForever();
            else
                s.WithRepeatCount(repeat);
            if (r.SkipMissed)
                s.WithMisfireHandlingInstructionNextWithRemainingCount();
            else
                s.WithMisfireHandlingInstructionNowWithExistingCount();
        });
        return null;
    }

    private static readonly string[] JobStatuses = ["Active", "Paused", "Unloadable", "Error"];

    /// <summary>The job as the dashboard shows it, or null when it does not exist (e.g. deleted meanwhile).</summary>
    internal static async Task<JobDto?> LoadJob(IScheduler scheduler, JobKey key, JobTypeCatalog catalog, CronlightOptions options)
    {
        IJobDetail? detail;
        try
        {
            detail = await scheduler.GetJobDetail(key);
        }
        catch (JobPersistenceException ex)
        {
            // Job type not loadable in this process (e.g. another app sharing the scheduler name)
            return new JobDto(key.Name, key.Group, null, null, null, false, false,
                ex.InnerException?.Message ?? ex.Message, new Dictionary<string, string?>(), await LoadTriggers(scheduler, key));
        }

        if (detail == null)
            return null;
        var info = catalog.Describe(detail.JobType);
        var secretKeys = SecretKeys(detail.JobType);

        var data = detail.JobDataMap.Where(kv => !Retries.IsReserved(kv.Key)).ToDictionary(
            kv => kv.Key,
            kv => Display(kv.Key, kv.Value?.ToString(), secretKeys, options)
        );

        return new JobDto(key.Name, key.Group, detail.Description, detail.JobType.FullName, info?.DisplayName,
            detail.Durable, true, null, data, await LoadTriggers(scheduler, key), Retries.For(detail), info?.Description);
    }

    private static async Task<IReadOnlyList<TriggerDto>> LoadTriggers(IScheduler scheduler, JobKey key)
    {
        var result = new List<TriggerDto>();
        foreach (var trigger in await scheduler.GetTriggersOfJob(key))
        {
            var state = await scheduler.GetTriggerState(trigger.Key);
            var cron = trigger as ICronTrigger;
            var retry = trigger.Key.Group == Retries.TriggerGroup;
            var kind = Kind(trigger);
            // Waiting retries and pending manual runs are simple triggers too, but not schedules of the job
            var simple = kind is "Once" or "Interval" ? (ISimpleTrigger)trigger : null;
            result.Add(new TriggerDto(
                trigger.Key.Name,
                trigger.Key.Group,
                kind,
                cron?.CronExpressionString,
                cron?.TimeZone.Id,
                state.ToString(),
                trigger.GetNextFireTimeUtc(),
                trigger.GetPreviousFireTimeUtc(),
                Misfires.Instruction(trigger),
                // Quartz's GetString throws for a missing key, as on a trigger put in the retry group by hand
                retry && trigger.JobDataMap.TryGetValue(Retries.AttemptKey, out var value) && int.TryParse(value?.ToString(), out var attempt)
                    ? attempt
                    : null,
                simple?.StartTimeUtc,
                kind == "Interval" ? (long)simple!.RepeatInterval.TotalSeconds : null,
                simple is { RepeatCount: > 0 } ? simple.RepeatCount : null,
                Misfires.SkipsMissed(trigger),
                trigger.CalendarName
            ));
        }
        return result;
    }

    /// <summary>
    /// Cron, Once and Interval, which the dashboard edits; Retry for a waiting retry and RunNow for a manual run waiting to
    /// start (e.g. behind a run of a job that does not run concurrently), which it shows but never changes; else the
    /// trigger's class name. A simple trigger whose interval is not a whole number of seconds up to 366 days is shown by
    /// class name too, since the editor could not send it back as it is.
    /// </summary>
    internal static string Kind(ITrigger trigger) => trigger switch
    {
        ICronTrigger => "Cron",
        _ when trigger.Key.Group == Retries.TriggerGroup => "Retry",
        ISimpleTrigger { RepeatCount: 0 } when IsManualRun(trigger) => "RunNow",
        ISimpleTrigger { RepeatCount: 0 } => "Once",
        ISimpleTrigger s when s.RepeatInterval.Ticks % TimeSpan.TicksPerSecond == 0
            && s.RepeatInterval.TotalSeconds is >= 1 and <= MaxIntervalSeconds => "Interval",
        _ => trigger.GetType().Name,
    };

    // Quartz's TriggerJob names its trigger MT_ and a random number, in the default group
    private static bool IsManualRun(ITrigger trigger) =>
        trigger.Key.Group == SchedulerConstants.DefaultGroup && trigger.Key.Name.StartsWith("MT_", StringComparison.Ordinal)
        && long.TryParse(trigger.Key.Name.AsSpan(3), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// A job's group or name as the route gives it. ASP.NET Core decodes a request's path but leaves %2F as it is, so a
    /// slash in a group or name, which the dashboard escapes, arrives as %2F.
    /// </summary>
    internal static string FromRoute(string value) => value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static JobKey JobKeyFrom(string group, string name) => new(FromRoute(name), FromRoute(group));

    /// <summary>
    /// Names are written into the audit log and other logs as they are, so a line break in one could forge a log line.
    /// </summary>
    private static bool HasControlCharacters(string? name) => name != null && name.Any(char.IsControl);

    // Names already warned about, so each is logged once per process
    private static readonly ConcurrentDictionary<string, bool> SettingsWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settings for a name no scheduler is served by protect nothing, which is easily missed with a typo. Checked when
    /// the dashboard asks for its scheduler, by which time schedulers added after startup are there too.
    /// </summary>
    private static void WarnAboutSettingsForNoScheduler(CronlightOptions options, IReadOnlyList<IScheduler> served, ILoggerFactory loggers)
    {
        foreach (var name in options.Schedulers.Keys)
        {
            if (served.Any(s => string.Equals(s.SchedulerName, name, StringComparison.OrdinalIgnoreCase)) || !SettingsWarned.TryAdd(name, true))
                continue;
            loggers.CreateLogger("Cronlight").LogWarning(
                "Cronlight has settings for scheduler '{Scheduler}' (o.Schedulers), but no scheduler by that name is served here, so its " +
                "policies and job types apply to nothing. Served: {Served}", name, string.Join(", ", served.Select(s => s.SchedulerName)));
        }
    }

    internal static async Task<bool> CanManage(HttpContext http, CronlightOptions options, string schedulerName)
    {
        if (options.ReadOnly)
            return false;
        options.Schedulers.TryGetValue(schedulerName, out var access);
        return await Authorized(http, options.ManagePolicy ?? options.ViewPolicy)
            && await Authorized(http, access?.ManagePolicy ?? access?.ViewPolicy);
    }

    internal static async Task<bool> CanView(HttpContext http, CronlightOptions options, string schedulerName) =>
        !options.Schedulers.TryGetValue(schedulerName, out var access) || await Authorized(http, access.ViewPolicy);

    internal static async Task<bool> Authorized(HttpContext http, string? policy)
    {
        if (policy == null)
            return true;
        // Resolved lazily: hosts without policies need not register authorization at all
        var auth = http.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await auth.AuthorizeAsync(http.User, policy)).Succeeded;
    }

    /// <summary>Checks [JobParameter] rules. Masked secrets are sent back unchanged, so they are not checked.</summary>
    private static string? ValidateParameters(JobTypeInfo info, Dictionary<string, string> data)
    {
        var missing = info.Parameters
            .Where(p => p.Required && string.IsNullOrWhiteSpace(data.GetValueOrDefault(p.Key)))
            .Select(p => p.Key)
            .ToList();
        if (missing.Count > 0)
            return $"Missing required parameters: {string.Join(", ", missing)}";

        foreach (var p in info.Parameters.Where(p => p.Choices is { Count: > 0 }))
        {
            if (data.GetValueOrDefault(p.Key) is { Length: > 0 } value && value != Mask && !p.Choices!.Contains(value))
                return $"'{value}' is not allowed for {p.Key}; expected one of {string.Join(", ", p.Choices!)}";
        }
        return null;
    }

    private static JobDataMap ToJobDataMap(Dictionary<string, string> data) =>
        new((IDictionary<string, object>)data.ToDictionary(kv => kv.Key, kv => (object)kv.Value));

    /// <summary>
    /// The keys a job type declares secret with [JobParameter], read from the type itself so they are masked whether or
    /// not the type is one the dashboard offers.
    /// </summary>
    internal static HashSet<string> SecretKeys(Type? jobType) =>
        jobType?.GetCustomAttributes<JobParameterAttribute>().Where(p => p.Type == JobParameterType.Secret).Select(p => p.Key).ToHashSet()
        ?? new HashSet<string>();

    /// <summary>Job data that differs between two maps, by "data.&lt;key&gt;", for the audit log. Sensitive values are masked.</summary>
    private static Dictionary<string, CronlightAuditChange> DataChanges(
        IDictionary<string, object> before, IDictionary<string, object> after, HashSet<string> secretKeys, CronlightOptions options)
    {
        var changes = new Dictionary<string, CronlightAuditChange>();
        // Cronlight's own keys are audited as what they mean, e.g. "retry"
        foreach (var key in before.Keys.Union(after.Keys).Where(k => !Retries.IsReserved(k)))
        {
            var old = before.TryGetValue(key, out var o) ? o?.ToString() : null;
            var @new = after.TryGetValue(key, out var n) ? n?.ToString() : null;
            if (old == @new)
                continue;
            changes[$"data.{key}"] = new(Display(key, old, secretKeys, options), Display(key, @new, secretKeys, options));
        }
        return changes;
    }

    private static void AddChange(Dictionary<string, CronlightAuditChange> changes, string field, string? before, string? after)
    {
        if (before != after)
            changes[field] = new(before, after);
    }

    private static string? DescribeRetry(JobRetryPolicy? policy) =>
        policy == null ? null
        : $"{policy.MaxRetries} {(policy.MaxRetries == 1 ? "retry" : "retries")}, {policy.DelaySeconds}s {(policy.Exponential ? "doubling" : "apart")}"
          + (policy.RetryOn is { Count: > 0 } types ? $", only on {string.Join(", ", types)}" : "");

    private static string? ReservedKey(IEnumerable<string> keys) =>
        keys.FirstOrDefault(Retries.IsReserved) is { } key
            ? $"'{key}' is reserved: job data keys starting with '{Retries.ReservedPrefix}' are Cronlight's own"
            : null;

    private static string? DescribeTriggers(IEnumerable<ITrigger> triggers, IReadOnlySet<string>? kinds = null) =>
        string.Join("; ", triggers.Where(t => (kinds ?? EditableKinds).Contains(Kind(t))).Select(DescribeTrigger)) is { Length: > 0 } text
            ? text
            : null;

    private static string DescribeTrigger(ITrigger trigger)
    {
        var skips = Misfires.SkipsMissed(trigger) ? ", skips missed" : "";
        return trigger switch
        {
            ICronTrigger cron => $"{cron.CronExpressionString} ({cron.TimeZone.Id}{skips})",
            ISimpleTrigger { RepeatCount: 0 } once => $"once at {once.StartTimeUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC{skips}",
            ISimpleTrigger every => $"every {(long)every.RepeatInterval.TotalSeconds}s from {every.StartTimeUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" +
                (every.RepeatCount > 0 ? $", {every.RepeatCount + 1} runs" : "") + skips,
            _ => trigger.Key.ToString(),
        };
    }

    internal static bool IsSensitive(string key, HashSet<string> secretKeys, CronlightOptions options) =>
        secretKeys.Contains(key)
        || options.SensitiveKeyPatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A job data value as the API and audit log show it: sensitive values are masked, except secret
    /// references, which name a secret without revealing it and are shown so people can see which one is used.
    /// </summary>
    internal static string? Display(string key, string? value, HashSet<string> secretKeys, CronlightOptions options) =>
        value == null || !IsSensitive(key, secretKeys, options) ? value
        : SecretReferences.Enabled(options) && SecretReferences.IsReference(value) ? value
        : Mask;

    /// <summary>Refuses secret references that would fail when the job runs, so the mistake shows when saving.</summary>
    private static string? CheckSecretReferences(HttpContext http, Dictionary<string, string> data, HashSet<string> secretKeys, CronlightOptions options) =>
        SecretReferences.Check(data, key => IsSensitive(key, secretKeys, options), options,
            http.RequestServices.GetRequiredService<IConfiguration>());

    private static bool TryResolveTimeZone(string? id, out TimeZoneInfo tz)
    {
        tz = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(id))
            return true;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }
}
