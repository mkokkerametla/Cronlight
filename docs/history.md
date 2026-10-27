# Execution history

The optional package that records every run, what it logged, misfires and late starts.

`Cronlight.Quartz.Dashboard.History` records every run (start, finish, duration, node, outcome, error message and
what the job logged) and adds a History tab, a "Failed (24h)" card and recent runs per job to the dashboard.

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
  Indexes and columns added in later versions (`ATTEMPT` for retries, `DATA` for each run's own data, `RUN_LOG`
  for what runs log) are added to an existing
  table at startup; with `CreateTable = false` a missing one is logged as a warning with the statement to
  run, and history is written without it. The same happens when adding one fails (the app may not alter the
  table, or a large table takes longer than the command timeout), so history keeps working, and the step is tried
  again every `CleanupInterval`. These checks look
  in the schema or database `TableName` names, if it names one.
- **Jobs are never slowed down.** Listeners only queue records; a background writer stores them. While the
  database is unavailable, records wait in the queue and are written once it is back; records beyond
  `QueueCapacity` are dropped, and jobs carry on. A record the database refuses while it is otherwise working is
  skipped, so it cannot hold up the rest. An app stopping during an outage does not wait for it.
- **Errors** are stored as exception type and message (truncated to `MaxErrorLength`), never stack traces.
- **What each run logs** through `ILogger` is kept with it, from the job's own code and from anything it
  calls or starts (an `HttpClient`, a repository), and shown under "log" in the History tab. Entries are kept
  from `MinimumLogLevel` (Information) up, and the host's logging filters apply too, under the provider alias
  `CronlightRunLog` (e.g. `Logging:CronlightRunLog:LogLevel:Default`). A run keeps its first `MaxLogEntries`
  (200), each up to `MaxLogMessageLength` characters, and says how many it left out. Exceptions are kept as
  type and message. The run's sensitive job data values (secret parameters and keys matching
  `SensitiveKeyPatterns`, resolved secret references included) are masked wherever they were logged, before
  a long message is cut; other
  secrets a job logs are kept as logged, so treat run logs like your other logs.
  - Logs are read one run at a time (`GET api/history/{id}/log`); the history list only says whether a run
    has one (`hasLog`).
  - Cronlight wraps the host's job factory to do this, so `context.JobInstance` is Cronlight's wrapper
    around the job. `CaptureLogs = false` turns it off. A factory set only through Quartz properties
    (`quartz.scheduler.jobFactory.type`) rather than `UseJobFactory` is left alone, with a warning. A scheduler built in code keeps logs once its job
    factory is wrapped with `WithCronlightRunLogs` ([several schedulers](schedulers.md)).
  - Logging that the job leaves running after it returns is not kept.
- **Misfires.** When a trigger misses its fire time by more than Quartz's misfire threshold (60 seconds by
  default; the scheduler was down, paused or had no free thread), history records it as `Misfired`: at the
  fire time it missed, with how many fire times were missed and what Quartz does about it, e.g. "Missed 3
  fire times, the first by 2h 5m; Quartz runs it once now (misfire instruction FireOnceNow)." A run that
  follows appears as a run of its own, scheduled at the fire time it makes up for, so it shows how late it
  started (when the misfire was handled on the node that runs it). The count ignores the trigger's calendar,
  so times a calendar excludes are counted as missed too. Since Quartz 3.17, a repeating trigger that has never
  fired starts now when it is scheduled with a start in the past, so it runs rather than misfires.
- **Refires.** A job that fails with a `JobExecutionException` whose `RefireImmediately` is set runs again at
  once; each attempt is a record of its own, with its refire count. Quartz 3.17 and later no longer tell
  listeners when such an attempt ends, so Cronlight records it as failed when the next attempt begins, with
  what it threw while it captures logs (`CaptureLogs`), and a generic message otherwise.
- **Jobs Quartz could not create.** When a job's constructor or the job factory throws (a dependency that is not
  registered, say), Quartz never runs the job and stops all its triggers until it is resumed (see
  [jobs in Error](behaviour.md)). History records it as failed, "Not run: the job could not be created",
  with what was thrown.
- **Runs that never finished.** When the app stops, history is written until its queue is empty, so a run that ends
  meanwhile is recorded as it ended. A run still going after that (Quartz does not wait for running jobs unless
  `WaitForJobsToComplete` is set) is recorded as failed: "Did not finish: the app stopped while it was running."
  For a scheduler whose database Cronlight reads, every `UnfinishedRunCheckInterval` each node also looks for runs
  still Running in history that no node is running any more, because the node running them crashed, and records
  them as failed: "Did not finish: no end was recorded, and no node is running it any more". Runs that started in
  the last 5 minutes are left alone, a run is closed only while it is still Running, and if its own end arrives
  later, it replaces this. Your own `IJobHistoryStore` is left alone. These runs raised no alert, so failure
  streaks read from history neither count them nor end at them.
- **Late starts.** Any run that started 5 seconds or more after its scheduled time is marked "started …
  late" in History and in the job's recent runs.
- **What a trigger does when it misses a run** shows on the job's page. Cron triggers made in the dashboard
  run once as soon as they can (Quartz's default), or, with "Skip missed runs", wait for the next fire time. Triggers with `IgnoreMisfirePolicy` fire every missed time instead
  and are not reported as misfires. Filter the History tab by *Misfired* to see them all.
- **Retention** is applied hourly by every node and only touches its own scheduler's rows, so apps sharing
  a database keep their own retention.
- **Your own store:** register an `IJobHistoryStore` before calling `AddCronlightHistory`. Its `Query` should honour
  every filter of `JobHistoryQuery`, including `FiredFromUtc` and `FiredBeforeUtc`: the MCP server's analytics read a
  window of runs with them. A store that ignores them still works, but reads every newer run first, so an answer about
  a window long past can come back truncated.

| Option | Default | |
|---|---|---|
| `Storage` | `Auto` | `Auto`, `Database` (fail if the database can't be used) or `InMemory` |
| `TableName` | prefix + `CRONLIGHT_HISTORY` | |
| `CreateTable` | `true` | |
| `Retention` | 14 days | `null` keeps everything |
| `CleanupInterval` | 1 hour | How often retention is applied, and a schema upgrade step that failed is tried again |
| `UnfinishedRunCheckInterval` | 5 minutes | How often runs left Running by a node that stopped are looked for; zero turns it off |
| `InMemoryCapacity` | 5000 | Records kept per node by the in-memory store |
| `QueueCapacity` | 10000 | |
| `MaxErrorLength` | 2000 | |
| `CaptureLogs` | `true` | Keep what each run logs |
| `MinimumLogLevel` | `Information` | Least severe log entries kept |
| `MaxLogEntries` | 200 | Log entries kept per run |
| `MaxLogMessageLength` | 1000 | |
