# Cluster Status Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** For a clustered scheduler, the header badge, scheduler details and (opt-in) health check describe the
cluster's nodes, so a dashboard that manages schedulers other processes run stops showing "Stopped" (issue #19).

**Architecture:** `ClusterNodesReader` reads `{prefix}SCHEDULER_STATE` through the existing `QuartzDataSource`, the
way `RunningJobsReader` reads `FIRED_TRIGGERS`. `api/scheduler` returns its nodes as `clusterNodes` (null when not
clustered or not readable). The header derives its badge and details from them. The health check uses the reader
when the host passes `checkCluster: true`. Design: `docs/plans/2026-10-26-cluster-status-design.md`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Quartz.NET 3.x ADO job store, xUnit (`SkippableTheory` over
PostgreSQL, SQL Server, MySQL); React 19 + TypeScript, Vitest + Testing Library.

**Conventions:** comments sparse and say why; test names are plain sentences (.NET: `Words_with_underscores`).
UI: `npx prettier --write <files>` and `npm run lint` clean (run from `src/Cronlight.UI`). .NET: `dotnet build`
clean. Commit messages are a plain sentence, ending with:

```
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_011F74GC6ubgQdyaPz2xomg2
```

**Running the database tests:** Quartz cannot cluster on SQLite, so the cluster tests need PostgreSQL, SQL Server or
MySQL. From the repo root in Git Bash, `build/test-all.sh --databases dotnet` starts them in Docker and runs the .NET
tests (about a minute). To run only the new class, start them as that script does and run
`dotnet test tests/Cronlight.Tests --filter FullyQualifiedName~ClusterTests` with `QUARTZ_TEST_POSTGRES`,
`QUARTZ_TEST_SQLSERVER` and `QUARTZ_TEST_MYSQL` set (see `docs/development.md`). Without them the tests skip, which
is not a pass. No sample host may be running from this checkout while .NET builds (it locks the DLLs).

---

### Task 1: A test host that never starts its scheduler

A management-only dashboard builds the scheduler but never starts it. The tests need such a host.

**Files:**
- Modify: `tests/Cronlight.Tests/TestHost.cs`
- Modify: `tests/Cronlight.Tests/HistoryTests.cs:740-751` (`DatabaseTests.Databases` and `Require`, to share them)

**Step 1: `TestHost.Start` gains `bool startScheduler = true`** (last parameter). When false:
- do not call `builder.Services.AddQuartzHostedService()`;
- do not call `WaitForScheduler`;
- keep the flag on the instance (`private readonly bool startedScheduler`, set through the constructor).

In `DisposeAsync`, before stopping the app, shut down a scheduler nothing started, since no hosted service will:

```csharp
    public async ValueTask DisposeAsync()
    {
        // Nothing else shuts down a scheduler the host never started, and Quartz keeps it in a process-wide registry
        if (!startedScheduler)
            await (await Scheduler()).Shutdown();
        await Within(app.StopAsync(), $"Test host {SchedulerName} did not stop");
        await Within(app.DisposeAsync().AsTask(), $"Test host {SchedulerName} was not disposed");
    }
```

**Step 2: share the database list.** In `HistoryTests.cs`, class `DatabaseTests`, make `Databases` stay public and
`Require` `internal static` (it is `private static`), so `ClusterTests` can use them.

**Step 3:** `dotnet build tests/Cronlight.Tests`, expect success, then run
`dotnet test tests/Cronlight.Tests --filter FullyQualifiedName~MetricsTests` (SQLite) to check started hosts still
work. Expect PASS.

**Step 4: Commit**: "Let a test host build its scheduler without starting it"

---

### Task 2: `ClusterNodesReader` and `clusterNodes` in `api/scheduler`

**Files:**
- Create: `src/Cronlight/ClusterNodesReader.cs`
- Modify: `src/Cronlight/CronlightExtensions.cs:32` (register next to `RunningJobsReader`)
- Modify: `src/Cronlight/DashboardApi.cs:224-253` (the `/scheduler` endpoint)
- Test: `tests/Cronlight.Tests/ClusterTests.cs` (create)

