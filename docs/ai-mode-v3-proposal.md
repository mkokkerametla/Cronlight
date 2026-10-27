# Cronlight AI Mode — v3 Proposal

Status: proposal, not committed. Written Oct 26, 2026, after discussion with the
maintainer. The goal is a design that can be dropped into `docs/` and refined
before any code is written.

## 1. The idea in one paragraph

AI Mode turns the Cronlight dashboard into a conversational operations
assistant. The operator types a question in plain language — "what are the
most failed jobs on last Tuesday?" — and gets an answer built from the
scheduler's own data, rendered as text and tables, with every claim linked
back to the underlying runs. The operator brings their own LLM API key; the
model is invoked through MCP-style tools that wrap the APIs Cronlight already
exposes. Alongside the chat, AI Mode always shows precomputed insights
(status summary, frequently failing jobs, error patterns), so the screen is
useful before the user types anything.

## 2. Principles

These are the constraints the design is built around. If a later decision
conflicts with one of them, the principle wins unless it is explicitly
revised here.

1. **Read-only first.** The first shippable AI Mode can only ask questions.
   It cannot trigger, pause, resume, delete, or re-run anything. Actions
   arrive later, and only behind an explicit human click (see §7).
2. **Every answer must be verifiable.** The model answers from tool results,
   never from memory. Tables link to history rows; run IDs are cited. If the
   data is missing, the answer says so instead of guessing.
3. **Bring your own key (BYOK).** Cronlight never holds, proxies, or pays
   for LLM usage. The key lives in the host application's configuration /
   secrets, is sent only to the configured provider, and is never stored in
   the Cronlight history database.
4. **Insights before chat.** The insights panel is deterministic (plain
   queries, no model) and ships first. It gives immediate value, teaches
   users what to ask, and makes the chat better by providing precomputed
   context.
5. **Logs are untrusted data.** Job names, error messages, and log lines can
   contain anything, including text that tries to instruct the model. Tool
   output is data, never instructions, and it is redacted before it reaches
   the model (see §6).
6. **No key, no problem.** Everything in Phase 1 works with no API key at
   all. AI Mode degrades to insights + suggested questions when no key is
   configured, rather than being a dead screen.

## 3. What already exists (the foundation)

AI Mode is mostly a new layer on top of existing surface, not new data
collection:

- Jobs list, job types, and running jobs (`/api/jobs`, `/api/job-types`,
  `/api/running`).
- Run history with filters, including outcome (`/api/history`).
- Per-run logs (`/api/history/{id}/log`).
- Mutations that already exist and already require the
  `X-Cronlight-Request` header: trigger / pause / resume, delete, run
  cancel, and the history "Run again" action.
- Audit logging (e.g. refused actions and the actor recorded on runs).

What does **not** exist yet and must be built for AI Mode:

- Aggregation endpoints: failure counts grouped by job over a date range,
  status counts, top error messages grouped with counts. These are small
  queries over the history store, but they are new API surface.
- A log-redaction step between the history store and the model.
- An LLM provider abstraction (request/response, streaming, errors).
- The AI Mode UI itself (insights strip + chat thread).

## 4. The tool list

Tools are the contract between the model and the scheduler. The model never
touches the database or the Quartz API directly; it only calls these tools,
and every tool enforces its own limits (max rows, max log bytes, timeout).

### Phase 2 — read-only query tools

| Tool | Purpose | Key inputs | Returns |
|---|---|---|---|
| `list_jobs` | All jobs, optionally filtered by group or state | `group?`, `state?` (paused/running) | name, group, type, trigger summary, next fire time |
| `get_job` | One job's definition | `group`, `name` | schedule, triggers, last/next fire, type description |
| `query_runs` | Runs with filters, paginated | `job?`, `group?`, `outcome?`, `from?`, `to?`, `limit` (capped) | run rows: id, job, start, duration, outcome, error excerpt |
| `get_run_log` | Full log for one run | `runId` | log text, redacted (see §6) |
| `running_now` | Currently executing runs | — | run rows in progress, elapsed time |
| `failure_summary` | Failures grouped by job over a range (NEW) | `from`, `to` | per job: failure count, failure rate, last error |
| `status_summary` | Overall health in a range (NEW) | `from`, `to` | counts by outcome, total runs, running now |
| `error_patterns` | Top error messages grouped (NEW) | `from`, `to`, `limit` | error text, count, affected jobs |

