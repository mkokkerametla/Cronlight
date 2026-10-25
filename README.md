<img src="https://raw.githubusercontent.com/mkokkerametla/Cronlight/main/assets/icon.png" alt="" width="64" height="64" align="left">

# Cronlight

[![CI](https://github.com/mkokkerametla/Cronlight/actions/workflows/ci.yml/badge.svg)](https://github.com/mkokkerametla/Cronlight/actions/workflows/ci.yml)

**A spotlight on your Quartz.NET jobs.** Cronlight is an embeddable UI and API for
[Quartz.NET](https://www.quartz-scheduler.net/): add it to the ASP.NET Core app that already runs your
scheduler to list, create, edit, pause, resume, trigger and delete jobs, and see what is running and
what failed.

Cronlight uses the scheduler you already configured. It needs no connection string or schema of its own
and never starts a second scheduler.

## Install

```bash
dotnet add package cronlight-quartz-scheduler --prerelease
dotnet add package cronlight-quartz-scheduler-history --prerelease   # optional: execution history
```

Cronlight is in beta: `--prerelease` is needed until 1.0, and options or endpoints may still change
between betas.

Requires ASP.NET Core 8 or 10 and Quartz.NET 3.x configured with `AddQuartz()`.

## Setup

```csharp
// 1. Your existing Quartz setup stays exactly as it is
builder.Services.AddQuartz(q =>
{
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;
        s.UseSqlServer(builder.Configuration.GetConnectionString("Quartz")!); // from Key Vault, env vars, ...
        s.UseNewtonsoftJsonSerializer();
    });
});
builder.Services.AddQuartzHostedService();

// 2. Add Cronlight
builder.Services.AddCronlight(o =>
{
    o.ViewPolicy = "QuartzViewers";   // required outside Development
    o.ManagePolicy = "QuartzAdmins";  // create/edit/pause/delete
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// 3. Map it under any path
app.MapCronlight("/ops/jobs");
```

Open `/ops/jobs/`. The API is at `/ops/jobs/api/*`.

### Options

| Option | Default | |
|---|---|---|
| `ViewPolicy` | none | Authorization policy for the UI and read endpoints |
| `ManagePolicy` | `ViewPolicy` | Policy for mutations |
| `ReadOnly` | `false` | Disable every mutation |
| `AllowAnonymousOutsideDevelopment` | `false` | Without a `ViewPolicy` Cronlight returns 403 outside Development unless this is set |
| `AllowManagingUnloadableJobs` | `false` | Allow pause/resume/delete on jobs whose type isn't in this app |
| `JobAssemblies` | all loaded app assemblies | Where to look for `IJob` types offered in "New Job" |
| `ShowScheduleAssistant` | `false` | Show the "describe your schedule" box that opens the question in ChatGPT or Google. What users type goes to that site, so turn it on only where staff may use external AI or search tools |
| `RefreshSeconds` | `10` | How often an open dashboard refreshes jobs, running jobs and history (1 to 300 seconds) |
| `SensitiveKeyPatterns` | password, secret, token, apikey, connectionstring | JobDataMap keys masked in responses |
| `SecretsSection` | none | Configuration section that [secret references](#secret-references) are read from; none turns them off |
| `ContentSecurityPolicy` | own files only, no framing | Content-Security-Policy for the dashboard. Change `frame-ancestors` to show it inside your own portal; `null` sends none |

## Describing your jobs (optional)

Any `IJob` in your app can be created from the UI with free-form job data. Attributes give it a proper form:

```csharp
[CronlightJob("Send invoices", Description = "Emails pending invoices for a customer")]
[JobParameter("customerId", Type = JobParameterType.Int, Required = true)]
[JobParameter("smtpPassword", Type = JobParameterType.Secret)]
[JobParameter("batchSize", Type = JobParameterType.Int, DefaultValue = "50")]
public class InvoiceJob : IJob { ... }
```

Parameter types are `String`, `Int`, `Bool`, `Url`, `Text` (multi-line) and `Secret` (masked).
`Choices = new[] { "csv", "json" }` turns a parameter into a dropdown and the API rejects other values.

Only types discovered in your app can be created; the API never instantiates a type by an arbitrary name.

### Secret references

Secret values are masked in the UI but stored as plain text in the Quartz tables. To keep them out of the
database, store a reference instead: set a secret parameter to `secret:Smtp:Password`, and Cronlight reads
the value from configuration just before each run.

```csharp
builder.Services.AddCronlight(o => o.SecretsSection = "JobSecrets");
```

```json
{ "JobSecrets": { "Smtp": { "Password": "…" } } }
```

Any configuration source works: user secrets, environment variables (`JobSecrets__Smtp__Password`) or
Azure Key Vault through its configuration provider (a vault secret named `JobSecrets--Smtp--Password`).

- Only keys inside `SecretsSection` can be referenced, so people who can edit jobs cannot read
  connection strings or other configuration.
- References are resolved only in secret parameters and keys matching `SensitiveKeyPatterns`. In other
  parameters `secret:…` is plain text, so a secret cannot be copied into a URL or request body.
- The API checks references when a job is saved and refuses unknown names. A run whose secret is missing
  (removed from configuration later, or a job saved by code) is vetoed and history shows why.
- References are not secret, so the UI and audit log show them as written.
- The value replaces the reference in the run's `context.MergedJobDataMap`, which is where jobs should
  read it. The stored job keeps the reference. `context.JobDetail.JobDataMap` holds the reference too.
- Every app that runs these jobs (each cluster node) needs `AddCronlight` with the same `SecretsSection`
  and the secrets in its configuration.

## Retries and re-running

A failed run can be retried automatically. Give the job type a default:

```csharp
// Waits 30 s, 60 s, then 120 s; without Exponential, 30 s each time
[CronlightRetry(3, DelaySeconds = 30, Exponential = true)]
public class InvoiceJob : IJob { ... }
```

and change it for a single job in the dashboard's create and edit forms (Retries 0 turns them off, even
when the type has a default). A job that has not been given its own policy follows its type's, so changing
the attribute later applies to it.

- **Each retry is a one-off trigger** of the same job, in the trigger group `Cronlight.Retries`. It is
  stored like any trigger, so in a cluster any node may run it and it survives a restart. It carries the
  failed run's trigger data, so a run started with different data is retried with that data.
- **Only failures are retried**, not cancelled or vetoed runs. Waits are capped at a day.
- **Pausing** the job pauses a waiting retry too. A job paused while its run was failing keeps the
  retry paused; a manual run of a paused job is retried as usual.
- **History** shows an attempt that will be retried as `Retried`, with its attempt number, and only the last
  attempt as `Failed` or `Succeeded`. **Alerts** look at the last attempt only, so a run that succeeds on a
  retry raises none.
- The policy is kept in the job's data under `Cronlight.Retry`. Job data keys starting with `Cronlight.`
  are Cronlight's own: the dashboard hides them and the API refuses to set them.
- Every app that runs these jobs (each cluster node) needs `AddCronlight`, which attaches the retry
  listener.

To run a job once with different data, open it and choose **Run with different data**. The changes apply
to that run and its retries only; the saved job keeps its data, and the audit log records what was changed.
From the API, send the changed values with the run (validated like an edit; a masked secret keeps the
saved value):

```bash
curl -X POST -H "X-Cronlight-Request: 1" -H "Content-Type: application/json" \
  -d '{"data":{"region":"us"}}' …/api/jobs/DEFAULT/nightly/trigger
```

The History tab has **Run again** on runs that failed, were vetoed or were cancelled. It runs the job with
its saved data (history does not keep a run's own data), and the audit log records which run it repeats as
`retryOf`.

## Several schedulers in one app

Cronlight serves the scheduler from `AddQuartz`. An app that builds more schedulers in code (one per team,
or per database) adds each one with `UseCronlight`, before starting it:

```csharp
var reports = await new StdSchedulerFactory(properties).GetScheduler();
reports.JobFactory = app.Services.GetRequiredService<IJobFactory>(); // jobs with constructor dependencies
await reports.UseCronlight(app.Services); // dashboard, history, alerts, retries and secret references
await reports.Start();
```

The dashboard then shows a scheduler picker in its header, and the API takes `?scheduler=<name>` on every
endpoint (without it, requests go to the `AddQuartz` scheduler, so existing scripts keep working). The
choice is kept in the page's URL, so a link opens the same scheduler. Each scheduler has its own jobs,
history and failure streaks, and audit events and alerts name the scheduler.

- Only schedulers added this way are served. Quartz keeps every scheduler in the process in one registry,
  which can include other apps' schedulers; Cronlight leaves those alone, and asking for one returns 404.
- Cronlight reads the database tables of the `AddQuartz` scheduler only. Another scheduler is read through
  Quartz's API: its job list loads each job, which suits hundreds of jobs rather than thousands, and its
  Running tab shows this node's runs only.
- History of every scheduler goes into the one history table (`SCHED_NAME` tells them apart), and
  `Retention` applies to each.
- The same permissions apply to every scheduler.

The sample host has a second, in-memory scheduler named Reports (`samples/Cronlight.SampleHost/ReportsScheduler.cs`).

## Scheduling API calls (HTTP job)

The sample includes [`HttpJob`](samples/Cronlight.SampleHost/HttpJob.cs), a job type for calling HTTP APIs
on a schedule: URL, method, headers, body, an `authorization` header kept as a masked secret, timeout and
expected status codes (`2xx`, `200,202`, ...). A response with any other status fails the run, so it shows
up in execution history.

Copy `HttpJob.cs` and `HttpJobNetwork.cs` into your app and register the job with the hosts it may call:

```csharp
builder.Services.AddHttpJob(o => o.AllowedHosts.AddRange(["api.partner.com", "*.hooks.contoso.com"]));
```

Anyone who can create jobs chooses the URL, so the job only calls what you allow:

- **Hosts** must be in `AllowedHosts`: exact names, or `*.example.com` for its subdomains. An empty list
  allows none.
- **Internal addresses are refused** even for allowed names: loopback, private networks, link-local
  (including the cloud metadata endpoint `169.254.169.254`) and other reserved ranges, in IPv4 and IPv6.
  The check runs on the address actually connected to, after DNS, so a name pointing (or re-pointed)
  at an internal address is refused too. Set `AllowPrivateNetworks = true` only if job authors may
  call internal services.
- **Redirects are not followed**, and no proxy is used: a 3xx response is a status like any other.

## Execution history (optional package)

`Cronlight.History` records every run (start, finish, duration, node, outcome and error message) and
adds a History tab, a "Failed (24h)" card and recent runs per job to the dashboard.

```csharp
builder.Services.AddCronlightHistory(o =>
{
    o.Retention = TimeSpan.FromDays(30);   // default 14 days; null keeps everything
});
```

- **Storage.** By default history goes into the Quartz database, in a table next to Quartz's own
  (`{prefix}CRONLIGHT_HISTORY`, e.g. `QRTZ_CRONLIGHT_HISTORY`), using the same provider and connection
  string as the job store. Every node in a cluster writes to it, so the dashboard shows the whole cluster.
  Supported: SQL Server, PostgreSQL, MySQL and SQLite. With the RAM job store (or another database)
  history is kept in memory on each node instead, and a warning is logged.
- **Table creation.** The table is created on startup if missing. Where the app has no DDL rights, set
  `CreateTable = false` and run the script for your database from `src/Cronlight.History/Schema`.
  Indexes and columns added in later versions (such as `ATTEMPT`, for retries) are added to an existing
  table at startup; with `CreateTable = false` a missing one is logged as a warning with the statement to
  run, and history is written without it.
- **Jobs are never slowed down.** Listeners only queue records; a background writer stores them. If the
  database is unavailable, records beyond `QueueCapacity` are dropped and jobs carry on.
- **Errors** are stored as exception type and message (truncated to `MaxErrorLength`), never stack traces.
- **Misfires.** When a trigger misses its fire time by more than Quartz's misfire threshold (60 seconds by
  default; the scheduler was down, paused or had no free thread), history records it as `Misfired`: at the
  fire time it missed, with how many fire times were missed and what Quartz does about it, e.g. "Missed 3
  fire times, the first by 2h 5m; Quartz runs it once now (misfire instruction FireOnceNow)." A run that
  follows appears as a run of its own. The count ignores the trigger's calendar, so times a calendar
  excludes are counted as missed too. Triggers with `IgnoreMisfirePolicy` fire every missed time instead
  and are not reported as misfires. Filter the History tab by *Misfired* to see them all.
- **Retention** is applied hourly by every node and only touches its own scheduler's rows, so apps sharing
  a database keep their own retention.
- **Your own store:** register an `IJobHistoryStore` before calling `AddCronlightHistory`.

| Option | Default | |
|---|---|---|
| `Storage` | `Auto` | `Auto`, `Database` (fail if the database can't be used) or `InMemory` |
| `TableName` | prefix + `CRONLIGHT_HISTORY` | |
| `CreateTable` | `true` | |
| `Retention` | 14 days | `null` keeps everything |
| `CleanupInterval` | 1 hour | |
| `InMemoryCapacity` | 5000 | Records kept per node by the in-memory store |
| `QueueCapacity` | 10000 | |
| `MaxErrorLength` | 2000 | |

## Audit log

Every change made through the dashboard or its API (create, edit, pause, resume, run now, cancel, delete) is
logged under the `Cronlight.Audit` category: who made it, the job, whether it succeeded, for creates
and edits what changed, and for runs with different data what was changed for that run. Refused attempts are logged as warnings with the reason, such as a cross-site
request or read-only mode. Requests turned away by the host's authorization policies before reaching
Cronlight are logged by ASP.NET Core instead. Secret values are never written: a changed secret shows as
`********`.

```text
Audit: alice@contoso.com update billing.invoices in Billing: succeeded (204) {"description":{"Before":"Weekly","After":"Daily"},"triggers":{"Before":"0 0 9 ? * MON (UTC)","After":"0 0 9 * * ? (UTC)"}}
```

The fields are structured (`User`, `AuditAction`, `JobGroup`, `JobName`, `Scheduler`, `AuditOutcome`, `StatusCode`),
so log tools such as Seq, Application Insights or Elasticsearch can filter on them. To keep events in your
own store as well, register one or more sinks:

```csharp
// Resolved per request, so scoped services such as a DbContext can be used
builder.Services.AddScoped<ICronlightAuditSink, MyAuditSink>();

class MyAuditSink(AuditDbContext db) : ICronlightAuditSink
{
    public async Task WriteAsync(CronlightAuditEvent e, CancellationToken ct) { /* e.User, e.Action, e.Changes, ... */ }
}
```

A sink that throws is logged as an error; the change itself has already been made and is not undone.

## Failure alerts

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
  [secret reference](#secret-references) cannot be resolved counts as a failure: the job is misconfigured
  and will not run until it is fixed. The alert's error starts with "Not run:". Other vetoed runs are
  ignored, unless the host's trigger listener sets `CronlightContextKeys.VetoReason`.
- `o.Jobs = job => job.Group != "experiments"` limits which jobs raise alerts.
- **Misfires** (a trigger that missed its fire time, see [execution history](#execution-history-optional-package))
  raise a `Misfired` alert when `NotifyOnMisfire` is on; it is off by default, since a restart after
  downtime raises one for every trigger that was due meanwhile. The alert's error says what was missed and
  what Quartz does about it. Misfires neither count as failures nor end a streak.
- A single scheduler counts every run itself. In a cluster with
  [execution history](#execution-history-optional-package), the streak is read from history, so it counts
  runs on every node and normally one alert is sent per streak. In a cluster without history, each node
  counts only the runs it executed. Streaks start again from zero when the app restarts.
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

## Behaviour worth knowing

- **Secrets** are shown as `********`. Saving a masked value back keeps the stored value. To keep them
  out of the database too, use [secret references](#secret-references).
- **Cancelling a run** from the Running tab asks the job to stop by cancelling
  `context.CancellationToken`, using Quartz's interrupt. Jobs that pass the token on (to `Task.Delay`,
  HttpClient, database calls) stop promptly; a job that never checks it keeps running. History records the
  run as Cancelled, with who cancelled it. Quartz can only interrupt runs on its own node, so in a cluster
  a run is cancelled from the dashboard of the node running it.
- **Large schedulers.** The job list is searched, filtered, counted and paged on the server
  (`GET api/jobs/search?search=&type=&status=&page=&pageSize=`). For database stores it reads every job's
  type and trigger states in two queries, and loads full details only for the page shown (50 jobs), so
  thousands of jobs cost about as much as fifty. `GET api/jobs` still returns every job with full
  details, for scripts and small schedulers.
- **Polling** happens every 10 seconds (`RefreshSeconds`) only while the dashboard is visible: a tab in the background asks
  nothing, and refreshes as soon as it is shown again. The 24-hour failure count is refreshed once a minute.
- **Jobs from other apps** sharing the scheduler (types not loadable here) are listed but read-only.
- **Running jobs** are read from `QRTZ_FIRED_TRIGGERS` for ADO stores, so every cluster node is visible.
  For the in-memory store, or a custom connection provider, only this node is shown.
- **Errors** are returned as ProblemDetails with a trace id, never stack traces.
- **Security headers** are sent on every dashboard response: a Content-Security-Policy that allows only
  the dashboard's own files and API and no embedding in other sites (`frame-ancestors 'none'`, plus
  `X-Frame-Options: DENY`), `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. To
  embed the dashboard in your portal:
  `o.ContentSecurityPolicy = CronlightOptions.DefaultContentSecurityPolicy.Replace("frame-ancestors 'none'", "frame-ancestors https://portal.contoso.com");`
- **Cross-site requests** cannot change jobs. Requests that create, edit, pause, run or delete must
  send an `X-Cronlight-Request` header (any value), which the dashboard does and another site cannot,
  and browser requests marked `Sec-Fetch-Site: cross-site` are refused. Scripts calling the API add the
  header, e.g. `curl -X POST -H "X-Cronlight-Request: 1" …/api/jobs/DEFAULT/nightly/trigger`. Reads
  need no header.
- **Editing triggers** replaces the job's cron triggers; other trigger types are left alone. A paused
  trigger stays paused when its schedule changes.

## Repository layout

| Path | |
|---|---|
| `src/Cronlight` | The library (net8.0, net10.0). The built UI is embedded in the assembly |
| `src/Cronlight.History` | Optional execution history: listener, database and in-memory stores, schema scripts |
| `src/Cronlight.UI` | UI source: React, TypeScript, Vite, Tailwind and shadcn/ui. Built into `src/Cronlight/wwwroot` |
| `samples/Cronlight.SampleHost` | Host app using SQLite, with an HTTP job, demo endpoints and demo `X-Demo-Role` header auth |
| `tests/Cronlight.Tests` | Integration tests (TestServer; SQLite, PostgreSQL, SQL Server, MySQL) |
| `tests/Cronlight.SampleHost.Tests` | Tests for the sample's own code, such as the HTTP job's network rules |
| `tests/Cronlight.E2E` | End-to-end tests: Playwright drives the sample host in Chromium |
| `assets` | Logo (`icon.svg`) and the 128px `icon.png` used as the NuGet package icon |
| `build/verify-packages.sh` | Checks run on packed `.nupkg` files in CI |
| `build/smoke-test.sh` | Installs the packed packages into new .NET 8 and .NET 10 apps and runs them |
| `build/test-all.sh` | Runs the UI and .NET test suites |
| `.github/workflows` | CI and release pipelines |

## Development

Requires the .NET 10 SDK (see `global.json`; it also builds the net8.0 target) and Node.js 20+.

```bash
dotnet build              # also runs `npm ci` / `npm run build` for the UI when its sources changed
dotnet test

# Run the sample: http://localhost:5099/ops/jobs/
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5099 dotnet run --project samples/Cronlight.SampleHost

# UI with hot reload against the running sample: http://localhost:5173/
cd src/Cronlight.UI && npm run dev
```

`dotnet build -p:SkipDashboardUi=true` embeds an existing `wwwroot` without running npm.

### Running the UI and .NET tests

`build/test-all.sh` runs the UI lint, type-check and unit tests and the .NET tests on .NET 8 and 10,
installing npm packages when missing. A failing suite does not stop the rest; a summary at the end lists
each result. It does not run the end-to-end tests (see below).

```bash
build/test-all.sh                  # UI and .NET suites; databases as configured below
build/test-all.sh --databases      # also PostgreSQL, MySQL and SQL Server, in throwaway containers
build/test-all.sh ui dotnet        # only some suites
```

`--databases` needs Docker or Podman. It starts the same images as CI on local ports 25432, 23306 and
21433, and removes them afterwards. Connection strings already set (below) are used instead.

### Database tests

`dotnet test` always runs against SQLite. Tests for PostgreSQL, SQL Server and MySQL run when their
connection strings are set, and are skipped otherwise. With Docker or Podman:

```bash
podman run -d --name qd-postgres -e POSTGRES_PASSWORD=Test_pass1 -e POSTGRES_DB=quartz -p 127.0.0.1:15432:5432 postgres:17-alpine
podman run -d --name qd-mysql -e MYSQL_ROOT_PASSWORD=Test_pass1 -e MYSQL_DATABASE=quartz -p 127.0.0.1:13306:3306 mysql:8.4
podman run -d --name qd-mssql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Test_pass1!' -p 127.0.0.1:11433:1433 mcr.microsoft.com/mssql/server:2022-latest
podman exec qd-mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Test_pass1!' -Q "CREATE DATABASE quartz"

export QUARTZ_TEST_POSTGRES='Host=127.0.0.1;Port=15432;Database=quartz;Username=postgres;Password=Test_pass1'
export QUARTZ_TEST_SQLSERVER='Server=127.0.0.1,11433;Database=quartz;User Id=sa;Password=Test_pass1!;TrustServerCertificate=true'
export QUARTZ_TEST_MYSQL='Server=127.0.0.1;Port=13306;Database=quartz;User Id=root;Password=Test_pass1;AllowUserVariables=true'
dotnet test
```

The tests recreate the Quartz tables in these databases, so point them at throwaway instances only.

### UI tests

Unit and component tests for the UI use [Vitest](https://vitest.dev) and
[React Testing Library](https://testing-library.com/docs/react-testing-library/intro), with jsdom. They
sit next to the code they test (`*.test.ts`, `*.test.tsx`) and mock the API, so no server is needed.

```bash
cd src/Cronlight.UI
npm test                # once
npm run test:watch      # re-runs on save
npm run test:coverage   # report in coverage/
```

### End-to-end tests

[Playwright](https://playwright.dev) tests in `tests/Cronlight.E2E` build the sample host once, then
each worker starts its own on a fresh SQLite database, so test files run in parallel. The hosts refresh
the dashboard every second (`RefreshSeconds`) so tests wait less. The tests drive the dashboard in
Chromium: creating, editing, pausing, running and deleting jobs (several triggers and time zones,
manual-only jobs, secrets kept on edit, secret references), cron schedules firing on their own, bulk
pause and resume, search, filters and paging of the job list, the stat cards, the running jobs view and
cancelling a run, history (paging, job filter, failing and succeeding HTTP jobs), retries, running a
job with different data, running a failed run again, read-only jobs from other apps sharing the
scheduler, server errors, changes made elsewhere, a lost connection, viewer and anonymous access, the
scheduler details, the theme switch and the schedule assistant.
They need Node.js 22.13 or later, for its built-in SQLite module.

```bash
cd tests/Cronlight.E2E
npm ci
npx playwright install chromium   # once
npx playwright test               # builds and starts the sample hosts, on ports from 5199 (E2E_PORT)
E2E_WORKERS=1 npx playwright test # one worker per 4 GB and 2 cores by default, at most 4; E2E_SKIP_BUILD=1 skips the build
npx playwright show-report        # after a failure: traces and screenshots
```

### UI components

The UI uses [shadcn/ui](https://ui.shadcn.com) (Radix primitives, `radix-nova` style). Components live in
`src/Cronlight.UI/src/components/ui` and are owned by this repo; add new ones with
`npx shadcn@4.22.1 add <component>` from `src/Cronlight.UI`. Theme tokens (light and dark) are in
`src/index.css`. The theme switch defaults to the OS setting and remembers the choice per browser.

## CI/CD

GitHub Actions, with every third-party action pinned to a commit SHA (Dependabot keeps them current).

**CI** ([`ci.yml`](.github/workflows/ci.yml)) runs on pushes to `main` and on pull requests:

1. UI: ESLint, TypeScript and Prettier checks, and the Vitest unit and component tests.
2. Build in Release (which builds and embeds the UI).
3. Tests against SQLite, in-memory, and PostgreSQL, MySQL and SQL Server service containers.
   `CRONLIGHT_REQUIRE_DATABASES=true` makes a missing database fail the run rather than skip its tests.
4. Pack both packages as `0.1.0-ci.<run>` and verify them with `build/verify-packages.sh` (both target
   frameworks, UI embedded, README, license, symbols, matching dependency versions).
   `build/smoke-test.sh` then installs them into a new .NET 8 and .NET 10 web app, runs it and checks
   the dashboard, API and history respond.
5. Upload the packages and test results as workflow artifacts.
6. End-to-end tests (a separate job): Playwright runs `tests/Cronlight.E2E` against the sample host and
   uploads its report when a test fails.

**Release** ([`release.yml`](.github/workflows/release.yml)) runs when a `v*.*.*` tag is pushed:

```bash
git tag v0.1.0-beta.1 && git push origin v0.1.0-beta.1   # a -suffix marks a prerelease
```

It runs the full CI pipeline with the tag's version, then (after approval on the `nuget` environment)
pushes both packages and symbols to nuget.org and creates a GitHub release with generated notes.

One-time setup for releases:

1. On nuget.org, add a **Trusted Publishing** policy for this repository and the `release.yml` workflow.
   No API key is stored in GitHub: each release gets a short-lived key via OIDC.
2. In the GitHub repo settings, add a variable `NUGET_USER` with the nuget.org username
   (`mkokkerametla`).
3. Create an environment named `nuget` and add yourself as a required reviewer.
