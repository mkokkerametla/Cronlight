# Cluster status: design

Issue #19, first part. For a clustered scheduler, the header badge, the scheduler details and the health check
describe the scheduler instance in the dashboard's own process. A dashboard that manages schedulers other
processes run, and never starts them, shows **Stopped** while the cluster runs its jobs. This design makes them
describe the cluster. The second part of #19, which job types New job offers per scheduler, is a separate change.

## Decisions

| Question | Decision |
|---|---|
| Badge for a clustered scheduler | **Running · N node(s)** while any node is alive; **No nodes running** (amber) when none is |
| Whether this process runs it | Shown in the details, not the badge |
| Health check | Unchanged by default (per process, right for worker nodes). `AddCronlight(checkCluster: true)` checks the cluster instead, for management-only hosts |
| Where the nodes come from | A reader of `QRTZ_SCHEDULER_STATE`, returned with `api/scheduler` |
| Non-clustered schedulers | Unchanged: Running, Standby or Stopped, from this process, which is the scheduler |

## Reading the nodes

`ClusterNodesReader`, built like `RunningJobsReader`, reads `{prefix}SCHEDULER_STATE` through the same
`QuartzDataSource`, for the scheduler's `SCHED_NAME`. It reads only for a clustered database store Cronlight has
the connection details of: the host's own scheduler, or one added with its properties through `UseCronlight`.
Otherwise it returns null.

For each node it returns the instance name, the last check-in (Quartz stores UTC ticks), the check-in interval
(milliseconds), the seconds since that check-in by the server's clock, whether it is alive, and whether it is this
process's instance. A node is alive while its last check-in plus its interval plus Quartz's check-in misfire
threshold (7.5 s by default) has not passed: Quartz's own rule before it treats a node as failed. A process that never
starts its scheduler never checks in, so it is not in the table.

When the table cannot be read, the reader logs a warning and returns null, and the dashboard shows this process's
state as it does today. The status line never breaks the dashboard.

`api/scheduler` gains `clusterNodes`: the list, or null. One small query per refresh, on a table with a row per node.

## Header and details

The badge, when `clusterNodes` is not null: **Running · 2 nodes** (or **1 node**) with the green dot while any node is
alive; **No nodes running** with an amber dot when none is. When it is null, today's badge.

The details menu, when `clusterNodes` is not null:

- **Scheduler**: Version, Job store (clustered), Your access.
- **This process**: when it runs the scheduler, its Instance, State (Running or Standby), Threads, Jobs executed and
  Running since. When it does not: one line, "Not started (manages only)".
- **Nodes**: each node's instance name (shortened, full on hover), "checked in 3 sec. ago" from the server's count,
  a **this node** tag for this process, and **not responding** for a node past the alive rule. Quartz removes such
  a row once another node recovers its jobs, so these are short-lived unless every node is down. With no node
  checked in at all, the section says "None checked in".

When `clusterNodes` is null, today's details.

## Health check

`AddHealthChecks().AddCronlight(checkCluster: true)`: for a clustered scheduler this process has not started and
whose nodes can be read, healthy while any node is alive ("is running on 2 nodes"), degraded when none is ("has no
running nodes"). A scheduler that has shut down or whose job store cannot be read is unhealthy, as today. A
scheduler this process has started is still checked by its own state, so a worker whose scheduler is in standby is
not hidden by the others and the option is safe on the nodes too (decided in review). A scheduler that is not
clustered, or whose nodes cannot be read, is checked per process as today. Without `checkCluster`, nothing changes.

## Edge cases

- Clocks: alive and "seconds since check-in" use the server's clock, as Quartz uses each node's, never the browser's.
- Every node crashed: their rows stay until a node recovers them, so they show as not responding and the badge says
  No nodes running, which is so.
- This process is a node in standby: the badge describes the cluster; its State row says Standby.
- Several schedulers: each has its own nodes, by `SCHED_NAME`.

## Testing

- .NET, on PostgreSQL, SQL Server and MySQL (Quartz cannot cluster on SQLite), through `api/scheduler` and the
  health check:
  - A started clustered host lists itself, alive and this node.
  - A host that never starts its scheduler, with node rows the test inserts: an alive and a stale node, and another
    scheduler's node left out. Rows are inserted only for hosts that never start, since a started node would
    recover a stale row and delete it.
  - SQLite and in-memory stores return null.
  - Health check with `checkCluster`: healthy with an alive node though this process never started, degraded with
    only stale nodes; without it, "has not started" as today.
- UI, with the API mocked: the badge for 2 nodes, 1 node and none, and today's badge when the nodes are null; the
  details with the node list and its tags, and "Not started (manages only)".
- No end-to-end test: the sample host uses SQLite, which cannot cluster.

## Docs

`docs/behaviour.md` for the badge and details; `docs/metrics-and-health.md` for `checkCluster` and when a
management-only host needs it.