Design notes:

- Date handling is the model's weakest point ("last Tuesday"). The tool
  layer accepts natural ranges but the server resolves them to absolute
  UTC/local timestamps and echoes the resolved range back in the result,
  so the answer can state exactly which window it covered.
- `failure_summary`, `status_summary`, and `error_patterns` do the counting
  server-side. The model must never be asked to count thousands of rows
  itself — that is where cost and errors explode.
- Tables in answers are rendered from structured tool output, not from
  freeform model text. The UI renders the table and attaches a "view runs"
  link per row that opens the filtered history view.

### Phase 1 — insights (deterministic, no model)

These are plain endpoints the dashboard calls directly. They are also what
the chat uses as precomputed context.

- **Status summary:** runs, successes, failures, cancelled in the last
  24 hours / 7 days, plus currently running.
- **Frequently failing jobs:** jobs ranked by failure count and failure
  rate over the last 7 days, with last error excerpt.
- **Error patterns:** most common error messages over the last 7 days,
  with counts and affected jobs.
- **Slowest / longest-running jobs** (optional, cheap to add once duration
  aggregation exists): p95 duration vs. typical, to catch jobs that are
  silently degrading rather than failing.

### Phase 3 — suggestions (model-assisted, still read-only)

- `suggest_fix(job, window)`: takes a job's recent failures and their
  redacted logs, and returns likely causes plus concrete next checks.
  Framing matters: the output is labelled "likely causes and next checks",
  never a diagnosis. Without codebase context, suggestions will often be
  generic — the value is in ranking the checks, not in claiming certainty.
- Suggestions are always shown next to the evidence (the error text and
  the affected runs), never standalone.

### Phase 4 — actions (human-confirmed only)

- `trigger_job`, `pause_job`, `resume_job`, `run_again` — the existing
  mutations, exposed as tools.
- The model may **propose** an action, but it may never execute one. The UI
  presents a confirmation card ("Pause FlakyJob?") and only the user's click
  performs the call, through the existing header-protected endpoint.
- Every AI-proposed action that is executed is audit-logged with the actor
  and the question that led to it.

### Phase 5 — external MCP server (optional, later)

