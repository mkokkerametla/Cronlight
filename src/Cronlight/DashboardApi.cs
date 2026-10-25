using System.Globalization;
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

namespace Cronlight;

public record TriggerDto(
    string Name,
    string Group,
    string Kind,
    string? CronExpression,
    string? TimeZone,
    string State,
    DateTimeOffset? NextFireTimeUtc,
    DateTimeOffset? PreviousFireTimeUtc
);

public record JobDto(
    string Name,
    string Group,
    string? Description,
    string? JobType,
    string? DisplayName,
    bool Durable,
    bool Loadable,
    string? LoadError,
    IDictionary<string, string?> Data,
    IReadOnlyList<TriggerDto> Triggers
);

public record TriggerRequest(string? Name, string CronExpression, string? TimeZone);

public record CreateJobRequest(
    string Type,
    string Name,
    string? Group,
    string? Description,
    Dictionary<string, string>? Data,
    List<TriggerRequest>? Triggers
);

/// <summary>Null properties are left unchanged. Triggers replaces the job's cron triggers only.</summary>
public record UpdateJobRequest(string? Description, Dictionary<string, string>? Data, List<TriggerRequest>? Triggers);

public record CronPreviewDto(bool IsValid, string Expression, string? Description, IReadOnlyList<DateTimeOffset> NextFireTimes, string? ErrorMessage);

internal static class DashboardApi
{
    public const string Mask = "********";

    /// <summary>
    /// Header the dashboard sends with every request. Requests that change jobs must carry it: another
    /// site cannot add a custom header to a cross-site request without the host allowing it through
    /// CORS, so a page elsewhere cannot use the viewer's cookies to pause, run or delete jobs.
    /// </summary>
    public const string RequestHeader = "X-Cronlight-Request";

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
            catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                logger.LogError(ex, "Cronlight request {Method} {Path} failed",
                    context.HttpContext.Request.Method, context.HttpContext.Request.Path);