**Step 1: Write the failing tests** in `tests/Cronlight.Tests/ClusterTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace Cronlight.Tests;

/// <summary>
/// Clustered schedulers, on the databases Quartz can cluster on (not SQLite). Node rows are inserted only for hosts
/// that never start their scheduler: a started node recovers a stale node's row and deletes it.
/// </summary>
public class ClusterTests
{
    public static TheoryData<string> Databases => DatabaseTests.Databases;

    /// <summary>A node of the host's scheduler (or another one), as Quartz writes it: check-in in UTC ticks, interval in milliseconds.</summary>
    private static Task Node(TestHost host, string instance, TimeSpan checkedInAgo, string? scheduler = null) =>
        host.Database.Execute(
            "INSERT INTO QRTZ_SCHEDULER_STATE (SCHED_NAME, INSTANCE_NAME, LAST_CHECKIN_TIME, CHECKIN_INTERVAL) " +
            $"VALUES ('{scheduler ?? host.SchedulerName}', '{instance}', {(DateTimeOffset.UtcNow - checkedInAgo).UtcTicks}, 7500)");

    private static Task ForgetNodes(TestHost host) =>
        host.Database.Execute($"DELETE FROM QRTZ_SCHEDULER_STATE WHERE SCHED_NAME LIKE '{host.SchedulerName}%'");

    private static async Task<JsonElement> ClusterNodes(TestHost host) =>
        (await host.Client().GetFromJsonAsync<JsonElement>("api/scheduler")).GetProperty("clusterNodes");

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task A_clustered_scheduler_lists_its_node_as_this_one_and_alive(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true);
        // The node checks in as the scheduler starts; allow for that to land
        var nodes = await ClusterNodes(host);
        for (var deadline = DateTime.UtcNow.AddSeconds(15); nodes.GetArrayLength() == 0 && DateTime.UtcNow < deadline; await Task.Delay(200))
            nodes = await ClusterNodes(host);

        var node = Assert.Single(nodes.EnumerateArray());
        Assert.Equal((await host.Scheduler()).SchedulerInstanceId, node.GetProperty("instanceId").GetString());
        Assert.True(node.GetProperty("alive").GetBoolean());
        Assert.True(node.GetProperty("thisNode").GetBoolean());
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task A_dashboard_that_never_starts_the_scheduler_lists_the_nodes_that_run_it(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false);
        try
        {
            await Node(host, "worker-a", TimeSpan.FromSeconds(2));
            await Node(host, "worker-b", TimeSpan.FromMinutes(5));
            await Node(host, "elsewhere", TimeSpan.FromSeconds(2), scheduler: $"{host.SchedulerName}-other");

            var info = await host.Client().GetFromJsonAsync<JsonElement>("api/scheduler");

            Assert.False(info.GetProperty("started").GetBoolean());
            var nodes = info.GetProperty("clusterNodes").EnumerateArray()
                .ToDictionary(n => n.GetProperty("instanceId").GetString()!);
            Assert.Equal(["worker-a", "worker-b"], nodes.Keys.Order());
            Assert.True(nodes["worker-a"].GetProperty("alive").GetBoolean());
            Assert.False(nodes["worker-b"].GetProperty("alive").GetBoolean());
            Assert.InRange(nodes["worker-b"].GetProperty("secondsSinceCheckin").GetInt64(), 295, 330);
            Assert.All(nodes.Values, n => Assert.False(n.GetProperty("thisNode").GetBoolean()));
        }
        finally
        {
            await ForgetNodes(host);
        }
    }

    [Fact]
    public async Task A_scheduler_that_is_not_clustered_lists_no_nodes()
    {
        await using var sqlite = await TestHost.Start();
        await using var memory = await TestHost.Start(ramJobStore: true);

        Assert.Equal(JsonValueKind.Null, (await ClusterNodes(sqlite)).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ClusterNodes(memory)).ValueKind);
    }
}
```

