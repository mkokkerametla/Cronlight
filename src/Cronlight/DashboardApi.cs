using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
                HistoryEnabled = http.RequestServices.GetService<IJobHistoryStore>() != null
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
        api.MapPost("/jobs", async (CreateJobRequest request, ISchedulerFactory factory, JobTypeCatalog catalog) =>
        {
            if (!catalog.TryGet(request.Type, out var type, out var info))
                return Results.BadRequest($"Unknown job type '{request.Type}'");

            var data = request.Data ?? new();
            foreach (var p in info.Parameters.Where(p => p.DefaultValue != null && !data.ContainsKey(p.Key)))
                data[p.Key] = p.DefaultValue!;

            if (ValidateParameters(info, data) is { } invalid)
                return Results.BadRequest(invalid);

            var key = new JobKey(request.Name, string.IsNullOrWhiteSpace(request.Group) ? "DEFAULT" : request.Group);
            if (!TryBuildTriggers(key, request.Triggers ?? [], out var triggers, out var error))
                return Results.BadRequest(error);

            var scheduler = await factory.GetScheduler();
            if (await scheduler.CheckExists(key))
                return Results.Conflict($"Job {key} already exists");

            var job = JobBuilder.Create(type)
                .WithIdentity(key)
                .WithDescription(request.Description)
                .SetJobData(ToJobDataMap(data))
                .StoreDurably()
                .Build();

            await scheduler.AddJob(job, replace: false);
            foreach (var trigger in triggers)
                await scheduler.ScheduleJob(trigger);

            return Results.Created($"jobs/{key.Group}/{key.Name}", new { key.Name, key.Group });
        });

        api.MapPut("/jobs/{group}/{name}", async (string group, string name, UpdateJobRequest request, ISchedulerFactory factory, JobTypeCatalog catalog, IOptions<CronlightOptions> options) =>
        {
            var scheduler = await factory.GetScheduler();
            var key = new JobKey(name, group);
            var (existing, problem) = await GetManageableJob(scheduler, key, options.Value);
            if (problem != null)
                return problem;
            if (existing == null)
                return Results.Conflict($"Job {key} has a type that cannot be loaded here and cannot be edited.");

            // Validate everything before mutating anything
            if (request.Data != null && catalog.Describe(existing.JobType) is { } info && ValidateParameters(info, request.Data) is { } invalid)
                return Results.BadRequest(invalid);

            var requested = new List<ITrigger>();
            if (request.Triggers != null && !TryBuildTriggers(key, request.Triggers, out requested, out var error))
                return Results.BadRequest(error);

            if (request.Data != null || request.Description != null)
            {
                var data = request.Data == null ? existing.JobDataMap : new JobDataMap();
                foreach (var (k, v) in request.Data ?? new())
                {
                    // The UI only ever sees masked secrets; sending the mask back means "unchanged"
                    data[k] = v == Mask && existing.JobDataMap.ContainsKey(k) ? existing.JobDataMap[k] : v;
                }

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
        var current = (await scheduler.GetTriggersOfJob(key)).OfType<ICronTrigger>().ToDictionary(t => t.Key);

        foreach (var stale in current.Keys.Where(k => requested.All(r => !r.Key.Equals(k))))
            await scheduler.UnscheduleJob(stale);

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
    }

    private static bool TryBuildTriggers(JobKey key, List<TriggerRequest> requests, out List<ITrigger> triggers, out string? error)
    {
        triggers = new();
        error = null;
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
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

            var name = string.IsNullOrWhiteSpace(r.Name) ? $"{key.Name}-trigger-{i + 1}" : r.Name.Trim();
            if (triggers.Any(t => t.Key.Name == name))
            {
                error = $"Duplicate trigger name '{name}'";
                return false;
            }

            triggers.Add(TriggerBuilder.Create()
                .WithIdentity(name, key.Group)
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
        var secretKeys = info?.Parameters.Where(p => p.Type == nameof(JobParameterType.Secret)).Select(p => p.Key).ToHashSet()
            ?? new HashSet<string>();

        var data = detail.JobDataMap.ToDictionary(
            kv => kv.Key,
            kv => IsSensitive(kv.Key, secretKeys, options) ? Mask : kv.Value?.ToString()
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

    private static bool IsSensitive(string key, HashSet<string> secretKeys, CronlightOptions options) =>
        secretKeys.Contains(key)
        || options.SensitiveKeyPatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));

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
