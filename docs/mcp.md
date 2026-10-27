# AI assistants (MCP server)

An optional package serves an [MCP](https://modelcontextprotocol.io) server from your app, so AI assistants such as
Claude, GitHub Copilot or any MCP client can answer questions about your scheduled jobs: "which jobs failed overnight,
and why?", "when does the invoice job run next?", "what did run 4182 log?". If you allow it, they can also pause,
resume and run jobs, with your permission rules and an audit trail.

```bash
dotnet add package Cronlight.Quartz.Dashboard.Mcp
```

```csharp
using Cronlight.Mcp;

builder.Services.AddCronlight(o => { o.ViewPolicy = "QuartzViewers"; o.ManagePolicy = "QuartzAdmins"; });
builder.Services.AddCronlightMcp();                              // read-only tools
// builder.Services.AddCronlightMcp(o => o.AllowActions = true); // also pause, resume and run

var app = builder.Build();
app.MapCronlight("/ops/jobs");
app.MapCronlightMcp("/ops/jobs/mcp");
```

It speaks MCP's streamable HTTP transport, statelessly: each request stands alone, so a dashboard on several nodes
behind a load balancer needs no sticky sessions.

## Connecting a client

The client signs in to your app as a browser would to the dashboard, so it needs a credential your authentication
accepts, usually a bearer token in a header. For example, with Claude Code:

```bash
claude mcp add --transport http cronlight https://ops.contoso.com/ops/jobs/mcp --header "Authorization: Bearer <token>"
```

The sample host takes its demo role header, so locally:
`claude mcp add --transport http cronlight http://localhost:5099/ops/jobs/mcp --header "X-Demo-Role: admin"`.

Clients that support MCP's OAuth sign-in can use it when your app's authentication is set up for it; Cronlight leaves
authentication to the host, as it does for the dashboard.

## Tools

These read, and change nothing:

| Tool | What it answers |
|---|---|
| `list_schedulers` | The schedulers you may see, and whether you may change their jobs |
| `list_jobs` | Jobs, searched, filtered by group or status and paged, with their triggers and next fire times, and counts by status |
| `get_job` | One job in full: type, description, job data (secrets masked), retry policy, triggers |
| `query_runs` | Recorded runs, newest first, by job, status and time window, with outcome, duration and error |
| `get_run_log` | What a run logged, sensitive job data masked, up to 50,000 characters |
| `running_now` | The runs going on now, on every node of a cluster when it can be read from the job store |
| `status_summary` | Jobs by status, runs going on, and runs succeeded, failed and blocked in the last hours |
| `failure_summary` | The jobs that failed in the last hours, most failures first, with their last and most common errors |
| `preview_cron` | Whether a cron expression is valid, and its next fire times |

With `AllowActions`, also:

| Tool | What it does |
|---|---|
| `pause_job`, `resume_job` | Pauses or resumes a job's triggers, as the dashboard's buttons do |
| `run_job` | Runs a job once now, with its saved data |
| `run_again` | Runs a failed, blocked or cancelled run again, with the data that run had |

Run history (`query_runs`, `get_run_log`, `failure_summary`, `run_again`) needs [execution history](history.md).
Every tool takes an optional `scheduler`, for [apps with several schedulers](schedulers.md). No tool creates, edits,
deletes or imports jobs.

## Safety

- **Permissions are the dashboard's.** The endpoint needs `ViewPolicy`, and outside Development is refused without
  one, as the dashboard is. Each tool checks again, including per-scheduler policies, so the rules hold even if the
  tools are served on another endpoint (below). The action tools need `ManagePolicy` for the scheduler, and are
  refused in read-only mode.
- **Actions are off unless you turn them on.** Without `AllowActions` the action tools are not registered at all, so
  no client, and no text a job logged, can call them. With it, they are marked as changing things (not read-only),
  which MCP clients use to ask the user before calling them.
- **Everything is audited.** Each action, refused or carried out, is written to the [audit log](audit.md) with the
  user, as the same action in the dashboard is, with `Via` set to `mcp`. Reads are not audited, as the dashboard's
  are not.
- **Secrets stay masked**, as the dashboard shows them: job data, run data and run logs.
- **Text from jobs is data.** Job names, descriptions, errors and logs are written by the jobs and the people who set
  them up, and could hold text that tries to instruct an assistant. The server's instructions and tool descriptions
  tell clients to report such text, never to follow it, and the action tools stay behind your permissions and the
  client's confirmation whatever it says.
- **Other sites are refused.** A browser request whose page is on another origin is refused, so a web page cannot use
  a signed-in user's cookies to reach the server, or reach one on their network.

## An app with its own MCP server

An app has one MCP server. If yours already has one (`AddMcpServer`), `AddCronlightMcp` adds Cronlight's tools to it,
and they are served on your endpoint as well as on `MapCronlightMcp`'s; each still checks the dashboard's permissions.
`AddCronlightMcp` sets the server's transport to stateless, and its name and instructions when you have not set them.
