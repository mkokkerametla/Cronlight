# Metrics and health checks

OpenTelemetry metrics for every run, and a health check per scheduler.

Cronlight publishes OpenTelemetry metrics on the `Cronlight` meter (`CronlightMetrics.MeterName`), for every
scheduler it serves, with or without the dashboard mapped:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(CronlightMetrics.MeterName).AddPrometheusExporter());
```

| Instrument | Type | |
|---|---|---|
| `cronlight.job.runs` | counter | Finished runs, by `cronlight.run.outcome`: succeeded, failed, retried, cancelled or vetoed |
| `cronlight.job.run.duration` | histogram (s) | How long runs took |
| `cronlight.job.runs.active` | up-down counter | Runs in progress |
| `cronlight.job.misfires` | counter | Fire times missed by more than the misfire threshold |

A job Quartz could not create counts as a failed run, with no duration, and is never active.

Every instrument is tagged with `cronlight.scheduler`, `cronlight.job.group` and `cronlight.job.name`. Each
node reports its own runs, so add them up across a cluster. A failure that will be retried counts as
`retried`, as history shows it.

A health check reports each scheduler:

```csharp
builder.Services.AddHealthChecks().AddCronlight();
app.MapHealthChecks("/healthz");
```

It is unhealthy when a scheduler has shut down or its job store cannot be read (for a database store, a
query on the Quartz tables, within 10 seconds), and degraded while one has not started yet or is in
standby. `AddCronlight(failureStatus: HealthStatus.Degraded, tags: ["ready"], timeout: …)` changes those.

A host that manages schedulers other processes run, and never starts them, would read as degraded ("has not
started"). `AddCronlight(checkCluster: true)` checks such a scheduler by its cluster's nodes instead: healthy while
any node is alive ("is running on 2 nodes"), degraded when none is ("has no running nodes"). A node counts as alive
by Quartz's own rule: it checked in within its check-in interval plus the cluster's check-in misfire threshold. This
applies to a clustered scheduler this process has not started, whose connection Cronlight has (the host's own, or
one added with its properties through `UseCronlight`); a scheduler this process runs is still checked by its own
state, so the option is safe on the nodes too.