**Step 2: Run them to check they fail**

Run: `dotnet test tests/Cronlight.Tests --filter FullyQualifiedName~ClusterTests` with the databases set.
Expected: FAIL, `clusterNodes` is not in the response (`KeyNotFoundException`), on every database and for the
`[Fact]`. If the theories report Skipped, the connection strings are not set: set them, do not carry on.

**Step 3: Write `src/Cronlight/ClusterNodesReader.cs`**

```csharp
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cronlight;

/// <param name="SecondsSinceCheckin">By the server's clock, so a browser with a wrong clock does not misread it.</param>
/// <param name="Alive">Checked in within its interval plus Quartz's allowance, so Quartz does not take it for failed.</param>
/// <param name="ThisNode">This process's scheduler instance; a process that never starts its scheduler is never listed.</param>
public record ClusterNodeDto(
    string InstanceId,
    DateTimeOffset LastCheckinUtc,
    long CheckinIntervalMs,
    long SecondsSinceCheckin,
    bool Alive,
    bool ThisNode
);

/// <summary>
/// The nodes of a clustered scheduler, from SCHEDULER_STATE, where each node checks in. Quartz's API describes only
/// this process's instance, which says nothing about a cluster that other processes run.
/// </summary>
internal sealed class ClusterNodesReader(QuartzDataSource hostDataSource, CronlightSchedulers schedulers, ILogger<ClusterNodesReader> logger)
{
    // Quartz's allowance past a node's check-in interval before it treats the node as failed
    private static readonly TimeSpan Allowance = TimeSpan.FromMilliseconds(7500);

    /// <summary>Null unless the scheduler is clustered on a database store Cronlight can read, and when that read fails.</summary>
    public async Task<IReadOnlyList<ClusterNodeDto>?> Read(IScheduler scheduler, CancellationToken cancellationToken)
    {
        var meta = await scheduler.GetMetaData(cancellationToken);
        if (!meta.JobStoreClustered || await schedulers.DataSource(scheduler, hostDataSource, cancellationToken) is not { } dataSource)
            return null;
        try
        {
            return await ReadNodes(dataSource, meta.SchedulerName, meta.SchedulerInstanceId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or SchedulerException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not read {Prefix}SCHEDULER_STATE, so this node's state is shown instead", dataSource.TablePrefix);
            return null;
        }
    }

    private static async Task<IReadOnlyList<ClusterNodeDto>> ReadNodes(QuartzDataSource dataSource, string schedulerName,
        string instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnection(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT INSTANCE_NAME, LAST_CHECKIN_TIME, CHECKIN_INTERVAL
            FROM {dataSource.TablePrefix}SCHEDULER_STATE
            WHERE SCHED_NAME = {dataSource.Parameter("schedName")}
            ORDER BY INSTANCE_NAME
            """;
        dataSource.AddParameter(command, "schedName", schedulerName);

        var now = DateTimeOffset.UtcNow;
        var nodes = new List<ClusterNodeDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var instance = reader.GetString(0);
            // Quartz stores the check-in as UTC ticks and the interval in milliseconds
            var checkin = new DateTimeOffset(Convert.ToInt64(reader.GetValue(1)), TimeSpan.Zero);
            var interval = Convert.ToInt64(reader.GetValue(2));
            nodes.Add(new ClusterNodeDto(
                instance,
                checkin,
                interval,
                (long)Math.Max(0, (now - checkin).TotalSeconds),
                checkin + TimeSpan.FromMilliseconds(interval) + Allowance >= now,
                instance == instanceId
            ));
        }
        return nodes;
    }
}
```

**Step 4: Register and return it**

- `CronlightExtensions.cs`, after `services.TryAddSingleton<RunningJobsReader>();`:
  `services.TryAddSingleton<ClusterNodesReader>();`
- `DashboardApi.cs`, the `/scheduler` endpoint: add `ClusterNodesReader cluster` and `CancellationToken ct` to the
  lambda's parameters, and after `meta.RunningSince,` add:

