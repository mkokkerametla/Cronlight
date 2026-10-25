using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.Matchers;

namespace Cronlight;

[JsonConverter(typeof(JsonStringEnumConverter<CronlightAlertKind>))]
public enum CronlightAlertKind
{
    /// <summary>The job has failed <see cref="CronlightAlert.ConsecutiveFailures"/> times in a row.</summary>
    Failing,

    /// <summary>The job succeeded after a failure streak that was alerted.</summary>
    Recovered
}

/// <summary>A job started failing, or recovered.</summary>
/// <param name="ConsecutiveFailures">For Failing, the streak that raised the alert; for Recovered, the streak that ended.</param>
/// <param name="Error">The last failure's exception type and message; null for Recovered.</param>
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
    public string Title => Kind == CronlightAlertKind.Failing
        ? ConsecutiveFailures == 1
            ? $"Job {JobGroup}.{JobName} failed"
            : $"Job {JobGroup}.{JobName} failed {ConsecutiveFailures} times in a row"
        : ConsecutiveFailures == 1
            ? $"Job {JobGroup}.{JobName} succeeded again after a failure"
            : $"Job {JobGroup}.{JobName} succeeded again after {ConsecutiveFailures} failures in a row";
}

/// <summary>
/// Receives every alert, in addition to the webhooks in <see cref="CronlightAlertOptions.Webhooks"/>. Register one
/// or more as singletons to send alerts elsewhere, such as email or a paging service. A sink that throws is
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

    /// <summary>The dashboard's address, e.g. https://ops.contoso.com/ops/jobs/, linked from alerts.</summary>
    public string? DashboardUrl { get; set; }

    public List<CronlightWebhook> Webhooks { get; } = new();

    /// <summary>Which jobs raise alerts. Null (the default) means all.</summary>
    public Func<JobKey, bool>? Jobs { get; set; }
}

/// <summary>A finished run, handed from the job thread to the alert worker.</summary>
internal sealed record AlertRun(
    string SchedulerName,
    string InstanceName,
    JobKey Job,
    string? JobType,
    string FireInstanceId,
    bool Succeeded,
    string? Error
);

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
internal sealed class AlertListener(AlertQueue queue, IOptions<CronlightAlertOptions> options) : IJobListener
{
    public string Name => "Cronlight.Alerts";

    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        var status = RunOutcome.Status(context, jobException);
        // Someone stopped it on purpose: neither a failure nor a recovery
        if (status == JobExecutionStatus.Cancelled || options.Value.Jobs?.Invoke(context.JobDetail.Key) == false)
            return Task.CompletedTask;

        queue.Enqueue(new AlertRun(
            context.Scheduler.SchedulerName,
            context.Scheduler.SchedulerInstanceId,
            context.JobDetail.Key,
            context.JobDetail.JobType.FullName,
            context.FireInstanceId,
            status == JobExecutionStatus.Succeeded,
            RunOutcome.Error(context, jobException)));
        return Task.CompletedTask;
    }

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Attaches the listener, tracks each job's failure streak and sends alerts. With execution history the streak
/// is read from history, so it covers every node of a cluster and one alert is sent per streak; without it,
/// each node counts the runs it executed itself.
/// </summary>
internal sealed class AlertWorker(
    ISchedulerFactory schedulerFactory,
    AlertListener listener,
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

    private readonly Dictionary<JobKey, int> streaks = new();
    private readonly CancellationTokenSource stopping = new();
    private Task? sending;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(options.Value);
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        if (scheduler.ListenerManager.GetJobListeners().All(l => l.Name != listener.Name))
            scheduler.ListenerManager.AddJobListener(listener, GroupMatcher<JobKey>.AnyGroup());
        sending = Task.Run(() => Send(stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The scheduler has already stopped (it is registered after us): send what is left, unless the host gives up
        queue.Complete();
        await using var _ = cancellationToken.Register(stopping.Cancel);
        if (sending != null)
            await sending;
    }

    private static void Validate(CronlightAlertOptions options)
    {
        if (options.AfterConsecutiveFailures < 1)
            throw new InvalidOperationException("CronlightAlertOptions.AfterConsecutiveFailures must be at least 1.");
        foreach (var webhook in options.Webhooks)
        {
            if (!Uri.TryCreate(webhook.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
                throw new InvalidOperationException("Every Cronlight alert webhook needs an absolute http(s) URL.");
        }
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
        var threshold = options.Value.AfterConsecutiveFailures;
        var before = await FailuresBefore(run, threshold, cancellationToken);
        streaks[run.Job] = run.Succeeded ? 0 : before + 1;

        if (!run.Succeeded && before + 1 == threshold)
            return Alert(run, CronlightAlertKind.Failing, threshold, Truncate(run.Error));
        if (run.Succeeded && before >= threshold && options.Value.NotifyOnRecovery)
            return Alert(run, CronlightAlertKind.Recovered, before, null);
        return null;
    }

    /// <summary>How many times the job failed in a row before this run.</summary>
    private async Task<int> FailuresBefore(AlertRun run, int threshold, CancellationToken cancellationToken)
    {
        if (services.GetService<IJobHistoryStore>() is not { } history)
            return streaks.GetValueOrDefault(run.Job);

        // Newest first. Some slack beyond the threshold for runs still going, cancelled or vetoed, which do not count.
        var records = await history.Query(run.SchedulerName, new JobHistoryQuery
        {
            JobGroup = run.Job.Group,
            JobName = run.Job.Name,
            Limit = threshold + 20
        }, cancellationToken);

        var failures = 0;
        foreach (var record in records.Where(r => r.FireInstanceId != run.FireInstanceId))
        {
            if (record.Status == JobExecutionStatus.Failed)
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

        foreach (var webhook in options.Value.Webhooks)
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
            $"{(alert.Kind == CronlightAlertKind.Failing ? ":x:" : ":white_check_mark:")} *{Escape(alert.Title)}*",
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
            facts.Add(new { title = "Error", value = alert.Error });

        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = alert.Title,
                weight = "Bolder",
                size = "Medium",
                wrap = true,
                color = alert.Kind == CronlightAlertKind.Failing ? "Attention" : "Good",
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