- The same tools, exposed over the MCP protocol from the host application
  (e.g. under the dashboard's own path), so operators can query their
  scheduler from Claude, Copilot, or any MCP client — not just the built-in
  chat box.
- This is deliberately last: it reuses everything above and adds protocol
  plumbing, auth for external clients, and a second security review. It
  should not be attempted before the in-dashboard chat is proven.

## 5. User experience

### Entry

- An "AI Mode" toggle/tab in the dashboard header, next to the existing
  Jobs / History views. Switching views never loses the other view's state.
- AI Mode is hidden entirely when the host has not enabled it, and shows
  insights + suggested questions (but no chat input) when no API key is
  configured.

### Layout (see wireframes)

1. **Insights strip** across the top: status summary card, frequently
   failing jobs card, error patterns card. Each card links into the
   filtered history view.
2. **Chat thread** below: user questions on one side, assistant answers on
   the other. Answers render text, tables, and citation links to runs.
3. **Input box** pinned at the bottom: free text, with suggested-question
   chips above it for first-time users ("Most failed jobs last Tuesday?",
   "Why did FlakyJob fail?", "What's running right now?").
4. **Suggestion cards** (Phase 3) appear inline in the thread, attached to
   the job and error they concern, labelled as suggestions.
5. **Action confirmation cards** (Phase 4) appear inline, with explicit
   Approve / Cancel buttons. No action ever happens from model text alone.

### Mobile

- Insights stack vertically; the chat thread is full-width; the input box
  stays pinned. The existing mobile table-clipping issue in the dashboard
  should be fixed before AI Mode ships, since AI answers are tables.

## 6. Security and privacy

This section is the shippability checklist. AI Mode does not ship until
each item is addressed.

1. **Read-only enforcement is structural, not prompt-based.** Phase 2
   tools physically cannot mutate: the tool allowlist contains no mutation
   tools, and the chat path never calls mutation endpoints. A prompt
   injection cannot conjure a capability that is not wired up.
2. **Prompt injection from data.** Logs, job names, and error text are
   treated as untrusted. The system prompt instructs the model to treat
   tool output as data only, and Phase 4 actions require a human click
   regardless of what any text says.
3. **Log redaction.** Before any log text reaches the provider, it passes
   a redaction step with default patterns for connection strings, API
   keys, tokens, passwords, and bearer credentials, plus host-configurable
   custom patterns. Redacted spans are marked (e.g. `[redacted]`) so the
   model knows something was removed instead of reasoning over a gap.
4. **Key handling.** The key is read from host configuration / secrets at
   startup, held in memory only, sent only to the configured provider
   endpoint, and never written to the history store, logs, or audit trail.
   Key configuration errors must fail closed (chat disabled, clear message).
5. **Auth gating.** AI Mode requires the host's authentication. The
   dashboard's current effectively-anonymous access is not acceptable for
   a mode that can spend the operator's LLM budget and read full run logs.
   At minimum, AI Mode endpoints reject unauthenticated requests even if
   the rest of the dashboard does not.
6. **Caps.** Per-question caps: max tool calls, max rows per tool result,
   max log bytes per run, request timeout, and a daily/operator token
   budget if the host wants one. Caps produce a visible "results truncated"
   note, never silent partial answers.
7. **Audit.** AI Mode logs: questions are not stored by default (they may
   contain sensitive context); executed actions are always logged with
   actor, action, target job, and the question that prompted it.
8. **Provider errors are user-visible.** Quota, invalid key, and timeout
   errors surface as plain-language messages in the chat, not stack traces.

## 7. LLM provider abstraction

- **First:** OpenAI-compatible chat completions (model, endpoint, key from
  host config). This covers OpenAI, Azure OpenAI, and most gateways with
  one code path.
- **Second:** Anthropic, only if demand appears — a second code path that
  must earn its maintenance cost.
- **Model behaviour:** low temperature for factual queries; system prompt
  requires answering from tool results only, citing run IDs, and stating
  the resolved date range for every time-based answer.
- **Streaming:** responses stream into the thread; tables render once the
  underlying tool result is complete, so a half-streamed table never shows
  invented rows.

## 8. Phasing

| Phase | Contents | Needs a key? | Unlocks |
|---|---|---|---|
| 1 | Insights panel (status, frequently failing, error patterns) | No | Daily value for every user; context for chat |
| 2 | Read-only chat + query tools (§4) | Yes | "Ask anything about jobs and history" |
| 3 | Log-based suggestions (§4) | Yes | From "what failed" to "what to check" |
| 4 | Human-confirmed actions (§4) | Yes | From advice to doing, safely |
| 5 | External MCP server (§4) | Depends on client | Query from any MCP client |

Rough effort sense (one focused developer, excluding review and polish):
Phase 1 is about a week; Phase 2 is two to three weeks, most of it in the
aggregation tools and the provider abstraction; Phases 3–5 are each
additional, smaller increments on top. The security review in §6, not the
code volume, is what determines whether a phase is shippable.

Suggested order of work inside Phase 2: aggregation endpoints first, then
the read-only tools, then the provider abstraction, then the chat UI last.
Building the UI first is tempting and backwards — the UI is the smallest
part and the easiest to redo.

## 9. Open questions (to decide before Phase 1)

1. Which providers ship first — OpenAI-compatible only, or Anthropic too?
2. What is the dashboard's auth story? AI Mode needs one even if the rest
   of the dashboard stays open.
3. Default redaction patterns: how aggressive, and how does a host add
   custom ones?
4. Where does an external MCP server live (path, transport) when Phase 5
   arrives?
5. Should questions be stored (for "recent questions") or never stored?
   Default proposed: never stored.
6. Token/cost visibility: show per-answer token usage, or keep it invisible?
   Default proposed: show it in small text under answers, so operators can
   see what their key is spending.

## 10. What success looks like

- An operator opens AI Mode in the morning, reads the insights strip, and
  knows the overnight state without touching a filter.
- "Most failed jobs last Tuesday" returns a correct table with links in
  under a minute, including the resolved date range.
- A suggestion points at the right next check often enough to be trusted,
  and is labelled honestly when it is a guess.
- No secret has ever left the host except to the operator's own configured
  provider, redacted first.
- Nobody has ever had a job paused by a sentence in a log file.
