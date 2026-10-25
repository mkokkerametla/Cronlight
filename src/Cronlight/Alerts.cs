using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cronlight;

[JsonConverter(typeof(JsonStringEnumConverter<CronlightAlertKind>))]
public enum CronlightAlertKind
{
    /// <summary>The job has failed <see cref="CronlightAlert.ConsecutiveFailures"/> times in a row.</summary>
    Failing,

    /// <summary>The job succeeded after a failure streak that was alerted.</summary>
    Recovered,

    /// <summary>
    /// A trigger of the job missed its fire time, because the scheduler was down or had no free thread. Sent only
    /// with <see cref="CronlightAlertOptions.NotifyOnMisfire"/>.
    /// </summary>
    Misfired
}

/// <summary>A job started failing, recovered, or missed a fire time.</summary>
/// <param name="ConsecutiveFailures">For Failing, the streak that raised the alert; for Recovered, the streak that ended; 0 for Misfired.</param>
/// <param name="Error">
/// For Failing, the last failure's exception type and message; for Misfired, what was missed and what Quartz does
/// about it; null for Recovered.
/// </param>
/// <param name="DashboardUrl">From <see cref="CronlightAlertOptions.DashboardUrl"/>.</param>
public sealed record CronlightAlert(
    CronlightAlertKind Kind,
    DateTimeOffset TimeUtc,
    string SchedulerName,
    string InstanceName,
    string JobGroup,
    string JobName,
    string? JobType,
    int ConsecutiveFailures,
    string? Error,
    string? DashboardUrl
)
{
    /// <summary>One line describing the alert, as chat messages show it.</summary>
    public string Title => Kind switch
    {
        CronlightAlertKind.Failing => ConsecutiveFailures == 1
            ? $"Job {JobGroup}.{JobName} failed"
            : $"Job {JobGroup}.{JobName} failed {ConsecutiveFailures} times in a row",
        CronlightAlertKind.Recovered => ConsecutiveFailures == 1
            ? $"Job {JobGroup}.{JobName} succeeded again after a failure"
            : $"Job {JobGroup}.{JobName} succeeded again after {ConsecutiveFailures} failures in a row",
        _ => $"Job {JobGroup}.{JobName} missed its fire time",
    };

    /// <summary>What <see cref="Error"/> holds, as messages label it.</summary>
    internal string ErrorLabel => Kind == CronlightAlertKind.Misfired ? "Details" : "Error";
}

/// <summary>
/// Receives every alert, in addition to the webhooks and email in <see cref="CronlightAlertOptions"/>. Register one
/// or more as singletons to send alerts elsewhere, such as a paging service. A sink that throws is
/// logged and does not stop the others.
/// </summary>
public interface ICronlightAlertSink
{
    Task SendAsync(CronlightAlert alert, CancellationToken cancellationToken);
}

public enum CronlightWebhookFormat
{
    /// <summary>The <see cref="CronlightAlert"/> as JSON, for your own endpoints.</summary>
    Json,

    /// <summary>A Slack incoming webhook.</summary>
    Slack,

    /// <summary>A Microsoft Teams webhook (Workflows "when a webhook request is received", or an incoming webhook connector).</summary>
    Teams
}

public class CronlightWebhook
{
    /// <summary>The webhook's URL. Chat webhook URLs work as passwords: keep them in configuration, not code.</summary>
    public string Url { get; set; } = "";

    public CronlightWebhookFormat Format { get; set; } = CronlightWebhookFormat.Json;
}

public class CronlightAlertOptions
{
    /// <summary>Alert when a job fails this many times in a row. One alert per streak.</summary>
    public int AfterConsecutiveFailures { get; set; } = 1;

    /// <summary>Send a Recovered alert when a job that was alerted succeeds again.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>
    /// Send a Misfired alert whenever a trigger misses its fire time by more than Quartz's misfire threshold
    /// (because the scheduler was down or had no free thread). Off by default: a restart after downtime raises one
    /// for every trigger that was due meanwhile.
    /// </summary>
    public bool NotifyOnMisfire { get; set; }

    /// <summary>The dashboard's address, e.g. https://ops.contoso.com/ops/jobs/, linked from alerts.</summary>
    public string? DashboardUrl { get; set; }

    /// <summary>Where to post alerts. A webhook without an absolute http(s) URL is skipped with a warning.</summary>
    public List<CronlightWebhook> Webhooks { get; } = new();

    /// <summary>
    /// Also send alerts by email. Optional: null (the default) sends none, and settings missing a host, sender or
    /// recipient are skipped with a warning instead of stopping the app.
    /// </summary>
    public CronlightEmailAlertOptions? Email { get; set; }

