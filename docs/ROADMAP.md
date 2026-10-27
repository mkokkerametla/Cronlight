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
- Follow-ups to the above:
  - Retries: Run again repeats a run with its own data (kept in history, secrets masked); waiting retries
    show on the job's page and can be cancelled; retries can be limited to some exception types; a retry
    that cannot be scheduled leaves the run failed and alerts.
  - Misfires: each trigger's misfire behaviour shows on the job's page, and "Skip missed runs" can be chosen
    for cron triggers; runs that start 5 seconds late or more are marked; the run that catches up after a
    misfire is recorded at the fire time it makes up for.
  - Several schedulers: permissions per scheduler (`o.Schedulers[name]`); an added scheduler given its
    properties is read from its tables, like the host's own; each scheduler can offer only its own app's job
    types in New job (`o.Schedulers[name].JobAssemblies`).
- Log output for each run: what a job logs through `ILogger` while it runs is kept with that run in history
  (`RUN_LOG` column), with sensitive job data values masked, and shown in the History tab. Captured by
  wrapping the host's job factory, so the log follows the run's async flow; a scheduler built in code opts in
  with `WithCronlightRunLogs`.
- One-time and interval triggers in the editor, alongside cron. Editing replaces the three kinds and keeps
  waiting retries and other trigger types.
- Pausing and resuming a group, or the whole scheduler, from the job list. Refused for groups that hold
  another app's jobs, and audited.
- OpenTelemetry metrics on the `Cronlight` meter (runs by outcome, duration, active runs, misfires) and a
  health check (`AddHealthChecks().AddCronlight()`) for shut-down or unreachable job stores.
- Clustered schedulers by their nodes: the header says how many nodes run the scheduler, read from
  `QRTZ_SCHEDULER_STATE`, the scheduler details list them, and `AddCronlight(checkCluster: true)` checks a
  dashboard that never starts its schedulers by the cluster's nodes.
- Triggers keep their Quartz calendar, priority, description and data when their schedule is edited, and the
  job panel names the calendar and lists a trigger's next five fire times.
- Export and import of jobs as JSON or YAML (`api/jobs/export`, `api/jobs/import`, and the All jobs menu): an import
  creates and updates jobs to match the file, never deletes one, checks every job before changing any, and can be
  checked first with a dry run. See [Exporting and importing jobs](export-import.md).
