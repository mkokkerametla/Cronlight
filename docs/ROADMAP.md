# Cronlight roadmap

What has been suggested for Cronlight but not built yet, with a design for the largest item: several
schedulers in one app.

## Already built

These are listed so the roadmap below is read against them:

- Security hardening: cross-site request protection, audit log, Content-Security-Policy, hardened sample
  HttpJob.
- Secret references (`secret:Name`) resolved from configuration at run time.
- Cancelling running jobs.
- Failure and recovery alerts by webhook (JSON, Slack, Teams) and, optionally, email.
  - A run blocked because a secret reference cannot be resolved counts as a failure (the job is
    misconfigured), and its alert error starts with "Not run:".
  - A webhook without a valid URL is skipped with a warning instead of stopping the app, as email
    settings already were.
- Server-side search, filtering and paging of the job list; no polling while the tab is hidden.
- Retries and re-running:
  - Automatic retries of failed runs, set per job type with `[CronlightRetry]` or per job in the editor,
    with a fixed or doubling wait.
  - History shows each attempt, with attempts that will be retried as `Retried`. Alerts count only the
    last attempt.
  - Run a job once with different data, without changing the saved job.
  - Run a failed, blocked or cancelled run again from the History tab.

## How Cronlight compares

| Product | What it is | Notable for |
| --- | --- | --- |
| Quartz.Dashboard (official) | Blazor dashboard, experimental | Several schedulers per app, authorization per scheduler, history, misfire tracking, read-only mode |
| SilkierQuartz | Quartz dashboard, v10 | Successor to Quartzmin |
| CrystalQuartz | Quartz dashboard | Last activity found June 2025 |
| Hangfire | Full scheduler with its own dashboard | Retries (Cronlight now has them too), wide adoption |
| TickerQ | Full scheduler with its own dashboard (v10.4) | Live SignalR dashboard, retries (Cronlight now has them too), multi-node, ABP integration |

Cronlight leads on security, governance and day-to-day operations. It lags on:

- several schedulers in one app
- misfire visibility
- live updates pushed to the dashboard instead of polling
- adoption

Suggested order: several schedulers in one app, and misfire visibility, come first.

## Next (1.x)

### Several schedulers in one app

See the [design below](#design-several-schedulers-in-one-app).

### Misfire visibility

Show when a trigger missed its fire time (scheduler down, no free threads) and what Quartz did about it
(fired late, skipped, rescheduled). Record misfires in history and show them on the job's page and in the
history filter. Optionally alert on them.

### Retry follow-ups

Left out of the first version of retries:

- **Keep each run's data in history**, so Run again repeats a failed run exactly. Today it runs the job
  with its saved data, which differs when the failed run was started with different data.
- **Show waiting retries**: when the next attempt is due, on the job's page and in the job list, with a
  way to cancel it.
- **Retry only some failures**, for example by exception type, so a validation error is not retried
  pointlessly.
- **Alert when a retry cannot be scheduled.** If the job store is unavailable at that moment, the run is
  already recorded as Retried and no alert is sent; only an error is logged.

### Log output for each run

Capture what a job logs while it runs and show it with that run in history.

### More trigger types in the editor

One-off (run once at a given time) and simple interval triggers, alongside cron.

### Pause a group or the whole scheduler

Pause and resume every job in a group, or the whole scheduler, from the dashboard. Audited like other
changes.

### Live updates

Push changes (runs starting and finishing, jobs paused) to the dashboard, for example with server-sent
events or SignalR, and fall back to the current polling.

### OpenTelemetry metrics and a health check

- Metrics: runs, failures, duration, misfires and running count per job.
- A health check that reports an unreachable job store or a scheduler that is not running.

## 2.0

- **Export and import** jobs as JSON or YAML, for moving jobs between environments and keeping them in
  source control.
- **Permissions per group**, and a "can run, can't edit" role.
- **OpenAPI description** of the API.

## For a global audience

- **Translations** of the dashboard.
- **Time zones**: show and edit schedules in a chosen zone, and warn about daylight-saving gaps and repeats.
- **Built-in cron builder** to replace the links to ChatGPT and Google.
- **Accessibility**: an axe scan in the tests, and tests at phone width.

## Simplify

- Remove `AllowManagingUnloadableJobs`.
- Use one wording for refusing to change another app's job. Today there are two: "Job is read-only in this
  dashboard." and "...cannot be loaded here and cannot be edited."
- Clearer error messages in general.

## Known gaps

- The end-to-end tests have not been run against secret references, cancelling, alerts, paging or
  retries.
- There are no end-to-end tests for cancelling a run, paging the job list, retries, running with
  different data, or Run again.
- The retry and run-with-data tests (`RetryTests.cs`, the retry scenarios in `HistoryTests.cs` and the UI
  tests) were written but have not been run yet.
- Not covered by tests: a history table without the `ATTEMPT` column when `CreateTable` is off, and a
  blocked run counting as a failure in a cluster, where streaks are read from history.

---

## Design: several schedulers in one app

Some apps run more than one Quartz scheduler, for example one per team or one per database. Today Cronlight
shows only the default scheduler.

### Where the code assumes one scheduler

- **The API**: 12 places in `DashboardApi.cs` call `factory.GetScheduler()`, which returns the default
  scheduler.
- **History, alerts, secret references and retries** each attach a listener to the default scheduler at
  startup (`Cronlight.History/JobHistoryWriter.cs`, `Alerts.cs`, `SecretReferences.cs`, `Retries.cs`).
- **Database access**: the job list index (`JobIndexReader`), the cluster-wide Running tab
  (`RunningJobsReader`) and the history store (`AdoJobHistoryStore`) read their connection from
  `QuartzDataSource`. That is built from the single `AddQuartz` configuration, so it only knows the default
  scheduler's database.

### The limitation

Quartz lets Cronlight list every scheduler in the app (`ISchedulerFactory.GetAllSchedulers`) and act on each
one: pause, run, edit, cancel. It does not expose another scheduler's database settings, so Cronlight can
manage extra schedulers but cannot read their tables directly.

- **The default scheduler** keeps everything it has today.
- **Extra schedulers** use Quartz's own API, which Cronlight already falls back to for the in-memory store.
  Everything works, with two costs: the job list loads each job one by one, and the Running tab shows only
  this node.
- **Optionally**, the host can give Cronlight an extra scheduler's database settings, which turns on the
  fast path for that scheduler too.
- **History** for every scheduler goes into the existing history table, which already stores the scheduler
  name (`SCHED_NAME`) on each row. The extra schedulers' databases are never touched for history.

### Plan

1. **Server**: pick the scheduler from a `?scheduler=` parameter, defaulting to today's scheduler, so
   existing scripts and links keep working. Add a list of schedulers to the API, and the scheduler name to
   audit events and alerts.
2. **Listeners**: attach history, alerts, secret references and retries to every scheduler. A scheduler created after
   the app has started is picked up when it first appears in the dashboard.
3. **Permissions** (optional): a separate permission per scheduler, for example letting the billing team see
   only the billing scheduler.
4. **UI**: a scheduler dropdown in the header, with every view following it. When the app has only one
   scheduler, nothing changes on screen.
5. **Tests**: two schedulers in one test app, one database-backed and one in memory, checking that each view
   and action reaches the right scheduler, and permissions per scheduler.

### Size and risk

Similar in size to the job list paging work. Most of the risk is in step 2, making sure every listener
reaches every scheduler.

### To decide before starting

How do host apps create the extra schedulers: through `AddQuartz`, or by building them in code? That decides
whether the optional per-scheduler database settings are worth building now. Without a real setup in mind,
build with Quartz's API only and add the faster database path later.
