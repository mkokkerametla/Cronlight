<img src="assets/icon.png" alt="" width="64" height="64" align="left">

# Cronlight

[![CI](https://github.com/mkokkerametla/Cronlight/actions/workflows/ci.yml/badge.svg)](https://github.com/mkokkerametla/Cronlight/actions/workflows/ci.yml)

**A spotlight on your Quartz.NET jobs.** Cronlight is a dashboard and API for
[Quartz.NET](https://www.quartz-scheduler.net/) that you add to the ASP.NET Core app already running your
scheduler: list, create, edit, pause, resume, run and delete jobs, and see what is running and what failed.

It uses the scheduler you already configured. It needs no connection string or schema of its own and never starts a
second scheduler.

**[Website](https://cronlight.pages.dev/) · [Live demo](https://cronlight.pages.dev/demo/) ·
[Documentation](https://cronlight.pages.dev/docs/)**

## Features

- **Jobs:** search, filter and page through thousands of jobs; create and edit them with cron, one-time and interval
  triggers; pause or resume a job, a group or every job; run one now, or once with different data; cancel a run.
- **Job types:** attributes give your `IJob` types a name, a form with typed and required parameters, and masked
  secrets, which can be kept out of the database as references to configuration.
- **Execution history** (optional package): every run with its outcome, error and what it logged, a running job's
  log so far, misfires and late starts, and run statistics: success rate and average and P95 duration, per job and
  over a period.
- **Retries:** failed runs retried automatically, as set per job type or per job.
- **Alerts:** Slack, Teams, webhook or email when a job fails, recovers or is stopped by Quartz.
- **Export and import:** jobs as JSON or YAML files, to move them between environments or keep them in source
  control; an import says what it will change before changing anything.
- **Operations:** an audit log of every change, OpenTelemetry metrics, a health check, and an OpenAPI description of
  the API.
- **Several schedulers** in one app, each with its own permissions.
- **AI assistants** (optional package): an MCP server, so Claude, Copilot or any MCP client can read jobs, runs and
  logs, and if you allow it pause, resume and run jobs, under the dashboard's permissions and audit log.

## Requirements

| | |
|---|---|
| .NET | ASP.NET Core 10 (`net10.0`). Hosts on .NET 8 can use 0.1.0-beta.2 |
| Quartz.NET | 3.13.1 or a later 3.x, set up with `AddQuartz()`; tested on 3.22.7. Quartz 4 is not supported yet |
| Job store | Any. Execution history is kept in the Quartz database on SQL Server, PostgreSQL, MySQL or SQLite, and in memory otherwise |

Until 1.0, a minor version (0.2, 0.3) may change options and endpoints, and its release notes say how; a patch
version (0.1.1) does not.

## Install

```bash
dotnet add package Cronlight.Quartz.Dashboard
dotnet add package Cronlight.Quartz.Dashboard.History   # optional: execution history
dotnet add package Cronlight.Quartz.Dashboard.Mcp       # optional: an MCP server for AI assistants
```

The setup below also calls `AddQuartzHostedService()`, from `Quartz.Extensions.Hosting` (or `Quartz.AspNetCore`).
Cronlight does not install it, since your app owns its scheduler setup.

## Quick start

```csharp
// 1. Your existing Quartz setup stays exactly as it is
builder.Services.AddQuartz(q =>
{
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;
        s.UseSqlServer(builder.Configuration.GetConnectionString("Quartz")!);
        s.UseNewtonsoftJsonSerializer();
    });
});
builder.Services.AddQuartzHostedService();

// 2. Add Cronlight, and the optional features you want
builder.Services.AddCronlight(o =>
{
    o.ViewPolicy = "QuartzViewers";   // required outside Development
    o.ManagePolicy = "QuartzAdmins";  // create, edit, pause, run and delete
});
builder.Services.AddCronlightHistory();                                                   // execution history
builder.Services.AddCronlightAlerts(o => builder.Configuration.GetSection("CronlightAlerts").Bind(o)); // alerts
builder.Services.AddHealthChecks().AddCronlight();                                       // health check

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// 3. Map it under any path
app.MapCronlight("/ops/jobs");
```

Open `/ops/jobs/`. The API is at `/ops/jobs/api/*`, described by an OpenAPI document at `/ops/jobs/api/openapi.json`.

- **Access:** without a `ViewPolicy`, the dashboard answers 403 outside the Development environment.
- **Scripts:** requests that change jobs must send an `X-Cronlight-Request` header (any value), which keeps other
  sites from using a viewer's cookies:
  `curl -X POST -H "X-Cronlight-Request: 1" https://…/ops/jobs/api/jobs/DEFAULT/nightly/trigger`
- **Job types:** any `IJob` in your app can be created from the dashboard. Attributes give one a proper form:

  ```csharp
  [CronlightJob("Send invoices", Description = "Emails pending invoices for a customer")]
  [JobParameter("customerId", Type = JobParameterType.Int, Required = true)]
  [JobParameter("smtpPassword", Type = JobParameterType.Secret)]
  public class InvoiceJob : IJob { ... }
  ```

## Limitations

- Quartz.NET 3.x only: Quartz 4 is not supported yet.
- The dashboard refreshes by polling (every 10 seconds by default) rather than live, and a run's log shows once the
  run has finished.
- Secret values are masked in the dashboard but stored in the Quartz tables as plain text, unless you use secret
  references.
- A run can be cancelled only from the node running it, and only if the job observes its cancellation token.
- Jobs of other apps sharing the scheduler (types this app cannot load) are read-only.
- Schedulers your app builds in code are read through Quartz's API unless you give Cronlight their properties: that
  suits hundreds of jobs rather than thousands, and their Running tab shows this node only.
- With the in-memory job store, or a database history does not support, history is kept in memory on each node and
  lost on restart.
- Alert failure streaks start again when the app restarts. Email alerts need STARTTLS or a plain connection, not
  implicit TLS on port 465.

## Documentation

- [Setup and options](https://github.com/mkokkerametla/Cronlight/blob/main/docs/configuration.md)
- [Job types and secrets](https://github.com/mkokkerametla/Cronlight/blob/main/docs/job-types.md)
- [Retries and re-running](https://github.com/mkokkerametla/Cronlight/blob/main/docs/retries.md)
- [Execution history](https://github.com/mkokkerametla/Cronlight/blob/main/docs/history.md)
- [Failure alerts](https://github.com/mkokkerametla/Cronlight/blob/main/docs/alerts.md)
- [Exporting and importing jobs](https://github.com/mkokkerametla/Cronlight/blob/main/docs/export-import.md)
- [AI assistants (MCP server)](https://github.com/mkokkerametla/Cronlight/blob/main/docs/mcp.md)
- [Audit log](https://github.com/mkokkerametla/Cronlight/blob/main/docs/audit.md)
- [Metrics and health checks](https://github.com/mkokkerametla/Cronlight/blob/main/docs/metrics-and-health.md)
- [Several schedulers in one app](https://github.com/mkokkerametla/Cronlight/blob/main/docs/schedulers.md)
- [Scheduling API calls (HTTP job)](https://github.com/mkokkerametla/Cronlight/blob/main/docs/http-job.md)
- [Behaviour worth knowing](https://github.com/mkokkerametla/Cronlight/blob/main/docs/behaviour.md)
- [Development](https://github.com/mkokkerametla/Cronlight/blob/main/docs/development.md) and the
  [roadmap](https://github.com/mkokkerametla/Cronlight/blob/main/docs/ROADMAP.md)

## License

MIT. See [LICENSE](https://github.com/mkokkerametla/Cronlight/blob/main/LICENSE).
