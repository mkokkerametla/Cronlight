using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.Matchers;
using YamlDotNet.Core;

namespace Cronlight;

/// <summary>A file of jobs, as api/jobs/export writes it and api/jobs/import reads it, in JSON or YAML.</summary>
/// <param name="Version">The file format's version: 1.</param>
/// <param name="Scheduler">The scheduler the jobs were exported from. Import ignores it, so jobs can move between schedulers.</param>
internal record JobFile(int Version, string? Scheduler, DateTimeOffset? ExportedAtUtc, List<JobFileEntry>? Jobs);

/// <param name="Group">DEFAULT when left out.</param>
/// <param name="Type">The job's class, by full name, as api/job-types lists it.</param>
/// <param name="Paused">
/// Every trigger of the job is paused. Import pauses the jobs it creates, and leaves those it updates paused or not as
/// they are.
/// </param>
/// <param name="Retry">
/// Left out, the job follows its type's [CronlightRetry], if any; MaxRetries 0 turns retries off.
/// </param>
/// <param name="Data">
/// Job data by key. Secrets are exported masked (********), except secret references. Importing a masked value keeps the
/// value the job has, and is refused for a job the import creates.
/// </param>
/// <param name="Triggers">Its cron, one-time and interval triggers. Waiting retries and other kinds are not exported.</param>
internal record JobFileEntry(string? Group, string? Name, string? Type, string? Description, bool? Paused, JobRetryPolicy? Retry,
    Dictionary<string, JsonNode?>? Data, List<JobFileTrigger>? Triggers);

/// <summary>A trigger, as <see cref="TriggerRequest"/> describes it.</summary>
internal record JobFileTrigger(string? Name, string? Kind, string? CronExpression, string? TimeZone, DateTimeOffset? StartAtUtc,
    long? IntervalSeconds, int? RepeatCount, bool? SkipMissed)
{
    public TriggerRequest ToRequest() =>
        new(Name, CronExpression, TimeZone, SkipMissed == true, Kind, StartAtUtc, IntervalSeconds, RepeatCount);
}

/// <summary>What an import does, or would do, to one job.</summary>
/// <param name="Action">create, update, unchanged, or error.</param>
/// <param name="Changes">What it changes, by field, as the audit log records it: secrets masked.</param>
internal record ImportedJobDto(string Group, string Name, string Action, string? Error, IReadOnlyDictionary<string, CronlightAuditChange> Changes);

internal record ImportResultDto(bool DryRun, int Created, int Updated, int Unchanged, IReadOnlyList<ImportedJobDto> Jobs);

