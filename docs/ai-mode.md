# AI mode

AI mode adds an area to the dashboard where people ask about their jobs in plain language: "which jobs failed
overnight, and why?", "what runs in the next 6 hours?", "which jobs got slower this week?". Your app's own AI model
answers, using the same read-only tools as the [MCP server](mcp.md), as the signed-in user. The answer streams in as
it is written, with each tool shown as it runs ("Reading the failure summary…"), and jobs it names are links that open
the job's panel.

It is in the MCP package, and works with any model [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
supports: Anthropic, OpenAI, Azure OpenAI, Ollama and others. Register the model as an `IChatClient`, then add AI mode:

```bash
dotnet add package Cronlight.Quartz.Dashboard.Mcp
```

```csharp
using Cronlight.Mcp;
using Microsoft.Extensions.AI;

builder.Services.AddChatClient(/* your provider's IChatClient */);
builder.Services.AddCronlight(o => { o.ViewPolicy = "QuartzViewers"; o.ManagePolicy = "QuartzAdmins"; });
builder.Services.AddCronlightAssistant();
// builder.Services.AddCronlightAssistant(o => o.Policy = "QuartzAdmins"); // only some users may ask

var app = builder.Build();
app.MapCronlight("/ops/jobs");
```

There is nothing to map: once it is added, the dashboard's header shows a **Dashboard | AI mode** switch, and without
it the dashboard looks as it always has. A link to `?area=assistant` opens AI mode, and Back returns from it.

`AddCronlightAssistant` does not need `AddCronlightMcp`; an app can have either, or both.

## Options

| Option | Default | What it does |
|---|---|---|
| `Policy` | none | A policy users must also meet to ask, on top of the dashboard's `ViewPolicy`. Each question costs model calls. |
| `ChatClientKey` | none | The key of a keyed `IChatClient`, when the app has several models. |
| `MaxToolCalls` | 12 | How many tool calls one answer may make before the model must answer with what it has. |
| `MaxOutputTokens` | 4096 | The most tokens the model may write in one answer. `null` leaves it to the provider, but some stop at a few hundred (Workers AI's default is 256). An answer that reaches the limit says it was cut off. |

## Choosing a model

AI mode needs a model that supports **tool calling** (also called function calling): the model answers by calling
Cronlight's tools, so a model without it can only guess. Larger models choose tools better and write better answers;
small local ones work, less reliably. Some to start with:

| Provider | Model | Notes |
|---|---|---|
| Cloudflare Workers AI | `@cf/openai/gpt-oss-120b` | Tested with AI mode. On the Free plan, within its daily allowance |
| Cloudflare Workers AI | `@cf/zai-org/glm-5.3-flash`, `@cf/moonshotai/kimi-k2.6` | Workers Paid plan only |
| OpenAI | `gpt-5-mini` | |
| Ollama (local) | A model tagged *tools*, such as `qwen2.5:7b` | No key, and nothing leaves the machine; less reliable |

On Workers AI, the models with tool calling are marked **Function calling** in its
[model catalog](https://developers.cloudflare.com/workers-ai/models/). A model your plan does not include fails with
403 Forbidden (see [Troubleshooting](#troubleshooting)).

## Trying it with the sample host

The sample host turns AI mode on when `Assistant:Model` is set, using any OpenAI-compatible endpoint; without it, the
dashboard has no AI mode switch.

**1. Get a key.** For Cloudflare Workers AI: in the Cloudflare dashboard, **My Profile → API Tokens → Create Token**,
and use the **Workers AI** template. The account id is in the Cloudflare dashboard's address once you are signed in,
`dash.cloudflare.com/<account-id>/...`. For OpenAI, an API key from its platform. A local Ollama needs no key: skip to step 3.

**2. Keep it in user secrets**, so it is in no file in the repository and no shell history. This prompts for the key
without showing it, and stops if nothing was pasted:

```bash
read -rsp "API key: " KEY; echo
if [ -n "$KEY" ]; then dotnet user-secrets set Assistant:ApiKey "$KEY" --project samples/Cronlight.SampleHost; else echo "Nothing was pasted, try again"; fi
unset KEY

# Check it is set, without printing it: prints 1
dotnet user-secrets list --project samples/Cronlight.SampleHost | grep -c '^Assistant:ApiKey = .'
```

**3. Start the sample** with the endpoint and model. User secrets are read only in the Development environment, which
`build/run.sh` sets:

```bash
# Cloudflare Workers AI
Assistant__Endpoint=https://api.cloudflare.com/client/v4/accounts/<account-id>/ai/v1 Assistant__Model=@cf/openai/gpt-oss-120b build/run.sh

# OpenAI (no endpoint)
Assistant__Model=gpt-5-mini build/run.sh

# A local Ollama (no key)
Assistant__Endpoint=http://localhost:11434/v1 Assistant__Model=qwen2.5:7b build/run.sh
```

Or with `dotnet run`, setting the environment yourself:

```bash
ASPNETCORE_ENVIRONMENT=Development Assistant__Endpoint=... Assistant__Model=...   dotnet run --project samples/Cronlight.SampleHost --urls http://localhost:5099
```

**4. Ask.** Open <http://localhost:5099/ops/jobs/>, switch the header to **AI mode**, and pick a suggested question or
type one. The sample's demo jobs fail on purpose, so "Which jobs failed in the last 24 hours, and why?" has something
to find.

## What it can and cannot do

- **It reads; it never changes anything.** It has the MCP server's read-only tools (jobs, runs, logs, statistics,
  error patterns, upcoming runs, cron previews) and none of its actions, whatever `AllowActions` says. Asked to pause
  or run a job, it links the job, whose panel has those actions.
- **It sees what the user sees.** Every tool checks the dashboard's permissions for the signed-in user, including
  per-scheduler policies, and masks secrets as the dashboard does. It answers about the scheduler the dashboard is
  showing unless asked about another.
- **Every answer shows its tools.** Under each answer, *Used N tools* lists the calls the model made, with their
  arguments and any refusal, so a figure can be traced to where it came from.
- **Counting is done on the server**, as for MCP clients ([how the analytics count](mcp.md#how-the-analytics-count)),
  so the model reports figures rather than adding them up.
- **Answers can still be wrong.** Models misread results now and then, and the dashboard says so under the question
  box. Check what matters in the dashboard itself; times in answers are UTC.
- **Stop** ends an answer as it is written, keeping what it wrote. **Try again** asks a failed question again, and
  **New conversation** starts afresh.

## Safety

- **Only the dashboard can ask.** Questions are a `POST` with the dashboard's request header, refused from other
  sites, so a web page cannot spend your model calls with a signed-in user's cookies.
- **Text from jobs is data.** Job names, descriptions, errors and logs can hold text that tries to instruct a model.
  The model is told to report such text, never follow it, and it has no tools that change anything, so the worst it
  can do is answer wrongly. Answers are shown as text: links other than job links are shown with their address and
  cannot be clicked, and no HTML or images are rendered.
- **Provider errors stay in your log.** When the model call fails, the user sees that it failed, with a trace id; the
  provider's message, which may quote keys or endpoints, is only logged.
- **Nothing is stored on the server.** The conversation is kept in the browser tab's session storage, so a reload keeps
  it and closing the tab forgets it, and it is sent with each question, at most 40 messages. Reads are not audited, as
  the dashboard's are not. What your model provider keeps is up to its terms.

## Troubleshooting

| What you see | Why, and what to do |
|---|---|
| No **AI mode** switch in the header | `AddCronlightAssistant` was not called. In the sample host, `Assistant:Model` is not set. |
| "AI mode has no model to answer with yet" | The assistant is added but no `IChatClient` is registered (or none under `ChatClientKey`). |
| "The AI model did not answer", with a trace id | The provider refused or failed. The app's log has its status and message under that trace id, logged as "The assistant's model call failed". |
| …and the log says 401 Unauthorized | The key is missing or wrong. With the sample, check it is in user secrets (above) and the app runs in Development. |
| …and the log says 403 Forbidden | On Workers AI: the model is not on your plan ("not available on the Workers Free plan"), or the token lacks Workers AI permission, or the account id in the endpoint is not the token's. |
| "Cut off: the answer reached the model's length limit" | The answer used `MaxOutputTokens`. Raise it, or ask for a shorter answer. |
| Answers without using any tools, or makes up jobs | The model does not support tool calling, or is too small to use it well. Try a larger model with tool calling. |
| Slow answers | Each tool call is a round trip to the model. Smaller hosted models, or fewer `MaxToolCalls`, answer faster. |

## The endpoint

The dashboard asks with `POST {dashboard}/api/assistant/ask`, sending `{"messages": [{"role": "user", "text": "…"}]}`
with the dashboard's request header. The answer is a stream of JSON objects, one a line (`application/x-ndjson`):
`tool` when the model calls a tool (its `id`, `name` and `arguments`), `tool-done` when it has answered (with `error`
if it was refused), `text` as the answer is written, then `done` (with `"cutOff": true` if the answer reached
`MaxOutputTokens`), or `error` with a `traceId` if the model call failed.
