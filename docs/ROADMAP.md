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
- Several schedulers in one app: schedulers the host builds in code are added with `UseCronlight`, picked in
  the dashboard's header and with `?scheduler=` in the API. Each has its own jobs, history and failure
  streaks; audit events and alerts name the scheduler. See the [design notes](#design-several-schedulers-in-one-app).
- Misfire visibility on Quartz 3.x: a trigger that missed its fire time is recorded in history as
  `Misfired`, at the fire time missed, with how many were missed and what Quartz did (ran it once now,
  skipped to the next fire time, or no fire times left). It shows in the History tab, its outcome filter and
  the job's recent runs, and raises an alert when `NotifyOnMisfire` is on.

## How Cronlight compares

Checked on 2026-10-25. The big change since the first version of this table: Quartz.NET 4.0 was released
(September 2026, `net10.0` only, breaking API changes and a mandatory schema migration), and the official
dashboard left "work in progress" with it, with ten releases from 4.0 to 4.6 in about a month.

| Product | What it is | Notable for |
| --- | --- | --- |
| Quartz.Dashboard 4.x (official) | Blazor dashboard for Quartz 4, released (4.6.0, 2026-10-19) | Misfires with reasons; history with charts and stats; several schedulers, including remote ones over HTTP, through an agent (`Quartz.Dashboard.Agent`) or from a store; which schedulers a visitor sees (`SchedulerAuthorizationPolicy`); read-only mode; live logs; job progress and captured log lines; interrupting a run; run now with job data overrides; in-memory action log (last 250) |
| Quartz.Dashboard 3.x (official) | The same dashboard for Quartz 3.16+, still marked work in progress | Several schedulers, read-only mode, live event stream; access control is dashboard-wide |
| Quartz.NET 4.x itself | The scheduler | Built-in retry policies (fixed, exponential with jitter, explicit; per trigger, per job type, or scheduler default; shared across a cluster), execution history, job outcomes (Succeeded, Skipped, Failed, Cancelled) |
| SilkierQuartz | Quartz dashboard | 9.0.x; the newest version found is from September 2025 |
| CrystalQuartz | Quartz dashboard | 7.x; the newest version found is from 2023 |
| Hangfire | Full scheduler with its own dashboard | Retries, wide adoption; 1.8.x line |
| TickerQ | Full scheduler with its own dashboard (10.4, May 2026) | Live SignalR dashboard, retries, multi-node, per-function concurrency limits, enabling and disabling cron jobs from the dashboard |

Compared with the official 4.x dashboard, Cronlight still leads on:

- failure and recovery alerts (Slack, Teams, webhook, email), which the official dashboard does not describe;
- a durable audit trail through sinks, where the official action log keeps the last 250 entries in memory
  (plus the application log);
- creating and editing jobs from a catalog of job types with typed, validated and secret parameters, and
  secret references kept out of the database;
- cross-site request protection and a Content-Security-Policy;
- running on Quartz 3.x from 3.13, with no Blazor circuit or SignalR connection.

It lags on:

- **Quartz 4 support.** Cronlight is built on Quartz 3.13 and does not run on 4.x, which is now the
  current line. Quartz 4 also brings its own retries and execution history, which overlap Cronlight's.
- which schedulers each visitor sees, and remote schedulers (HTTP, agent)
- live updates, job progress and captured log lines
- adoption

Suggested order: Quartz 4 support once 4.x settles (see
[Quartz 4 support](#quartz-4-support)), since it changes how retries, history and several schedulers should
be built.

## Next (1.x)

### Quartz 4 support

**Decision (2026-10-25): wait, and stay on Quartz 3.13.1.** 4.x is released (4.6.0 on 2026-10-19), but it is
seven weeks old with 12 releases, and recent ones fixed serious bugs: 4.6.0 fixed `UseClustering()` leaving
every node as `NON_CLUSTERED`, so nodes could take each other's fired triggers, and 4.2.4 fixed a database
error committing half a fire batch (duplicate firings on PostgreSQL, triggers stuck `BLOCKED` on SQL Server
and MySQL). 4.6.0 also added three more schema migrations. 3.x is still maintained (3.22.7 on the same day).

Move when all of these hold:

- at least four weeks after the latest 4.x release with no data-integrity, clustering or
  missed/duplicate-firing fixes;
- no new schema migrations in that time;
- no open 4.x bugs of that kind on GitHub.

Staying on 3.13.1 also avoids [quartznet#4167](https://github.com/quartznet/quartznet/issues/4167), open
since 2026-10-25: non-concurrent jobs with many triggers start 15–35 seconds late on 3.19.1 and 3.22.7, but
not on 3.13.0. Check it before raising the 3.x version.

Quartz.NET 4.0 targets `net10.0` only. Public `Task` members became `ValueTask`, `IJob.Execute` takes a
cancellation token, the container builds the scheduler (no `quartz.config`, no process-global singletons) and
the schema needs a migration. Cronlight's listeners, its table reads and its scheduler registry are all
affected. To decide:

- Support both lines (a 3.x package and a 4.x package), or move to 4.x only?
- On 4.x, use Quartz's own retry policies and execution history instead of Cronlight's, and show them,
  rather than run two of each?
- Process-global singletons are gone in 4.x, so `UseCronlight`'s reason for being opt-in (the
  process-wide scheduler registry) may not apply there.

### Several schedulers: follow-ups

Left out of the first version:

- **Permissions per scheduler**, for example letting the billing team see only the billing scheduler.
  Today one view policy and one manage policy cover every scheduler.
- **The fast database path for added schedulers.** The host could give Cronlight an added scheduler's
  database settings, so its job list reads two queries instead of loading each job, and its Running tab
  shows every node.

### Misfire follow-ups

Left out of the first version of misfire visibility:

- **Show and choose each trigger's misfire instruction** in the job's page and editor ("if missed: run once
  now / skip"). Today it shows only in a misfire's description, and cron triggers made in the editor use
  Quartz's default (run once now).
- **Runs that start late but within the misfire threshold** (under 60 seconds by default) are not
  misfires to Quartz and are not flagged. History has their scheduled and fired times, so a "started late"
  marker could be added.
- **Link a misfire to the late run that followed it.** The run is rescheduled to now, so its scheduled time
  is not the missed one.

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

- No end-to-end tests for alerts, which would need a webhook receiver in the sample host.
- `RetryTests.A_run_with_different_data_uses_it_for_that_run_and_its_retries_only` failed once on .NET 10
  during a full parallel run: the retry never showed in history. It passes on its own and in later full
  runs. A suspected cause, not confirmed, is a busy SQLite store making the retry fail to schedule, which is
  only logged (see Alert when a retry cannot be scheduled), and the tests discard logs.
- Not covered by tests: a history table without the `ATTEMPT` column when `CreateTable` is off, and a
  blocked run counting as a failure in a cluster, where streaks are read from history.

---

## Design: several schedulers in one app

Built from the design below, except for permissions per scheduler (a follow-up), with these decisions:

- **Opt-in, with `UseCronlight`.** Quartz's `GetAllSchedulers` returns every scheduler in the process,
  including other apps' (and, in the test suite, other test hosts'), so attaching to all of them would
  record and alert on runs that are not this app's. The host adds each extra scheduler with
  `scheduler.UseCronlight(services)` before starting it, which attaches history, alerts, retries and secret
  references at once, so no early run is missed. A scheduler created later is added the same way.
- **Quartz's API only** for added schedulers, as the open question below suggested. The table reads
  (`JobIndexReader`, `RunningJobsReader`) apply to the `AddQuartz` scheduler only.
- **One registry** (`CronlightSchedulers`) runs one attachment per feature (`ISchedulerAttachment`) on each
  scheduler, in place of the four hosted services that each attached to the default scheduler.
- **The dashboard starts afresh** when another scheduler is picked, so no open job, filter or page carries
  over. The choice is kept in the page's URL.

The original design follows.

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