                // Scheduler messages are useful to operators ("couldn't retrieve job ...") and contain no internals;
                // anything else gets a generic message and a trace id to correlate with logs.
                return Results.Problem(
                    title: "The scheduler operation failed.",
                    detail: ex is SchedulerException ? ex.Message : null,
                    statusCode: StatusCodes.Status500InternalServerError,
                    extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier }
                );
            }
        });
    }

    public static void MapReadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/scheduler", async (HttpContext http, ISchedulerFactory factory, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await factory.GetScheduler();
            var meta = await scheduler.GetMetaData();
            return Results.Ok(new
            {
                meta.SchedulerName,
                meta.SchedulerInstanceId,
                meta.Version,
                meta.Started,
                meta.InStandbyMode,
                JobStore = meta.JobStoreType.Name,
                meta.JobStoreClustered,
                meta.JobStoreSupportsPersistence,
                meta.ThreadPoolSize,
                meta.NumberOfJobsExecuted,
                meta.RunningSince,
                // Lets the UI hide controls the user cannot use; the server still enforces it
                CanManage = await CanManage(http, options.Value),
                HistoryEnabled = http.RequestServices.GetService<IJobHistoryStore>() != null,
                options.Value.ShowScheduleAssistant,
                SecretReferences = SecretReferences.Enabled(options.Value)
            });
        });

        api.MapGet("/job-types", (JobTypeCatalog catalog) =>
            Results.Ok(catalog.All.Where(t => !t.Hidden).OrderBy(t => t.DisplayName)));

        api.MapGet("/jobs", async (ISchedulerFactory factory, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await factory.GetScheduler();
            var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
            var jobs = new List<JobDto>();
            foreach (var key in keys.OrderBy(k => k.Group).ThenBy(k => k.Name))
                jobs.Add(await LoadJob(scheduler, key, catalog, options.Value));
            return Results.Ok(jobs);
        });

        api.MapGet("/jobs/{group}/{name}", async (string group, string name, ISchedulerFactory factory, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await factory.GetScheduler();
            var key = new JobKey(name, group);
            return await scheduler.CheckExists(key)
                ? Results.Ok(await LoadJob(scheduler, key, catalog, options.Value))
                : Results.NotFound();
        });

        api.MapGet("/running", async (ISchedulerFactory factory, RunningJobsReader reader, CancellationToken ct) =>
            Results.Ok(await reader.Read(await factory.GetScheduler(ct), ct)));

        // History is optional: these return 404 unless the Cronlight.History package is registered
        api.MapGet("/history", async (HttpContext http, ISchedulerFactory factory, string? group, string? name, string? status, long? before, int? limit, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IJobHistoryStore>() is not { } store)
                return Results.NotFound("Execution history is not enabled.");

            JobExecutionStatus? statusFilter = null;
            if (status != null)
            {
                if (!Enum.TryParse<JobExecutionStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest($"Unknown status '{status}'");
                statusFilter = parsed;
            }

            var scheduler = await factory.GetScheduler(ct);
            var pageSize = Math.Clamp(limit ?? 50, 1, 500);
            var records = await store.Query(scheduler.SchedulerName, new JobHistoryQuery
            {
                JobGroup = group,
                JobName = name,
                Status = statusFilter,
                BeforeId = before,
                Limit = pageSize
            }, ct);

            return Results.Ok(new
            {
                Records = records,
                NextBefore = records.Count == pageSize ? records[^1].Id : (long?)null
            });
        });

        api.MapGet("/history/summary", async (HttpContext http, ISchedulerFactory factory, int? hours, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IJobHistoryStore>() is not { } store)
                return Results.NotFound("Execution history is not enabled.");

            var scheduler = await factory.GetScheduler(ct);
            var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 90));
            return Results.Ok(await store.Summarize(scheduler.SchedulerName, since, ct));
        });

        api.MapGet("/cron/preview", (string expression, string? timeZone, int? count) =>
        {
            if (!TryResolveTimeZone(timeZone, out var tz))
                return Results.Ok(new CronPreviewDto(false, expression, null, [], $"Unknown time zone '{timeZone}'"));
            try
            {
                var cron = new CronExpression(expression) { TimeZone = tz };
                var times = new List<DateTimeOffset>();
                DateTimeOffset? next = DateTimeOffset.UtcNow;
                for (var i = 0; i < Math.Clamp(count ?? 5, 1, 10) && next != null; i++)
                {
                    next = cron.GetNextValidTimeAfter(next.Value);
                    if (next != null)
                        times.Add(next.Value);
                }
                // Quartz has no human-readable summary (GetExpressionSummary is a debug dump), so leave it to the UI
                return Results.Ok(new CronPreviewDto(true, expression, null, times, null));
            }
            catch (FormatException ex)
            {
                return Results.Ok(new CronPreviewDto(false, expression, null, [], ex.Message));
            }
        });
    }

    public static void MapManageEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs", async (CreateJobRequest request, HttpContext http, ISchedulerFactory factory, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            if (!catalog.TryGet(request.Type, out var type, out var info))
                return Results.BadRequest($"Unknown job type '{request.Type}'");

            var data = request.Data ?? new();
            foreach (var p in info.Parameters.Where(p => p.DefaultValue != null && !data.ContainsKey(p.Key)))
                data[p.Key] = p.DefaultValue!;

            if (ValidateParameters(info, data) is { } invalid)
                return Results.BadRequest(invalid);
            var secretKeys = SecretKeys(info);
            if (CheckSecretReferences(http, data, secretKeys, options.Value) is { } unresolved)
                return Results.BadRequest(unresolved);

            var key = new JobKey(request.Name, string.IsNullOrWhiteSpace(request.Group) ? "DEFAULT" : request.Group);
            if (!TryBuildTriggers(key, request.Triggers ?? [], [], out var triggers, out var error))
                return Results.BadRequest(error);

            var scheduler = await factory.GetScheduler();
            if (await scheduler.CheckExists(key))
                return Results.Conflict($"Job {key} already exists");
            if (await FindTriggerConflict(scheduler, triggers, new HashSet<TriggerKey>()) is { } conflict)
                return Results.Conflict(conflict);

            var job = JobBuilder.Create(type)
                .WithIdentity(key)
                .WithDescription(request.Description)
                .SetJobData(ToJobDataMap(data))
                .StoreDurably()
                .Build();

            var changes = DataChanges(new Dictionary<string, object>(), job.JobDataMap, secretKeys, options.Value);
            AddChange(changes, "description", null, request.Description);
            AddChange(changes, "triggers", null, DescribeTriggers(triggers));
            Audit.RecordChanges(http, changes);

            // One call stores the job and its triggers together, so a failure leaves nothing behind
            await scheduler.ScheduleJob(job, triggers, replace: false);

            return Results.Created($"jobs/{key.Group}/{key.Name}", new { key.Name, key.Group });
        });

        api.MapPut("/jobs/{group}/{name}", async (string group, string name, UpdateJobRequest request, HttpContext http, ISchedulerFactory factory, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await factory.GetScheduler();
            var key = new JobKey(name, group);
            var (existing, problem) = await GetManageableJob(scheduler, key, options.Value);
            if (problem != null)
                return problem;
            if (existing == null)
                return Results.Conflict($"Job {key} has a type that cannot be loaded here and cannot be edited.");

            // Validate everything before mutating anything
            var info = catalog.Describe(existing.JobType);
            var secretKeys = SecretKeys(info);
            if (request.Data != null && info != null && ValidateParameters(info, request.Data) is { } invalid)
                return Results.BadRequest(invalid);
            if (request.Data != null && CheckSecretReferences(http, request.Data, secretKeys, options.Value) is { } unresolved)
                return Results.BadRequest(unresolved);

            var requested = new List<ITrigger>();
            IReadOnlyCollection<ITrigger> current = [];
            if (request.Triggers != null)
            {
                current = await scheduler.GetTriggersOfJob(key);
                if (!TryBuildTriggers(key, request.Triggers, current.Select(t => t.Key).ToList(), out requested, out var error))
                    return Results.BadRequest(error);
                var ownCron = current.OfType<ICronTrigger>().Select(t => t.Key).ToHashSet();
                if (await FindTriggerConflict(scheduler, requested, ownCron) is { } conflict)
                    return Results.Conflict(conflict);
            }

            var data = request.Data == null ? existing.JobDataMap : MergeJobData(existing.JobDataMap, request.Data);
            var changes = DataChanges(existing.JobDataMap, data, secretKeys, options.Value);
            if (request.Description != null)
                AddChange(changes, "description", existing.Description, request.Description);
            if (request.Triggers != null)
                AddChange(changes, "triggers", DescribeTriggers(current), DescribeTriggers(requested));
            Audit.RecordChanges(http, changes);

            if (request.Data != null || request.Description != null)
            {

                var updated = existing.GetJobBuilder()
                    .WithDescription(request.Description ?? existing.Description)
                    .SetJobData(data)
                    .Build();
                await scheduler.AddJob(updated, replace: true, storeNonDurableWhileAwaitingScheduling: true);
            }

            if (request.Triggers != null)
                await ReplaceCronTriggers(scheduler, key, requested);

            return Results.NoContent();
        });

        api.MapPost("/jobs/{group}/{name}/pause", (string group, string name, ISchedulerFactory f, IOptions<CronlightOptions> o) =>
            OnJob(f, o.Value, group, name, (s, k) => s.PauseJob(k)));
        api.MapPost("/jobs/{group}/{name}/resume", (string group, string name, ISchedulerFactory f, IOptions<CronlightOptions> o) =>
            OnJob(f, o.Value, group, name, (s, k) => s.ResumeJob(k)));
        api.MapPost("/jobs/{group}/{name}/trigger", (string group, string name, ISchedulerFactory f, IOptions<CronlightOptions> o) =>
            OnJob(f, o.Value, group, name, (s, k) => s.TriggerJob(k)));
        api.MapDelete("/jobs/{group}/{name}", (string group, string name, ISchedulerFactory f, IOptions<CronlightOptions> o) =>
            OnJob(f, o.Value, group, name, (s, k) => s.DeleteJob(k)));

        api.MapPost("/jobs/{group}/{name}/runs/{fireInstanceId}/cancel", CancelRun);
    }

    /// <summary>
    /// Asks a running job to stop by cancelling its <see cref="IJobExecutionContext.CancellationToken"/>.
    /// Quartz can only interrupt runs on its own node, so a run on another cluster node is refused.
    /// </summary>
    private static async Task<IResult> CancelRun(string group, string name, string fireInstanceId, HttpContext http,
        ISchedulerFactory factory, RunningJobsReader reader, IOptions<CronlightOptions> options, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        var key = new JobKey(name, group);
        var (_, problem) = await GetManageableJob(scheduler, key, options.Value);
        if (problem != null)
            return problem;

        var local = (await scheduler.GetCurrentlyExecutingJobs(ct))
            .FirstOrDefault(c => c.FireInstanceId == fireInstanceId && c.JobDetail.Key.Equals(key));
        if (local != null)
        {
            if (local.CancellationToken.IsCancellationRequested)
                return Results.Accepted(value: new { Message = "Cancellation was already requested." });
            local.Put(CronlightContextKeys.CancelledBy, Audit.UserName(http.User));
            await scheduler.Interrupt(fireInstanceId, ct);
            return Results.Accepted(value: new { Message = "Cancellation requested. The job stops when it next checks its cancellation token." });
        }

        var elsewhere = (await reader.Read(scheduler, ct)).Jobs
            .FirstOrDefault(j => j.FireInstanceId == fireInstanceId && j.Group == group && j.Name == name);
        return elsewhere != null
            ? Results.Problem(
                title: "This run is on another node.",
                detail: $"It runs on {elsewhere.InstanceName}. Only the node running a job can cancel it; open the dashboard there.",
                statusCode: StatusCodes.Status409Conflict)
            : Results.Problem(
                title: "This run is not running.",
                detail: "It has already finished, or was never started.",
                statusCode: StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> OnJob(ISchedulerFactory factory, CronlightOptions options, string group, string name, Func<IScheduler, JobKey, Task> action)
    {
        var scheduler = await factory.GetScheduler();
        var key = new JobKey(name, group);
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
    private static async Task<(IJobDetail? Detail, IResult? Problem)> GetManageableJob(IScheduler scheduler, JobKey key, CronlightOptions options)
    {
        if (!await scheduler.CheckExists(key))
            return (null, Results.NotFound());
        try
        {
            return (await scheduler.GetJobDetail(key), null);
        }
        catch (JobPersistenceException ex)
        {
            if (options.AllowManagingUnloadableJobs)
                return (null, null);
            return (null, Results.Problem(
                title: "Job is read-only in this dashboard.",
                detail: $"The type of job {key} cannot be loaded in this application ({ex.InnerException?.Message ?? ex.Message}). " +
                        "It probably belongs to another application sharing this scheduler.",
                statusCode: StatusCodes.Status409Conflict));
        }
    }

    private static async Task ReplaceCronTriggers(IScheduler scheduler, JobKey key, List<ITrigger> requested)
    {
        var all = await scheduler.GetTriggersOfJob(key);
        var current = all.OfType<ICronTrigger>().ToDictionary(t => t.Key);
        var stale = current.Keys.Where(k => requested.All(r => !r.Key.Equals(k))).ToList();

        // Quartz deletes a non-durable job (the default for jobs the host registers) together with its
        // last trigger. When the edit leaves no triggers, make the job durable so it stays for manual runs.
        if (requested.Count == 0 && stale.Count == all.Count && await scheduler.GetJobDetail(key) is { Durable: false } job)
            await scheduler.AddJob(job.GetJobBuilder().StoreDurably().Build(), replace: true);

        // Add and update first, remove last: the job keeps a trigger throughout
        foreach (var trigger in requested.Cast<ICronTrigger>())
        {
            if (!current.TryGetValue(trigger.Key, out var old))
            {
                await scheduler.ScheduleJob(trigger);
                continue;
            }
            if (old.CronExpressionString == trigger.CronExpressionString && old.TimeZone.Id == trigger.TimeZone.Id)
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
    /// The edit form sends back every value as the text it was shown (ToString(), secrets masked).
    /// Values that come back unchanged keep their original object, and changed values keep the
    /// original primitive type when they convert to it, so non-string job data survives an edit.
    /// </summary>
    private static JobDataMap MergeJobData(JobDataMap existing, Dictionary<string, string> requested)
    {
        var data = new JobDataMap();
        foreach (var (k, v) in requested)
        {
            var known = existing.TryGetValue(k, out var old);
            if (known && (v == Mask || (old is not (null or string) && v == old.ToString())))
                data[k] = old;
            else if (known && old is not (null or string))
                data[k] = TryConvert(v, old.GetType()) ?? v;
            else
                data[k] = v;
        }
        return data;
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
    private static bool TryBuildTriggers(JobKey key, List<TriggerRequest> requests, IReadOnlyCollection<TriggerKey> existing,
        out List<ITrigger> triggers, out string? error)
    {
        triggers = new();
        error = null;
        var taken = requests.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => r.Name!.Trim())
            .Concat(existing.Select(k => k.Name))
            .ToHashSet();
        var next = 1;
        foreach (var r in requests)
        {
            if (string.IsNullOrWhiteSpace(r.CronExpression) || !CronExpression.IsValidExpression(r.CronExpression))
            {
                error = $"Invalid cron expression '{r.CronExpression}'";
                return false;
            }
            if (!TryResolveTimeZone(r.TimeZone, out var tz))
            {
                error = $"Unknown time zone '{r.TimeZone}'";
                return false;
            }

            if (new CronExpression(r.CronExpression.Trim()) { TimeZone = tz }.GetNextValidTimeAfter(DateTimeOffset.UtcNow) == null)
            {
                error = $"Cron expression '{r.CronExpression}' never fires again";
                return false;
            }

            string name;
            if (!string.IsNullOrWhiteSpace(r.Name))
                name = r.Name.Trim();
            else
            {
                do name = $"{key.Name}-trigger-{next++}";
                while (!taken.Add(name));
            }
            if (triggers.Any(t => t.Key.Name == name))
            {
                error = $"Duplicate trigger name '{name}'";
                return false;
            }

            var group = existing.FirstOrDefault(k => k.Name == name)?.Group ?? key.Group;
            triggers.Add(TriggerBuilder.Create()
                .WithIdentity(name, group)
                .ForJob(key)
                .WithCronSchedule(r.CronExpression.Trim(), c => c.InTimeZone(tz))
                .Build());
        }
        return true;
    }

    private static async Task<JobDto> LoadJob(IScheduler scheduler, JobKey key, JobTypeCatalog catalog, CronlightOptions options)
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

        var info = catalog.Describe(detail!.JobType);
        var secretKeys = SecretKeys(info);

        var data = detail.JobDataMap.ToDictionary(
            kv => kv.Key,
            kv => Display(kv.Key, kv.Value?.ToString(), secretKeys, options)
        );

        return new JobDto(key.Name, key.Group, detail.Description, detail.JobType.FullName, info?.DisplayName,
            detail.Durable, true, null, data, await LoadTriggers(scheduler, key));
    }

    private static async Task<IReadOnlyList<TriggerDto>> LoadTriggers(IScheduler scheduler, JobKey key)
    {
        var result = new List<TriggerDto>();
        foreach (var trigger in await scheduler.GetTriggersOfJob(key))
        {
            var state = await scheduler.GetTriggerState(trigger.Key);
            var cron = trigger as ICronTrigger;
            result.Add(new TriggerDto(
                trigger.Key.Name,
                trigger.Key.Group,
                cron != null ? "Cron" : trigger is ISimpleTrigger ? "Simple" : trigger.GetType().Name,
                cron?.CronExpressionString,
                cron?.TimeZone.Id,
                state.ToString(),
                trigger.GetNextFireTimeUtc(),
                trigger.GetPreviousFireTimeUtc()
            ));
        }
        return result;
    }

    private static async Task<bool> CanManage(HttpContext http, CronlightOptions options)
    {
        if (options.ReadOnly)
            return false;
        var policy = options.ManagePolicy ?? options.ViewPolicy;
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

    internal static HashSet<string> SecretKeys(JobTypeInfo? info) =>
        info?.Parameters.Where(p => p.Type == nameof(JobParameterType.Secret)).Select(p => p.Key).ToHashSet() ?? new HashSet<string>();

    /// <summary>Job data that differs between two maps, by "data.&lt;key&gt;", for the audit log. Sensitive values are masked.</summary>
    private static Dictionary<string, CronlightAuditChange> DataChanges(
        IDictionary<string, object> before, IDictionary<string, object> after, HashSet<string> secretKeys, CronlightOptions options)
    {
        var changes = new Dictionary<string, CronlightAuditChange>();
        foreach (var key in before.Keys.Union(after.Keys))
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

    private static string? DescribeTriggers(IEnumerable<ITrigger> triggers) =>
        string.Join("; ", triggers.OfType<ICronTrigger>().Select(t => $"{t.CronExpressionString} ({t.TimeZone.Id})")) is { Length: > 0 } text
            ? text
            : null;

    internal static bool IsSensitive(string key, HashSet<string> secretKeys, CronlightOptions options) =>
        secretKeys.Contains(key)
        || options.SensitiveKeyPatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A job data value as the API and audit log show it: sensitive values are masked, except secret
    /// references, which name a secret without revealing it and are shown so people can see which one is used.
    /// </summary>
    private static string? Display(string key, string? value, HashSet<string> secretKeys, CronlightOptions options) =>
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