```csharp
                // The cluster's nodes, for a dashboard whose own process may not run the scheduler at all
                ClusterNodes = await cluster.Read(scheduler, ct),
```

**Step 5: Run the tests to check they pass**, on all three databases (none skipped). If the started host's node
is never alive, check the units: `LAST_CHECKIN_TIME` should be ticks near `DateTimeOffset.UtcNow.UtcTicks`.

**Step 6: Commit**: "Read a clustered scheduler's nodes and return them with the scheduler"

---

### Task 3: The health check's cluster mode

**Files:**
- Modify: `src/Cronlight/HealthCheck.cs`
- Test: `tests/Cronlight.Tests/ClusterTests.cs`

**Step 1: Write the failing tests** (add `using Microsoft.Extensions.DependencyInjection;` and
`using Microsoft.Extensions.Diagnostics.HealthChecks;`)

```csharp
    private static async Task<HealthReportEntry> Health(TestHost host) =>
        (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["cronlight"];

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Checking_the_cluster_a_host_that_never_starts_the_scheduler_is_healthy_while_a_node_runs_it(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false,
            services: s => s.AddHealthChecks().AddCronlight(checkCluster: true));
        try
        {
            await Node(host, "worker-a", TimeSpan.FromSeconds(2));
            var running = await Health(host);
            Assert.Equal(HealthStatus.Healthy, running.Status);
            Assert.Equal("is running on 1 node", running.Data[host.SchedulerName]);

            await ForgetNodes(host);
            await Node(host, "worker-a", TimeSpan.FromMinutes(5));
            var down = await Health(host);
            Assert.Equal(HealthStatus.Degraded, down.Status);
            Assert.Equal($"{host.SchedulerName} has no running nodes", down.Description);
        }
        finally
        {
            await ForgetNodes(host);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Databases))]
    public async Task Without_checking_the_cluster_a_scheduler_this_host_never_started_is_degraded(string name)
    {
        await using var host = await TestHost.Start(database: DatabaseTests.Require(name), clustered: true, startScheduler: false,
            services: s => s.AddHealthChecks().AddCronlight());
        try
        {
            await Node(host, "worker-a", TimeSpan.FromSeconds(2));
            var entry = await Health(host);
            Assert.Equal(HealthStatus.Degraded, entry.Status);
            Assert.Equal($"{host.SchedulerName} has not started", entry.Description);
        }
        finally
        {
            await ForgetNodes(host);
        }
    }
```

**Step 2: Run to check they fail**: the first does not compile (`checkCluster` is not a parameter). The second
should pass already; it guards the default.

**Step 3: Change `HealthCheck.cs`**

- Add `using Microsoft.Extensions.DependencyInjection.Extensions;`.
- `AddCronlight` gains a last parameter `bool checkCluster = false`, documented on the method:

```csharp
    /// <param name="checkCluster">
    /// For a clustered scheduler on a database store, check the cluster's nodes instead of this process: healthy while
    /// any node is alive, degraded when none is. For a host that manages schedulers other processes run and never
    /// starts them. Leave it off on the nodes themselves, so one whose scheduler did not start is not hidden by the others.
    /// </param>
```

  and when it is true, registers what the reader needs and passes it to the check:

```csharp
        builder.Services.AddCronlightSchedulers();
        if (checkCluster)
        {
            builder.Services.TryAddSingleton<QuartzDataSource>();
            builder.Services.TryAddSingleton<ClusterNodesReader>();
        }
        return builder.Add(new HealthCheckRegistration(name,
            services => new SchedulerHealthCheck(services.GetRequiredService<CronlightSchedulers>(),
                checkCluster ? services.GetRequiredService<ClusterNodesReader>() : null),
            failureStatus, tags, timeout ?? TimeSpan.FromSeconds(10)));
```

- `SchedulerHealthCheck(CronlightSchedulers schedulers, ClusterNodesReader? cluster)`. `State` becomes an instance
  method (it needs `cluster`). After the job store read succeeds, and before the `IsStarted` checks:

