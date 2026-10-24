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
| `ShowScheduleAssistant` | `true` | Show the "describe your schedule" box that opens the question in ChatGPT or Google. Set to `false` where staff may not use external AI or search tools |
| `SensitiveKeyPatterns` | password, secret, token, apikey, connectionstring | JobDataMap keys masked in responses |

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

## Scheduling API calls (HTTP job)

The sample includes [`HttpJob`](samples/Cronlight.SampleHost/HttpJob.cs), a job type for calling HTTP APIs
on a schedule: URL, method, headers, body, an `authorization` header kept as a masked secret, timeout and
expected status codes (`2xx`, `200,202`, ...). A response with any other status fails the run, so it shows
up in execution history.

Copy it into your app and register it:

```csharp
builder.Services.AddHttpClient(HttpJob.ClientName);
builder.Services.Configure<HttpJobOptions>(o => o.AllowedHosts.AddRange(["api.partner.com", "hooks.internal"]));
```

Restrict `AllowedHosts`: otherwise anyone who can create jobs can make your server call any URL,
including internal services.

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
- **Jobs are never slowed down.** Listeners only queue records; a background writer stores them. If the
  database is unavailable, records beyond `QueueCapacity` are dropped and jobs carry on.
- **Errors** are stored as exception type and message (truncated to `MaxErrorLength`), never stack traces.
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

## Behaviour worth knowing

- **Secrets** are shown as `********`. Saving a masked value back keeps the stored value.
- **Jobs from other apps** sharing the scheduler (types not loadable here) are listed but read-only.
- **Running jobs** are read from `QRTZ_FIRED_TRIGGERS` for ADO stores, so every cluster node is visible.
  For the in-memory store, or a custom connection provider, only this node is shown.
- **Errors** are returned as ProblemDetails with a trace id, never stack traces.
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
| `assets` | Logo (`icon.svg`) and the 128px `icon.png` used as the NuGet package icon |
| `build/verify-packages.sh` | Checks run on packed `.nupkg` files in CI |
| `build/smoke-test.sh` | Installs the packed packages into new .NET 8 and .NET 10 apps and runs them |
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

### UI components

The UI uses [shadcn/ui](https://ui.shadcn.com) (Radix primitives, `radix-nova` style). Components live in
`src/Cronlight.UI/src/components/ui` and are owned by this repo; add new ones with
`npx shadcn@4.22.1 add <component>` from `src/Cronlight.UI`. Theme tokens (light and dark) are in
`src/index.css`. The theme switch defaults to the OS setting and remembers the choice per browser.

## CI/CD

GitHub Actions, with every third-party action pinned to a commit SHA (Dependabot keeps them current).

**CI** ([`ci.yml`](.github/workflows/ci.yml)) runs on pushes to `main` and on pull requests:

1. UI: ESLint, TypeScript and Prettier checks.
2. Build in Release (which builds and embeds the UI).
3. Tests against SQLite, in-memory, and PostgreSQL, MySQL and SQL Server service containers.
   `CRONLIGHT_REQUIRE_DATABASES=true` makes a missing database fail the run rather than skip its tests.
4. Pack both packages as `0.1.0-ci.<run>` and verify them with `build/verify-packages.sh` (both target
   frameworks, UI embedded, README, license, symbols, matching dependency versions).
   `build/smoke-test.sh` then installs them into a new .NET 8 and .NET 10 web app, runs it and checks
   the dashboard, API and history respond.
5. Upload the packages and test results as workflow artifacts.

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