- An OpenAPI 3.1 description of the API at `api/openapi.json`, a document of its own that the host's documents leave
  out. See [Setup and options](configuration.md#the-apis-description).
- An MCP server for AI assistants, in its own package (`Cronlight.Quartz.Dashboard.Mcp`): read-only tools for jobs,
  runs, logs and failure and status summaries, and opt-in pause, resume, run and run again, on the dashboard's
  permissions and audit log, and history analytics: per-job run statistics (success rate, average, median and P95
  duration, compared with the window before), runs over time, and errors grouped across jobs. See
  [AI assistants (MCP server)](mcp.md). The dashboard itself does not show these statistics yet (#29).

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
- remote schedulers (HTTP, agent)
- live updates and job progress (captured log lines are now kept per run, but shown once the run has finished)
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

**Checked again on 2026-10-27: still wait.** 4.6.1 (2026-10-26) fixed a start resetting declared triggers' run
state, so a trigger with a set number of runs ran them again, a retry pending at shutdown was dropped and a
trigger an operator paused was resumed. That restarts the four weeks below: 2026-11-23 at the earliest. 4.6.2 the
same day fixed time zone names only. Open: [quartznet#4173](https://github.com/quartznet/quartznet/issues/4173), a non-concurrent job's next run starting about 70 ms
later on 4.x's database stores than on 3.x, with stalls of seconds early in a backlog. 3.22.8 and 3.22.9 shipped
the same fixes for 3.x.

Move when all of these hold:

- at least four weeks after the latest 4.x release with no data-integrity, clustering or
  missed/duplicate-firing fixes;
- no new schema migrations in that time;
- no open 4.x bugs of that kind on GitHub.

The packages still require only Quartz 3.13.1, so a host is not moved to a later 3.x by installing Cronlight:
[quartznet#4167](https://github.com/quartznet/quartznet/issues/4167), open since 2026-10-25, makes
non-concurrent jobs with many triggers start 15–35 seconds late on 3.19.1 and 3.22.7, but not on 3.13.0.
3.22.8 makes such a job's completion read only its misfired triggers, which shortens that drain; check it before
raising that minimum. The tests and the sample run on the latest 3.x (3.22.7 since
2026-10-26), and the package smoke test on 3.13.1. Cronlight targets .NET 10 only since 2026-10-26 (.NET 8
support ends 2026-11-10); hosts on .NET 8 can stay on 0.1.0-beta.2.

Quartz.NET 4.0 targets `net10.0` only. Public `Task` members became `ValueTask`, `IJob.Execute` takes a
cancellation token, the container builds the scheduler (no `quartz.config`, no process-global singletons) and
the schema needs a migration. Cronlight's listeners, its table reads and its scheduler registry are all
affected. To decide:

- Support both lines (a 3.x package and a 4.x package), or move to 4.x only?
- On 4.x, use Quartz's own retry policies and execution history instead of Cronlight's, and show them,
  rather than run two of each?
- Process-global singletons are gone in 4.x, so `UseCronlight`'s reason for being opt-in (the
  process-wide scheduler registry) may not apply there.

### Not planned now

- **Live updates** (an event stream and a Live view, [#26](https://github.com/mkokkerametla/Cronlight/issues/26)) are not planned: polling every 10 seconds suits a
  jobs dashboard, and a stream brings proxy, fallback and cluster limits (a node only sees its own events). A
  running job's log so far could later be read on each refresh, without a stream.

### Deferred

Waiting for a user who asks for them, or for Quartz 4, which does some of them itself:

- Branding options and an embedded mode for showing the dashboard inside an app's own layout ([#23](https://github.com/mkokkerametla/Cronlight/issues/23)).
- Cancelling a run on any node of a cluster, through the store ([#27](https://github.com/mkokkerametla/Cronlight/issues/27); Quartz 4 does this).
- An Activity tab showing the audit trail in the dashboard ([#28](https://github.com/mkokkerametla/Cronlight/issues/28)).
- Run statistics in the dashboard: success rate and average and P95 duration over History and per job ([#29](https://github.com/mkokkerametla/Cronlight/issues/29)).
  The MCP server's `run_statistics` already counts them, which the dashboard could show.
- Attaching to a Quartz database and listing every scheduler in it ([#39](https://github.com/mkokkerametla/Cronlight/issues/39); Quartz 4 does this).

## 2.0

- **Permissions per group**, and a "can run, can't edit" role.
- **Schedules in plain words**: type "every weekday at 9:30" and an AI integration the host sets up turns it into
  cron, shown in the cron box with its preview to check before saving. Off unless the host turns it on, since
  what users type goes to that AI service. Until then, the trigger editor links to Quartz.NET's cron syntax.

## For a global audience

- **Translations** of the dashboard.
- **Time zones**: show and edit schedules in a chosen zone, and warn about daylight-saving gaps and repeats.
- **Accessibility**: an axe scan in the tests, and tests at phone width.

## Known gaps

- Run logs are shown once a run has finished; a running job's log so far is not visible.
- The misfire counter can count a misfire twice when a database store rolls back its misfire handling and
  reports it again; history keeps one record per misfire.
- No end-to-end tests for email alerts or misfire alerts; the integration tests cover both.
- On Quartz before 3.17, a job with a retry policy that fails with `RefireImmediately` is retried as well as
  refired: Quartz reports that attempt's end to listeners there, and the retry planner does not check for a
  refire. 3.17 and later do not report it, so it does not happen there. The tests run on 3.22.7, so this path
  is untested.
- Not covered by tests: a history table without the `ATTEMPT` column when `CreateTable` is off, and a
  blocked run counting as a failure in a cluster, where streaks are read from history.

---

## Design: several schedulers in one app

Built from the design below, with permissions per scheduler added later, and with these decisions:

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