```csharp
        if (cluster != null && await cluster.Read(scheduler, cancellationToken) is { } nodes)
        {
            var alive = nodes.Count(n => n.Alive);
            return alive > 0 ? ($"is running on {alive} {(alive == 1 ? "node" : "nodes")}", true) : ("has no running nodes", null);
        }
```

- The class's summary on `AddCronlight` mentions the mode in one clause: "…degraded while one has not started or is
  in standby (with `checkCluster`, while a clustered one has no running nodes)."

**Step 4: Run** `dotnet test tests/Cronlight.Tests --filter "FullyQualifiedName~ClusterTests|FullyQualifiedName~MetricsTests"`
with the databases set. Expected: PASS, none skipped.

**Step 5: Commit**: "Let the health check judge a clustered scheduler by its nodes"

---

### Task 4: The badge

**Files:**
- Modify: `src/Cronlight.UI/src/types.ts` (`SchedulerInfo`, and a `ClusterNode` type)
- Modify: `src/Cronlight.UI/src/test/fixtures.ts` (`scheduler()` gets `clusterNodes: null`)
- Modify: `src/Cronlight.UI/src/components/header.tsx`
- Test: `src/Cronlight.UI/src/components/header.test.tsx`

**Step 1: Types**

```ts
/** A node of a clustered scheduler, as it last checked in */
export interface ClusterNode {
  instanceId: string
  lastCheckinUtc: string
  checkinIntervalMs: number
  /** By the server's clock */
  secondsSinceCheckin: number
  alive: boolean
  /** This process's scheduler instance */
  thisNode: boolean
}
```

In `SchedulerInfo`, after `runningSince`:

```ts
  /** A clustered scheduler's nodes; null when it is not clustered or they cannot be read */
  clusterNodes: ClusterNode[] | null
```

And `clusterNodes: null,` in the `scheduler()` fixture.

**Step 2: Write the failing test** in `header.test.tsx` (import `ClusterNode` from `@/types`):

```tsx
const node = (instanceId: string, overrides: Partial<ClusterNode> = {}): ClusterNode => ({
  instanceId,
  lastCheckinUtc: '2030-01-01T00:00:00Z',
  checkinIntervalMs: 7500,
  secondsSinceCheckin: 3,
  alive: true,
  thisNode: false,
  ...overrides,
})

  it.each([
    [[node('a'), node('b')], 'Running · 2 nodes'],
    [[node('a'), node('b', { alive: false })], 'Running · 1 node'],
    [[node('a', { alive: false })], 'No nodes running'],
  ])('describes a cluster by its running nodes, whatever this process does: %#', (clusterNodes, label) => {
    renderHeader(scheduler({ jobStoreClustered: true, started: false, clusterNodes }))

    expect(screen.getByText(label)).toBeInTheDocument()
    expect(screen.queryByText('Stopped')).toBeNull()
  })
```

**Step 3: Run it to check it fails**: `npx vitest run src/components/header.test.tsx`, expect "Stopped" shown.

**Step 4: Change `header.tsx`**: replace the `state` computation and the badge:

```tsx
type Tone = 'running' | 'idle' | 'warning'

const dot: Record<Tone, string> = {
  running: 'bg-emerald-400',
  idle: 'bg-muted-foreground',
  warning: 'bg-amber-500',
}

/** A cluster by its nodes, which may all be other processes; otherwise this process, which is the scheduler */
function schedulerState(scheduler: SchedulerInfo): { label: string; tone: Tone } {
  if (scheduler.clusterNodes) {
    const alive = scheduler.clusterNodes.filter(n => n.alive).length
    return alive > 0
      ? { label: `Running · ${alive} ${alive === 1 ? 'node' : 'nodes'}`, tone: 'running' }
      : { label: 'No nodes running', tone: 'warning' }
  }
  if (!scheduler.started) return { label: 'Stopped', tone: 'idle' }
  return scheduler.inStandbyMode
    ? { label: 'Standby', tone: 'idle' }
    : { label: 'Running', tone: 'running' }
}
```