    /// <summary>Which jobs raise alerts. Null (the default) means all.</summary>
    public Func<JobKey, bool>? Jobs { get; set; }
}

/// <summary>A finished run, or a misfire, handed from the job thread to the alert worker.</summary>
/// <param name="Clustered">The scheduler is clustered, so its other nodes run the job too.</param>
/// <param name="Misfired">Not a run but a missed fire time, which leaves the failure streak alone.</param>
internal sealed record AlertRun(
    string SchedulerName,
    string InstanceName,
    JobKey Job,
    string? JobType,
    string FireInstanceId,
    bool Succeeded,
    string? Error,
    bool Clustered = false,
    bool Misfired = false
)
{
    /// <summary>Jobs in different schedulers may share a key, so streaks are kept per scheduler.</summary>
    public (string, JobKey) StreakKey => (SchedulerName, Job);
}

internal sealed class AlertQueue(ILogger<AlertQueue> logger)
{
    private readonly Channel<AlertRun> channel = Channel.CreateBounded<AlertRun>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public ChannelReader<AlertRun> Reader => channel.Reader;

    public void Enqueue(AlertRun run)
    {
        if (!channel.Writer.TryWrite(run))
            logger.LogWarning("Cronlight alert queue is full or closed; the outcome of {Job} was not checked for alerts", run.Job);
    }

    public void Complete() => channel.Writer.TryComplete();
}

/// <summary>Hands each finished run to the alert worker, so the job thread never waits on a webhook.</summary>
internal sealed class AlertListener(AlertQueue queue, IOptions<CronlightAlertOptions> options, IServiceProvider services) : IJobListener
{
    // Registered by AddCronlight; without it, nothing is retried
    private readonly RetryPlanner? retries = services.GetService<RetryPlanner>();

    // Schedulers whose runs spread over several nodes, so a streak is read from history
    private readonly ConcurrentDictionary<string, bool> clustered = new();

    public string Name => "Cronlight.Alerts";

    public void MarkClustered(string schedulerName) => clustered[schedulerName] = true;

    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        var status = RunOutcome.Status(context, jobException);
        // Someone stopped it on purpose: neither a failure nor a recovery. A failure that will be retried is not
        // the outcome yet; the last attempt's is.
        if (status != JobExecutionStatus.Cancelled && retries?.WillRetry(context, status) != true)
            Enqueue(context, status == JobExecutionStatus.Succeeded, RunOutcome.Error(context, jobException));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A veto with a reason (a secret reference that cannot be resolved) means the job is misconfigured and will
    /// not run until someone fixes it, so it counts as a failure. A veto without one is a deliberate skip.
    /// </summary>
    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Get(CronlightContextKeys.VetoReason) is string reason)
            Enqueue(context, succeeded: false, $"Not run: {reason}");
        return Task.CompletedTask;
    }

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private void Enqueue(IJobExecutionContext context, bool succeeded, string? error)
    {
        if (options.Value.Jobs?.Invoke(context.JobDetail.Key) == false)
            return;
        queue.Enqueue(new AlertRun(
            context.Scheduler.SchedulerName,
            context.Scheduler.SchedulerInstanceId,
            context.JobDetail.Key,
            context.JobDetail.JobType.FullName,
            context.FireInstanceId,
            succeeded,
            error,
            clustered.ContainsKey(context.Scheduler.SchedulerName)));
    }

    public void Misfired(Misfire misfire)
    {
        if (options.Value.Jobs?.Invoke(misfire.Job) == false)
            return;
        // The job's type is looked up by the worker: the job store must not be read where misfires are reported
        queue.Enqueue(new AlertRun(misfire.SchedulerName, misfire.InstanceName, misfire.Job, null, "", Succeeded: false,
            misfire.Description, Misfired: true));
    }
}

/// <summary>
/// Attaches <see cref="AlertListener"/>, noting whether the scheduler is clustered, and a misfire listener when
/// misfires raise alerts.
/// </summary>
internal sealed class AlertAttachment(AlertListener listener, IOptions<CronlightAlertOptions> options, ILogger<AlertAttachment> logger)
    : ISchedulerAttachment
{
    public async Task Attach(IScheduler scheduler, CancellationToken cancellationToken)
    {
        if ((await scheduler.GetMetaData(cancellationToken)).JobStoreClustered)
            listener.MarkClustered(scheduler.SchedulerName);
        scheduler.AddJobListenerOnce(listener);
        if (options.Value.NotifyOnMisfire)
            scheduler.AddTriggerListenerOnce(new MisfireListener("Cronlight.Alerts.Misfires", scheduler, listener.Misfired, logger));
    }
}

