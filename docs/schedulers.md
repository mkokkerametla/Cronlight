# Several schedulers in one app

Serving schedulers the app builds in code, with permissions per scheduler.

Cronlight serves the scheduler from `AddQuartz`. An app that builds more schedulers in code (one per team,
or per database) adds each one with `UseCronlight`, before starting it:

```csharp
var reports = await new StdSchedulerFactory(properties).GetScheduler();
// Jobs with constructor dependencies; wrapped so history keeps what each run logs (needs AddCronlightHistory)
reports.JobFactory = app.Services.GetRequiredService<IJobFactory>().WithCronlightRunLogs(app.Services);
await reports.UseCronlight(app.Services); // dashboard, history, alerts, retries and secret references
await reports.Start();
```

The dashboard then shows a scheduler picker in its header, and the API takes `?scheduler=<name>` on every
endpoint (without it, requests go to the `AddQuartz` scheduler, so existing scripts keep working). The
choice is kept in the page's URL, so a link opens the same scheduler. Each scheduler has its own jobs,
history and failure streaks, and audit events and alerts name the scheduler.

- Only schedulers added this way are served. Quartz keeps every scheduler in the process in one registry,
  which can include other apps' schedulers; Cronlight leaves those alone, and asking for one returns 404.
- Cronlight reads the database tables of the `AddQuartz` scheduler directly. For another scheduler with a
  database store, pass the properties it was built from, `await reports.UseCronlight(app.Services, properties)`,
  and its tables are read the same way. Without them it is read through Quartz's API: its job list loads
  each job, which suits hundreds of jobs rather than thousands, and its Running tab shows this node's runs
  only.
- History of every scheduler goes into the one history table (`SCHED_NAME` tells them apart), and
  `Retention` applies to each.
- **Permissions per scheduler** come on top of `ViewPolicy` and `ManagePolicy`:
  `o.Schedulers["Billing"] = new() { ViewPolicy = "BillingTeam", ManagePolicy = "BillingAdmins" }`. A user
  without its view policy does not see it in the picker and gets 403 from its API; one without its manage
  policy (falling back to its view policy) sees it read-only. Schedulers not listed are open to everyone
  the dashboard admits. Refusals are audited.

The sample host has a second, in-memory scheduler named Reports (`samples/Cronlight.SampleHost/ReportsScheduler.cs`).