and in `Header`: `const state = scheduler && schedulerState(scheduler)`, with the badge:

```tsx
        {state && (
          <Badge variant={state.tone === 'running' ? 'default' : 'secondary'} className="gap-1.5">
            <span className={`size-1.5 rounded-full ${dot[state.tone]}`} />
            {state.label}
          </Badge>
        )}
```

**Step 5: Run** the header tests and the whole UI suite (`npx vitest run`), expect PASS; lint clean.

**Step 6: Commit**: "Describe a clustered scheduler by its running nodes in the header"

---

### Task 5: The details

**Files:**
- Modify: `src/Cronlight.UI/src/components/header.tsx` (`SystemInfo`)
- Test: `src/Cronlight.UI/src/components/header.test.tsx`

**Step 1: Write the failing tests**

```tsx
  it("lists a cluster's nodes in the details, and says this process does not run it", async () => {
    renderHeader(
      scheduler({
        jobStoreClustered: true,
        started: false,
        clusterNodes: [node('worker-a'), node('worker-b', { alive: false, secondsSinceCheckin: 300 })],
      })
    )

    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))

    expect(screen.getByText('Not started (manages only)')).toBeInTheDocument()
    expect(screen.getByText('worker-a')).toBeInTheDocument()
    expect(screen.getByText('checked in 3 sec. ago')).toBeInTheDocument()
    expect(screen.getByText('not responding')).toBeInTheDocument()
    expect(screen.queryByText('Jobs executed')).toBeNull()
  })

  it('marks this node, with its own state, when this process is one of the nodes', async () => {
    renderHeader(
      scheduler({
        jobStoreClustered: true,
        inStandbyMode: true,
        clusterNodes: [node('node-1', { thisNode: true }), node('node-2', { secondsSinceCheckin: 90 })],
      })
    )

    await userEvent.click(screen.getByRole('button', { name: 'Scheduler details' }))

    expect(screen.getByText('this node')).toBeInTheDocument()
    expect(screen.getByText('Standby')).toBeInTheDocument()
    expect(screen.getByText('checked in 1 min. ago')).toBeInTheDocument()
    expect(screen.getByText('Jobs executed')).toBeInTheDocument()
  })
```

If no existing test opens the details menu, check that `userEvent.click` opens the Radix dropdown in jsdom; if it
does not, use `userEvent.pointer` or `fireEvent.pointerDown` as other dropdown tests in `src` do (search for
`'Change layout'` in `App.test.tsx`, which opens a dropdown with `userEvent.click`).

**Step 2: Run to check they fail.**

**Step 3: Rewrite `SystemInfo`**. With `clusterNodes` null, it shows exactly today's rows under "Scheduler". With
nodes, three sections:

```tsx
type Rows = [string, string][]

/** "3 sec. ago", "2 min. ago", from the server's count */
const ago = (seconds: number) =>
  seconds < 60 ? `${seconds} sec. ago` : `${Math.floor(seconds / 60)} min. ago`

function SystemInfo({ scheduler }: { scheduler: SchedulerInfo }) {
  const nodes = scheduler.clusterNodes
  const store = `${scheduler.jobStore}${scheduler.jobStoreClustered ? ' (clustered)' : ''}`
  const access = scheduler.canManage ? 'Manage' : 'Read-only'
  const sections: { label: string; rows: Rows }[] = nodes
    ? [
        {
          label: 'Scheduler',
          rows: [
            ['Version', scheduler.version],
            ['Job store', store],
            ['Your access', access],
          ],
        },
        {
          label: 'This process',
          rows: scheduler.started
            ? [
                ['Instance', scheduler.schedulerInstanceId],
                ['State', scheduler.inStandbyMode ? 'Standby' : 'Running'],
                ['Threads', String(scheduler.threadPoolSize)],
                ['Jobs executed', String(scheduler.numberOfJobsExecuted)],
                ['Running since', formatDateTime(scheduler.runningSince)],
              ]
            : [['State', 'Not started (manages only)']],
        },
      ]
    : [
        {
          label: 'Scheduler',
          rows: [
            ['Version', scheduler.version],
            ['Instance', scheduler.schedulerInstanceId],
            ['Job store', store],
            ['Threads', String(scheduler.threadPoolSize)],
            ['Jobs executed', String(scheduler.numberOfJobsExecuted)],
            ['Running since', formatDateTime(scheduler.runningSince)],
            ['Your access', access],
          ],
        },
      ]

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="icon" aria-label="Scheduler details">
          <Info />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80">
        {sections.map(({ label, rows }, i) => (
          <Fragment key={label}>
            {i > 0 && <DropdownMenuSeparator />}
            <DropdownMenuLabel>{label}</DropdownMenuLabel>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 px-2 py-1.5 text-sm">
              {rows.map(([name, value]) => (
                <div key={name} className="contents">
                  <dt className="text-muted-foreground">{name}</dt>
                  <dd className="truncate text-right font-medium" title={value}>
                    {value}
                  </dd>
                </div>
              ))}
            </dl>
          </Fragment>
        ))}
        {nodes && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Nodes</DropdownMenuLabel>
            <ul className="space-y-1.5 px-2 py-1.5 text-sm">
              {nodes.map(n => (
                <li key={n.instanceId} className="flex items-center justify-between gap-2">
                  <span className="min-w-0 truncate font-mono text-xs" title={n.instanceId}>
                    {n.instanceId}
                  </span>
                  <span className="flex shrink-0 items-center gap-1.5 text-xs text-muted-foreground">
                    {n.thisNode && <Badge variant="outline">this node</Badge>}
                    {n.alive ? (
                      `checked in ${ago(n.secondsSinceCheckin)}`
                    ) : (
                      <span className="text-amber-700 dark:text-amber-400">not responding</span>
                    )}
                  </span>
                </li>
              ))}
            </ul>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
```

The not-started row is labelled "State", so the existing "This process" heading names it; the test looks for its
value. Import `Fragment` from `react`. The first section's separator is gone on purpose: the old menu had one
under the "Scheduler" label, which `DropdownMenuLabel` now does without; check it still looks right in the browser.

**Step 4: Run** the UI suite, lint, prettier. Expect PASS.

**Step 5: Commit**: "List a cluster's nodes in the scheduler details"

---

### Task 6: Docs, full checks

**Files:**
- Modify: `docs/behaviour.md` (a bullet near the other header and status bullets)
- Modify: `docs/metrics-and-health.md` (after the paragraph on `failureStatus`)

**Step 1: Docs**

`docs/behaviour.md`:

```markdown
- **A cluster by its nodes.** For a clustered scheduler on a database store, the header says how many nodes run it
  (**Running · 2 nodes**, or **No nodes running**), from the nodes' check-ins, whether or not this process runs it.
  The scheduler details list the nodes, mark this one, and say "Not started (manages only)" for a dashboard that
  only manages schedulers other apps run. Other schedulers show this process's state: Running, Standby or Stopped.
```

`docs/metrics-and-health.md`:

```markdown
A host that manages schedulers other processes run, and never starts them, would read as degraded ("has not
started"). `AddCronlight(checkCluster: true)` checks a clustered scheduler by its nodes instead: healthy while any
node is alive, degraded when none is. Leave it off on the nodes themselves, so a node whose scheduler did not start
is not hidden by the others.
```

**Step 2: Full checks** (no sample host running from this checkout):

- `build/test-all.sh --databases` from the repo root: UI, and .NET on four databases, none skipped.
- `cd tests/Cronlight.E2E && npx playwright test`: end-to-end, unchanged behaviour on SQLite.

**Step 3: In the browser** (optional, since the sample is SQLite): start the sample host and `npm run dev`, check the
header and details for a non-clustered scheduler look as before.

**Step 4: Commit**: "Describe the cluster status and the health check's cluster mode in the docs"