/// <summary>
/// Tracks each job's failure streak, per scheduler, and sends alerts. A single scheduler counts the runs
/// it sees, which are all of them. In a cluster with execution history the streak is read from history, so it
/// covers every node; without history, each node counts the runs it executed itself.
/// </summary>
internal sealed class AlertWorker(
    AlertQueue queue,
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    IOptions<CronlightAlertOptions> options,
    ILoggerFactory loggerFactory) : IHostedService
{
    public const string HttpClientName = "Cronlight.Alerts";
    private readonly ILogger logger = loggerFactory.CreateLogger("Cronlight.Alerts");
    private const int MaxErrorLength = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Failures in a row per job, as this node saw them; exact unless runs of a job spread over several nodes
    private readonly Dictionary<(string, JobKey), int> streaks = new();
    // Jobs this node sent a Failing alert for, until they recover: history written behind the runs cannot repeat an alert
    private readonly HashSet<(string, JobKey)> alerted = new();
    // Registered by AddCronlightHistory; a clustered scheduler's streaks are read from it
    private IJobHistoryStore? history;
    private List<CronlightWebhook> webhooks = new();
    private CronlightEmailAlertOptions? email;
    private readonly CancellationTokenSource stopping = new();
    private Task? sending;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AfterConsecutiveFailures < 1)
            throw new InvalidOperationException("CronlightAlertOptions.AfterConsecutiveFailures must be at least 1.");
        // Like email below, a webhook that cannot be used is skipped with a warning rather than stopping the app
        webhooks = options.Value.Webhooks.Where((webhook, index) =>
        {
            if (Uri.TryCreate(webhook.Url, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
                return true;
            // The URL may carry the webhook's secret, so it is not logged
            logger.LogWarning("Cronlight alerts will not be sent to webhook {Index} ({Format}): it needs an absolute http(s) URL",
                index, webhook.Format);
            return false;
        }).ToList();
        // Email is optional: incomplete settings (e.g. a template section not filled in yet) turn it off rather than stop the app
        if (options.Value.Email is { } settings && EmailAlerts.Problem(settings) is { } problem)
            logger.LogWarning("Cronlight alerts will not be emailed: {Problem} under Email", problem);
        else
            email = options.Value.Email;
        history = services.GetService<IJobHistoryStore>();
        sending = Task.Run(() => Send(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The scheduler has already stopped (it is registered after us): send what is left, unless the host gives up
        queue.Complete();
        await using var _ = cancellationToken.Register(stopping.Cancel);
        if (sending != null)
            await sending;
    }

    private async Task Send(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var run in queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (await Check(run, cancellationToken) is { } alert)
                        await Deliver(alert, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Checking {Job} for alerts failed", run.Job);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown timed out; alerts still queued are dropped
        }
    }

    /// <summary>The alert this run raises, if any.</summary>
    private async Task<CronlightAlert?> Check(AlertRun run, CancellationToken cancellationToken)
    {
        // Only one node handles each misfire, so each raises exactly one alert
        if (run.Misfired)
        {
            var jobType = services.GetService<CronlightSchedulers>() is { } schedulers
                ? await schedulers.JobType(run.SchedulerName, run.Job, cancellationToken)
                : null;
            return Alert(run with { JobType = jobType }, CronlightAlertKind.Misfired, 0, run.Error);
        }

        var threshold = options.Value.AfterConsecutiveFailures;
        var before = await FailuresBefore(run, threshold, cancellationToken);
        streaks[run.StreakKey] = run.Succeeded ? 0 : before + 1;

        if (!run.Succeeded)
        {
            // Exactly at the threshold: in a cluster, only the node whose run crossed it sends the alert
            if (before + 1 != threshold || !alerted.Add(run.StreakKey))
                return null;
            return Alert(run, CronlightAlertKind.Failing, threshold, Truncate(run.Error));
        }

        var wasAlerted = alerted.Remove(run.StreakKey);
        if ((before >= threshold || wasAlerted) && options.Value.NotifyOnRecovery)
            return Alert(run, CronlightAlertKind.Recovered, Math.Max(before, threshold), null);
        return null;
    }

    /// <summary>
    /// How many times the job failed in a row before this run. In a cluster with history, read from history so
    /// runs on every node count; history is written in the background, so a run that just finished may not be
    /// there yet.
    /// </summary>
    private async Task<int> FailuresBefore(AlertRun run, int threshold, CancellationToken cancellationToken)
    {
        // A single node sees every run, so its own count is exact. Only a cluster needs history, which every node writes.
        if (!run.Clustered || history == null)
            return streaks.GetValueOrDefault(run.StreakKey);

        // Newest first. Some slack beyond the threshold for runs still going, cancelled, retried or skipped, which do not count.
        var records = await history.Query(run.SchedulerName, new JobHistoryQuery
        {
            JobGroup = run.Job.Group,
            JobName = run.Job.Name,
            Limit = threshold + 20
        }, cancellationToken);

        var failures = 0;
        foreach (var record in records.Where(r => r.FireInstanceId != run.FireInstanceId))
        {
            // A vetoed run with a reason counts as a failure, as AlertListener counts it
            if (record.Status == JobExecutionStatus.Failed || record is { Status: JobExecutionStatus.Vetoed, Error: not null })
                failures++;
            else if (record.Status == JobExecutionStatus.Succeeded)
                break;
        }
        return failures;
    }

    private CronlightAlert Alert(AlertRun run, CronlightAlertKind kind, int failures, string? error) =>
        new(kind, DateTimeOffset.UtcNow, run.SchedulerName, run.InstanceName, run.Job.Group, run.Job.Name, run.JobType,
            failures, error, options.Value.DashboardUrl);

    private static string? Truncate(string? text) =>
        text == null || text.Length <= MaxErrorLength ? text : text[..(MaxErrorLength - 1)] + "…";

    private async Task Deliver(CronlightAlert alert, CancellationToken cancellationToken)
    {
        logger.LogWarning("Alert: {AlertTitle}{AlertError}", alert.Title, alert.Error is { } error ? $": {error}" : "");

        foreach (var webhook in webhooks)
        {
            try
            {
                await PostWebhook(webhook, alert, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The URL carries the webhook's secret, so only its host is logged
                logger.LogError(ex, "Sending alert to {Format} webhook at {Host} failed", webhook.Format, new Uri(webhook.Url).Host);
            }
        }

        if (email != null)
        {
            try
            {
                await EmailAlerts.Send(email, alert, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Sending alert by email through {Host}:{Port} failed", email.Host, email.Port);
            }
        }

        foreach (var sink in services.GetServices<ICronlightAlertSink>())
        {
            try
            {
                await sink.SendAsync(alert, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Alert sink {Sink} failed", sink.GetType().Name);
            }
        }
    }

    private async Task PostWebhook(CronlightWebhook webhook, CronlightAlert alert, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        // Retries network errors, timeouts and 5xx a few times, for a webhook that is briefly unavailable
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            HttpResponseMessage? response = null;
            try
            {
                response = await client.PostAsJsonAsync(webhook.Url, WebhookPayload.For(webhook.Format, alert), Json, timeout.Token);
            }
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
            }
            using (response)
            {
                if (response is { IsSuccessStatusCode: true })
                    return;
                if (response != null && ((int)response.StatusCode < 500 || attempt == 3))
                    response.EnsureSuccessStatusCode();
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
        }
    }
}

/// <summary>The body each webhook format expects.</summary>
internal static class WebhookPayload
{
    public static object For(CronlightWebhookFormat format, CronlightAlert alert) => format switch
    {
        CronlightWebhookFormat.Slack => Slack(alert),
        CronlightWebhookFormat.Teams => Teams(alert),
        _ => alert,
    };

    private static object Slack(CronlightAlert alert)
    {
        // Slack reads &, < and > in text as markup
        static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        var lines = new List<string>
        {
            $"{alert.Kind switch { CronlightAlertKind.Failing => ":x:", CronlightAlertKind.Recovered => ":white_check_mark:", _ => ":warning:" }} *{Escape(alert.Title)}*",
            $"Node: {Escape(alert.InstanceName)} · Scheduler: {Escape(alert.SchedulerName)}",
        };
        if (alert.Error != null)
            lines.Add($"```{Escape(alert.Error).Replace("```", "'''")}```");
        if (alert.DashboardUrl != null)
            lines.Add($"<{alert.DashboardUrl}|Open Cronlight>");
        return new { text = string.Join("\n", lines) };
    }

    /// <summary>An Adaptive Card message, which Teams Workflows webhooks and incoming webhook connectors both accept.</summary>
    private static object Teams(CronlightAlert alert)
    {
        var facts = new List<object>
        {
            new { title = "Job", value = $"{alert.JobGroup}.{alert.JobName}" },
            new { title = "Node", value = alert.InstanceName },
            new { title = "Scheduler", value = alert.SchedulerName },
        };
        if (alert.Error != null)
            facts.Add(new { title = alert.ErrorLabel, value = alert.Error });

        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = alert.Title,
                weight = "Bolder",
                size = "Medium",
                wrap = true,
                color = alert.Kind switch { CronlightAlertKind.Failing => "Attention", CronlightAlertKind.Recovered => "Good", _ => "Warning" },
            },
            new { type = "FactSet", facts },
        };
        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (alert.DashboardUrl != null)
            card["actions"] = new[] { new { type = "Action.OpenUrl", title = "Open Cronlight", url = alert.DashboardUrl } };

        return new
        {
            type = "message",
            attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", content = card } },
        };
    }
}