/// <summary>
/// Exports jobs to a file and imports them from one, to move jobs between environments or keep them in source control.
/// An import creates the jobs the file has that the scheduler does not, and updates the others to match the file; it
/// never deletes a job. It checks every job before changing any, so a file with a mistake changes nothing.
/// </summary>
internal static class JobTransfer
{
    public const string YamlContentType = "application/yaml";
    private const int MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxJobs = 10_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A misspelt property ("cron" for "cronExpression") would otherwise be dropped without a word
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <param name="group">Only this group's jobs; every job when left out.</param>
    /// <param name="format">json or yaml; when left out, YAML if the Accept header asks for it, else JSON.</param>
    public static async Task<IResult> Export(string? group, string? format, HttpContext http, IOptions<CronlightOptions> options)
    {
        bool yaml;
        if (string.IsNullOrEmpty(format))
            yaml = http.Request.Headers.Accept.ToString().Contains("yaml", StringComparison.OrdinalIgnoreCase);
        else if (format.Equals("json", StringComparison.OrdinalIgnoreCase) || format.Equals("yaml", StringComparison.OrdinalIgnoreCase))
            yaml = format.Equals("yaml", StringComparison.OrdinalIgnoreCase);
        else
            return DashboardApi.Invalid($"Unknown format '{format}'; expected json or yaml");

        var scheduler = await DashboardApi.Scheduler(http);
        var keys = await scheduler.GetJobKeys(group == null ? GroupMatcher<JobKey>.AnyGroup() : GroupMatcher<JobKey>.GroupEquals(group));
        var jobs = new List<JobFileEntry>();
        var notExported = 0;
        foreach (var key in keys.OrderBy(k => k.Group).ThenBy(k => k.Name))
        {
            IJobDetail? detail;
            try
            {
                detail = await scheduler.GetJobDetail(key);
            }
            // Its class cannot be loaded here: it belongs to another app sharing the database
            catch (JobPersistenceException)
            {
                notExported++;
                continue;
            }
            if (detail != null)
                jobs.Add(await Entry(scheduler, detail, options.Value));
        }

        var now = DateTimeOffset.UtcNow;
        var file = new JobFile(1, scheduler.SchedulerName, now, jobs);
        // In a header rather than in the file, which version 1 readers would refuse: the dashboard warns that the download
        // is not every job
        http.Response.Headers[NotExportedHeader] = notExported.ToString(CultureInfo.InvariantCulture);
        var name = $"cronlight-{new string(scheduler.SchedulerName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray())}-{now:yyyyMMdd-HHmmss}";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}.{(yaml ? "yaml" : "json")}\"";
        return yaml
            ? Results.Text(JobFileYaml.Write(JsonSerializer.SerializeToNode(file, Json)), $"{YamlContentType}; charset=utf-8")
            : Results.Text(JsonSerializer.Serialize(file, Json), "application/json; charset=utf-8");
    }

    /// <summary>How many jobs an export left out, because their class cannot be loaded here.</summary>
    public const string NotExportedHeader = "X-Cronlight-Not-Exported";

    /// <summary>The job as a file has it.</summary>
    private static async Task<JobFileEntry> Entry(IScheduler scheduler, IJobDetail detail, CronlightOptions options)
    {
        var key = detail.Key;
        var secretKeys = DashboardApi.SecretKeys(detail.JobType);
        var data = detail.JobDataMap
            .Where(kv => !Retries.IsReserved(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => (JsonNode?)DashboardApi.Display(kv.Key, DashboardApi.DataText(kv.Value), secretKeys, options,
                scheduler.SchedulerName));

        var triggers = new List<JobFileTrigger>();
        var paused = 0;
        foreach (var trigger in await scheduler.GetTriggersOfJob(key))
        {
            var kind = DashboardApi.Kind(trigger);
            if (!DashboardApi.EditableKinds.Contains(kind))
                continue;
            if (await scheduler.GetTriggerState(trigger.Key) == TriggerState.Paused)
                paused++;
            var skips = Misfires.SkipsMissed(trigger) ? true : (bool?)null;
            triggers.Add(trigger switch
            {
                ICronTrigger cron => new(trigger.Key.Name, kind, cron.CronExpressionString, cron.TimeZone.Id, null, null, null, skips),
                ISimpleTrigger simple => new(trigger.Key.Name, kind, null, null, simple.StartTimeUtc,
                    kind == "Interval" ? (long)simple.RepeatInterval.TotalSeconds : null, simple.RepeatCount > 0 ? simple.RepeatCount : null, skips),
                _ => throw new InvalidOperationException($"Unexpected trigger {trigger.Key}"),
            });
        }

        // The policy it runs with; one turned off only shows against its type's
        var retry = Retries.For(detail) ?? (Retries.ForType(detail.JobType) != null ? new JobRetryPolicy(0, 0) : null);
        return new JobFileEntry(key.Group, key.Name, detail.JobType.FullName, string.IsNullOrEmpty(detail.Description) ? null : detail.Description,
            triggers.Count > 0 && paused == triggers.Count ? true : null, retry, data, triggers.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
    }

    /// <param name="dryRun">Check the file and answer with what importing it would do, changing nothing.</param>
    public static async Task<IResult> Import(bool? dryRun, HttpContext http, JobTypeCatalog catalog, IOptions<CronlightOptions> options)
    {
        var (file, invalid) = await Read(http.Request);
        if (file == null)
            return DashboardApi.Invalid(invalid!);
        if (file.Version != 1)
            return DashboardApi.Invalid($"The file is version {file.Version}; this Cronlight reads version 1 files");
        if (file.Jobs == null)
            return DashboardApi.Invalid("The file has no jobs list");
        if (file.Jobs.Count > MaxJobs)
            return DashboardApi.Invalid($"The file has {file.Jobs.Count} jobs; at most {MaxJobs} can be imported at once");

        var scheduler = await DashboardApi.Scheduler(http);
        var results = new List<ImportedJobDto>();
        var planned = new List<(JobChange Change, bool Create)>();
        var seen = new HashSet<JobKey>();
        var triggerOwners = new Dictionary<TriggerKey, JobKey>();
        foreach (var entry in file.Jobs)
        {
            var group = string.IsNullOrWhiteSpace(entry?.Group) ? SchedulerConstants.DefaultGroup : entry.Group;
            var name = entry?.Name ?? "";
            ImportedJobDto Error(string error) => new(group, name, "error", error, new Dictionary<string, CronlightAuditChange>());

            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
            {
                results.Add(Error("A job name is required"));
                continue;
            }
            var key = new JobKey(name, group);
            if (!seen.Add(key))
            {
                results.Add(Error($"Job {key} is in the file more than once"));
                continue;
            }
            if (!TryData(entry.Data, out var data, out var badData))
            {
                results.Add(Error(badData));
                continue;
            }

            var (change, create, error) = await Plan(scheduler, key, entry, data, http, catalog, options.Value);
            if (change == null)
            {
                results.Add(Error(error!));
                continue;
            }
            // Each job's triggers are checked against the scheduler, not against the file's other jobs
            if (change.Triggers.FirstOrDefault(t => triggerOwners.TryGetValue(t, out var owner) && !owner.Equals(key)) is { } taken)
            {
                results.Add(Error($"Trigger name '{taken.Name}' in group '{taken.Group}' is also used by job {triggerOwners[taken]} in the file"));
                continue;
            }
            foreach (var trigger in change.Triggers)
                triggerOwners[trigger] = key;

            var unchanged = !create && change.Changes.Count == 0 && await SameTriggers(scheduler, key, change.Triggers);
            results.Add(new ImportedJobDto(group, name, create ? "create" : unchanged ? "unchanged" : "update", null, change.Changes));
            if (!unchanged)
                planned.Add((change, create));
        }

        var failed = results.Where(r => r.Action == "error").ToList();
        if (failed.Count > 0)
            return Results.Problem(
                title: "Invalid request.",
                detail: $"{failed.Count} of {results.Count} jobs cannot be imported, so none was. The first: {failed[0].Group}.{failed[0].Name}: {failed[0].Error}",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["jobs"] = results });

        var result = new ImportResultDto(dryRun == true, results.Count(r => r.Action == "create"), results.Count(r => r.Action == "update"),
            results.Count(r => r.Action == "unchanged"), results);
        if (dryRun == true)
        {
            // What importing would do, as the import's own event records what it did
            var preview = new Dictionary<string, CronlightAuditChange>();
            foreach (var (change, create) in planned)
            {
                preview[change.Key.ToString()] = new(null, create ? "to create" : "to update");
                foreach (var (field, value) in change.Changes)
                    preview[$"{change.Key}: {field}"] = value;
            }
            Audit.RecordChanges(http, preview);
            return Results.Ok(result);
        }

        // Filled in as each job is carried out, so if the scheduler fails partway the audit event lists what was done
        var audited = new Dictionary<string, CronlightAuditChange>();
        Audit.RecordChanges(http, audited);
        foreach (var (change, create) in planned)
        {
            await change.Apply(scheduler);
            audited[change.Key.ToString()] = new(null, create ? "created" : "updated");
            foreach (var (field, value) in change.Changes)
                audited[$"{change.Key}: {field}"] = value;
        }
        return Results.Ok(result);
    }

    /// <summary>A create when the scheduler does not have the job, else an update that makes it match the file.</summary>
    private static async Task<(JobChange? Change, bool Create, string? Error)> Plan(IScheduler scheduler, JobKey key, JobFileEntry entry,
        Dictionary<string, string> data, HttpContext http, JobTypeCatalog catalog, CronlightOptions options)
    {
        var triggers = (entry.Triggers ?? []).Select(t => t?.ToRequest() ?? new TriggerRequest(null, null, null)).ToList();
        if (!await scheduler.CheckExists(key))
        {
            if (data.FirstOrDefault(kv => kv.Value == DashboardApi.Mask).Key is { } masked)
                return (null, true, $"'{masked}' was exported masked ({DashboardApi.Mask}): set its value, or a secret reference, " +
                                    "for the job to be created");
            var (created, refused) = await DashboardApi.PlanCreate(scheduler,
                new CreateJobRequest(entry.Type, key.Name, key.Group, entry.Description, data, triggers, entry.Retry), http, catalog, options,
                paused: entry.Paused == true);
            return (created, true, Message(refused));
        }

        var (existing, problem) = await DashboardApi.GetManageableJob(scheduler, key, options);
        if (problem != null)
            return (null, false, Message(problem));
        // Its type cannot be loaded here, which only AllowManagingUnloadableJobs lets through; PlanUpdate refuses it
        if (existing != null)
        {
            if (entry.Type != null && entry.Type != existing.JobType.FullName)
                return (null, false, $"Job {key} is a {existing.JobType.FullName}, and the file has it as a {entry.Type}. A job's type " +
                                     "cannot be changed: delete it first, or give it another name");
            if (data.FirstOrDefault(kv => kv.Value == DashboardApi.Mask && !existing.JobDataMap.ContainsKey(kv.Key)).Key is { } masked)
                return (null, false, $"'{masked}' was exported masked ({DashboardApi.Mask}), and job {key} has no value for it to keep: " +
                                     "set its value, or a secret reference");
        }

        // A trigger written without a name is the job's trigger with that schedule, if it has one: importing the file
        // again does not replace it with a new name
        var current = (await scheduler.GetTriggersOfJob(key)).Where(t => DashboardApi.EditableKinds.Contains(DashboardApi.Kind(t))).ToList();
        var named = triggers.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => t.Name!.Trim()).ToHashSet();
        for (var i = 0; i < triggers.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(triggers[i].Name))
                continue;
            if (current.FirstOrDefault(t => !named.Contains(t.Key.Name) && SameSchedule(triggers[i], t)) is { } match)
            {
                triggers[i] = triggers[i] with { Name = match.Key.Name };
                named.Add(match.Key.Name);
            }
        }

        // The data the file has, and the job's other keys removed
        var changes = data.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        foreach (var k in existing?.JobDataMap.Keys.Where(k => !Retries.IsReserved(k) && !data.ContainsKey(k)) ?? [])
            changes[k] = null;
        var retry = entry.Retry ?? (existing == null ? null : Retries.ForType(existing.JobType) ?? new JobRetryPolicy(0, 0));
        // An empty description clears one; a job with none keeps none
        var description = entry.Description ?? (string.IsNullOrEmpty(existing?.Description) ? null : "");
        var (updated, refusedUpdate) = await DashboardApi.PlanUpdate(scheduler, key,
            new UpdateJobRequest(description, changes, triggers, retry, [.. DashboardApi.EditableKinds]), http, catalog, options);
        return (updated, false, Message(refusedUpdate));
    }

    /// <summary>Whether a trigger in the file has the stored trigger's schedule, whatever its name.</summary>
    private static bool SameSchedule(TriggerRequest requested, ITrigger stored) => (requested.Kind ?? "Cron").ToLowerInvariant() switch
    {
        "cron" => stored is ICronTrigger cron && cron.CronExpressionString == requested.CronExpression?.Trim()
            && cron.TimeZone.Id == (string.IsNullOrEmpty(requested.TimeZone) ? TimeZoneInfo.Utc.Id : requested.TimeZone),
        "once" => DashboardApi.Kind(stored) == "Once" && ((ISimpleTrigger)stored).StartTimeUtc == requested.StartAtUtc,
        "interval" => DashboardApi.Kind(stored) == "Interval" && stored is ISimpleTrigger simple
            && (long)simple.RepeatInterval.TotalSeconds == requested.IntervalSeconds
            && (simple.RepeatCount > 0 ? simple.RepeatCount : (int?)null) == requested.RepeatCount,
        _ => false,
    } && Misfires.SkipsMissed(stored) == requested.SkipMissed;

    /// <summary>Whether the job already has these triggers of the kinds import sets, by name: a renamed one is a change.</summary>
    private static async Task<bool> SameTriggers(IScheduler scheduler, JobKey key, IReadOnlyList<TriggerKey> triggers) =>
        (await scheduler.GetTriggersOfJob(key))
            .Where(t => DashboardApi.EditableKinds.Contains(DashboardApi.Kind(t)))
            .Select(t => t.Key)
            .ToHashSet()
            .SetEquals(triggers);

    private static string? Message(IResult? problem) =>
        (problem as ProblemHttpResult)?.ProblemDetails is { } details ? details.Detail ?? details.Title : null;

    /// <summary>
    /// Job data as text. A file may give numbers and true or false without quotes, as YAML makes easy; a null value is
    /// the same as leaving the key out.
    /// </summary>
    private static bool TryData(Dictionary<string, JsonNode?>? values, out Dictionary<string, string> data, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        data = new();
        error = null;
        foreach (var (key, node) in values ?? [])
        {
            switch (node?.GetValueKind())
            {
                case null or JsonValueKind.Null:
                    break;
                case JsonValueKind.String:
                    data[key] = node.GetValue<string>();
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    data[key] = node.ToJsonString();
                    break;
                default:
                    error = $"Job data '{key}' is a list or an object; job data values are text";
                    return false;
            }
        }
        return true;
    }

    /// <summary>The file in the request's body: YAML when its content type says so, else JSON.</summary>
    private static async Task<(JobFile? File, string? Error)> Read(HttpRequest request)
    {
        if (request.ContentLength > MaxFileBytes)
            return (null, $"The file is larger than {MaxFileBytes / 1024 / 1024} MB");
        // Read in pieces, so a body sent without a length cannot grow past the limit either
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = new StringBuilder();
        var buffer = new char[16 * 1024];
        while (await reader.ReadAsync(buffer, request.HttpContext.RequestAborted) is var read and > 0)
        {
            body.Append(buffer, 0, read);
            if (body.Length > MaxFileBytes)
                return (null, $"The file is larger than {MaxFileBytes / 1024 / 1024} MB");
        }
        var text = body.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return (null, "The request has no file: send the exported JSON or YAML as its body");

        var yaml = request.ContentType?.Contains("yaml", StringComparison.OrdinalIgnoreCase) == true;
        try
        {
            var file = yaml ? JobFileYaml.Read(text).Deserialize<JobFile>(Json) : JsonSerializer.Deserialize<JobFile>(text, Json);
            return file == null ? (null, "The file is empty") : (file, null);
        }
        // Not JSON, but perhaps YAML in flow style, which starts with a brace too: read as YAML if it is, and otherwise
        // answer what is wrong with it as JSON, which is likelier what it was meant to be
        catch (JsonException ex) when (!yaml && IsSyntaxError(ex) && ReadYaml(text) is { } file)
        {
            return (file, null);
        }
        catch (JsonException ex)
        {
            // Its message names Cronlight's own classes; the path says where in the file the problem is
            var at = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "" : $" at {ex.Path}";
            var what = ex.Message.Contains("could not be mapped", StringComparison.Ordinal) ? "has a property Cronlight does not know"
                : ex.Message.Contains("could not be converted", StringComparison.Ordinal) ? "has a value of the wrong kind"
                : yaml ? "is not a jobs file"
                : $"is not valid JSON{(ex.LineNumber is { } line ? $" (line {line + 1})" : "")}";
            return (null, $"The file {what}{at}");
        }
        catch (YamlException ex)
        {
            return (null, $"The file is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}");
        }
    }

    private static bool IsSyntaxError(JsonException ex) =>
        !ex.Message.Contains("could not be mapped", StringComparison.Ordinal) && !ex.Message.Contains("could not be converted", StringComparison.Ordinal);

    private static JobFile? ReadYaml(string text)
    {
        try
        {
            return JobFileYaml.Read(text).Deserialize<JobFile>(Json);
        }
        catch (Exception ex) when (ex is JsonException or YamlException)
        {
            return null;
        }
    }
}
