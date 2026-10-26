# Failure alerts

Alerts by webhook (Slack, Teams, JSON), email or your own sink when jobs fail, recover, stop or misfire.

```csharp
builder.Services.AddCronlightAlerts(o =>
{
    o.AfterConsecutiveFailures = 3;                    // default 1
    o.DashboardUrl = "https://ops.contoso.com/ops/jobs/";
    builder.Configuration.GetSection("CronlightAlerts").Bind(o);
});
```

```json
{
  "CronlightAlerts": {
    "Webhooks": [
      { "Url": "https://hooks.slack.com/services/…", "Format": "Slack" },
      { "Url": "https://prod-00.westeurope.logic.azure.com/workflows/…", "Format": "Teams" },
      { "Url": "https://alerts.contoso.com/cronlight", "Format": "Json" }
    ],
    "Email": {
      "Host": "smtp.office365.com",
      "Port": 587,
      "UserName": "cronlight@contoso.com",
      "Password": "…",
      "From": "Cronlight <cronlight@contoso.com>",
      "To": [ "ops@contoso.com" ]
    }
  }
}
```

When a job fails `AfterConsecutiveFailures` times in a row, Cronlight sends one alert: the job, the node, the
error and a link to the dashboard. When it next succeeds, a recovery alert follows (`NotifyOnRecovery`,
on by default). Every alert is also logged as a warning under the `Cronlight.Alerts` category.

- **Slack:** an incoming webhook. **Teams:** a Workflows "when a webhook request is received" flow, or
  an incoming webhook connector; both receive an Adaptive Card. **Json:** the alert as JSON, for your own
  endpoints.
- **Email** (optional, off unless `Email` is set; settings missing a host, sender or recipient are
  skipped with a warning rather than stopping the app): a plain-text message to every `To` address
  through your SMTP server. It connects with
  STARTTLS (`UseTls`, on by default), usually on port 587 as Office 365, SendGrid and Amazon SES offer;
  implicit TLS on port 465 is not supported. `UserName` and `Password` are optional, for relays that
  accept the app without signing in.
- Webhook URLs and the SMTP password work as passwords, so keep them in configuration or a key vault
  rather than code.
- Network errors, timeouts, 5xx responses and unavailable mail servers are retried twice; other failures
  are logged. One channel failing does not stop the others.
- A webhook without an absolute http(s) URL is skipped with a warning; the app still starts and the other
  channels still send.
- Cancelled runs neither count as failures nor end a streak. A run vetoed because a
  [secret reference](job-types.md#secret-references) cannot be resolved counts as a failure: the job is misconfigured
  and will not run until it is fixed. The alert's error starts with "Not run:". Other vetoed runs are
  ignored, unless the host's trigger listener sets `CronlightContextKeys.VetoReason`.
- `o.Jobs = job => job.Group != "experiments"` limits which jobs raise alerts.
- **Misfires** (a trigger that missed its fire time, see [execution history](history.md))
  raise a `Misfired` alert when `NotifyOnMisfire` is on; it is off by default, since a restart after
  downtime raises one for every trigger that was due meanwhile. The alert's error says what was missed and
  what Quartz does about it. Misfires neither count as failures nor end a streak.
- A single scheduler counts every run itself. In a cluster with
  [execution history](history.md), the streak is read from history, so it counts
  runs on every node and normally one alert is sent per streak. In a cluster without history, each node
  counts only the runs it executed. Streaks start again from zero when the app restarts.
- **A job Quartz could not create** (see [execution history](history.md)) is alerted at
  once, whatever `AfterConsecutiveFailures` says and even when its failures were alerted already: Quartz has stopped
  it, so it cannot fail again until someone resumes it. The alert's error starts with "Not run: the job could not be
  created". As after other failure
  alerts, its next success sends a recovery alert from the node that alerted, unless the app has restarted since
  (a fix that needs a deploy usually means a restart; see the streaks below).
- Alerts work without the dashboard: an app that only runs jobs can call `AddCronlightAlerts` alone.

For a paging service or anything else, register a sink. Sinks are resolved once, as singletons:

```csharp
builder.Services.AddSingleton<ICronlightAlertSink, PagerAlertSink>();

class PagerAlertSink(IPagerClient pager) : ICronlightAlertSink
{
    public Task SendAsync(CronlightAlert alert, CancellationToken ct) =>
        alert.Kind == CronlightAlertKind.Failing ? pager.Trigger(alert.Title, alert.Error, ct) : pager.Resolve(alert.Title, ct);
}
```
