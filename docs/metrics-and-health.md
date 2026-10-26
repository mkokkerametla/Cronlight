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
