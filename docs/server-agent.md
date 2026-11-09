# Server agent: a harness that runs in the server

Status: plan (2026-11-09), milestone M9 in [plan.md](plan.md). Nothing is built yet.

## 1. What and why

Today every session runs a harness (Claude Code, Codex, OpenCode) as a process on a daemon's machine. The model loop,
the tools and the credentials all live there.

The **server agent** is a harness of our own:

- **The loop runs in the server.** Each step calls the model, runs the tool calls, and calls the model again until it
  stops.
- **The server defines the tools.** The model sees only the tools the session's settings allow.
- **A daemon runs the machine tools.** Reading, writing, searching and shell commands run on the machine the session
  points at, over the existing machine RPC.
- **A session may have no machine at all.** Then it has no read, write or shell tools, only MCP servers (and BoSoFa's
  own tools such as asking the person).
- **Providers:**
  - the OpenAI **Responses** API, with an OpenAI API key, a ChatGPT subscription signed in through OAuth (as
    CodeAlta does), or an OpenCode Go API key;
  - the Anthropic **Messages** API, with an Anthropic API key (OpenCode Go also serves it).
- **The system prompt can be set** in a profile and per session.

What it gives over a daemon harness:

- Sessions that need no computer: research, triage, or working through MCP servers only.
- One loop we control end to end: the same tools, approvals, transcript and usage on every provider, and model
  switches inside a session.
- Credentials never leave the server: no account lease to a daemon.
- Machines need only `bosofad`, not Node, Claude Code or Codex.

Daemon harnesses stay. The server agent is one more choice in the harness list, with the id `agent`.

CodeAlta (`~/Research/CodeAlta`) already has a loop like this, and this plan follows it where it fits. The design docs
`doc/providers.md`, `doc/runtime.md` and `doc/sandbox.md` there are worth reading before building.

## 2. Taken from CodeAlta

| CodeAlta | Here |
|---|---|
| A provider's turn executor makes exactly one model call (`IModelProviderTurnExecutor.ExecuteTurnAsync`); `AgentSession.ExecuteRunAsync` loops around it the same way for every provider. | Same split: `IModelTurnExecutor` per API, one `AgentLoop`. |
| Every request is rebuilt from the session's journal: full input, `store=false`, no `previous_response_id`. Switching provider, forking or rewinding needs nothing kept by the provider. | The journal is the session's items in BTDB. Every step is rebuilt from them. |
| Reasoning is replayed as `encrypted_content` (OpenAI) or signed thinking blocks (Anthropic), only to the same provider, model and transport; any other one gets the text. | Same rule. The opaque part is kept with the item. |
| Unknown tool, unparsable arguments, a failing handler: each becomes a tool result the model reads, never a failed run. A tool call is never asked twice. | Same. |
| Steering is added before the next step; a run about to end continues if a steer arrived. | Maps to our dispatch modes (steer, queue). |
| Compaction at 95% of the input limit, and once more on a context overflow, by a summarizing turn on the same executor. | Same, in step 8. |
| Sandbox worker: the loop and credentials stay in the host; tools run elsewhere over a message protocol (`invoke`, `progress`, `result`, `cancel`, `permission`). | The daemon is our worker; machine RPC plus streaming output carries the same messages. |
| Instructions are layered: base prompt, then user and project prompts (replace, or `mode: append`), runtime context, `AGENTS.md`/`CLAUDE.md`, and session instructions last so the cached prefix stays stable. | Same order, with profiles instead of prompt folders. |
| Approvals: `bypassPermissions` approves all, `acceptEdits` approves file changes, anything else reviews all; every pending request has a fresh id so a stale answer cannot resolve it. | Our runtime requests and permission modes already work this way. |

Where we differ:

- **Our own thin HTTP clients**, not the OpenAI and Anthropic SDKs. Every product assembly must stay AOT-compatible with
  source-generated JSON, and we need full control of headers, fields and reasoning items anyway. The wire formats we
  need are small.
- **A step limit.** CodeAlta has none. A server spending someone's API money needs a ceiling (default 200 model calls a
  run, set in the profile).
- **Durability across nodes.** CodeAlta's session lives in one process. Ours must survive the node that runs it dying
  (§4.2).

## 3. Settings of an agent session

A session with harness `agent` has:

| Setting | Where it comes from |
|---|---|
| Provider account | One of the creator's harness accounts: OpenAI API key, OpenAI subscription, OpenCode Go key, Anthropic API key. |
| Model | Chosen when starting; the profile may give a default. Listed from the provider where it can say (`/v1/models`). |
| Reasoning effort | Optional; providers that cannot take it ignore it. |
| System prompt | Built-in base, then the profile's, then the session's (§7). |
| Machine and working directory | Optional. Without one there are no machine tools. |
| Machine tools | `none`, `read` (read, list, search) or `full` (also write, edit, shell). The profile sets the most it allows. |
| MCP servers | From the profile, as today. |
| Permission mode | As today: `default`, `acceptEdits`, `plan` (read tools only), `bypassPermissions`. |
| Limits | Steps a run (default 200), tool output kept inline (16 KiB, the rest in a blob), shell timeout. |

Sessions stay visible only to their creator. Machine tools need the creator to be able to control the machine,
checked on every call, not only at the start.

## 4. Architecture

### 4.1 The loop

```
person ─ message ─▶ server node running the session (runner)
                      │  build request from the session's items + tools + instructions
                      ▼
                 IModelTurnExecutor ── HTTPS, streamed ──▶ OpenAI / ChatGPT / OpenCode Go / Anthropic
                      │  deltas ──▶ LiveHub (and other nodes)
                      │  assistant message + tool calls ──▶ event: AgentStepRecorded
                      ▼
                 for each tool call, in order:
                   approval needed? ──▶ runtime request, wait for the answer
                   machine tool ──▶ machine RPC ──▶ daemon runs it (PathGuard, timeout, output cap)
                   MCP tool ──▶ HTTP MCP from the server, or stdio MCP through the daemon
                   BoSoFa tool ──▶ ask/notify the person, upload a file
                   result ──▶ event: AgentToolCompleted
                      ▼
                 no tool calls and nothing steered ──▶ run ends (usage, cost) ──▶ idle
```

New code lives in `src/BoSoFa.Server/Agents/`: the loop, executors, tool registry and runner service. The protocol types
that cross to the daemon go into `BoSoFa.Protocol`.

### 4.2 Who runs a session, and surviving a dead node

- **Ownership.** One node runs a session's loop at a time. A new relation `AgentRunner(SessionId, NodeId, ClaimedAt)`
  records it, written by an `AgentRunClaimed` event, the same pattern as `MachinePresence`. The node that handles the
  person's message claims the session if nobody runs it; a message for a session another node runs is forwarded to it
  on the peer port, like machine RPC.
- **Failover.** When a node leaves, the leader expires its rows (as `NodePresenceExpired` does), and another node claims
  each running session and resumes it from its items.
- **Durable steps.** Each model response is stored as items in one event before any of its tools run, and each tool
  result is stored before the next model call. The transcript is therefore the loop's state; nothing else must
  survive.
- **Resuming after a crash.**
  - A model call that was streaming is asked again.
  - A tool call that has no result is not run again: its effect is unknown. It gets the result "The server restarted
    while this ran; whether it took effect is unknown," and the model decides what to do.
  - A pending approval stays pending.
- **Single node.** The same code; the claim always succeeds.

### 4.3 Transcript and items

The existing items fit: `User`, `Assistant`, `Thinking`, `ToolUse`, `ToolResult`, `RunEnded`, `Notice`, `Diff`. They
need:

- Provider-opaque data on `Thinking` and `Assistant` items: OpenAI `encrypted_content` and item ids, Anthropic thinking
  signatures, plus the provider, model and transport it came from. Kept in the item's data as JSON (in a blob above the
  inline limit). It never reaches the browser.
- Usage on each step: input, output, cache read, cache write and reasoning tokens, and cost. The run total goes into
  `RunEnded`, as for daemon harnesses.
- Tool results above 16 KiB stored as a blob, with the model given the first 16 KiB and a note on how to read more (the
  `read_file` tool with an offset, for machine tools).

The event log is kept forever, so the size per step matters: deltas stay ephemeral, and large payloads go to blobs, as
they already do.

### 4.4 Live deltas

The runner publishes text, thinking and tool-output deltas to `LiveHub`. `LiveRelay` follows machines today. It gains a
second kind of source, a session's runner node, so a browser connected to another node still sees the stream. Lost
deltas cost nothing: the final items come through the event log.

## 5. Providers

```csharp
interface IModelTurnExecutor
{
    Task<TurnResponse> ExecuteAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation);
}
```

- `TurnRequest`: instructions, conversation, tools (name, description, JSON Schema), model, effort, cache key, session
  id.
- `TurnResponse`: assistant parts (text, reasoning with opaque data, tool calls), usage, stop reason, and errors
  classified as retry, context overflow, authentication or quota.
- **Retries:** once after a stream that broke before any tool call; backoff on 429 and 5xx, honoring `retry-after`.

### 5.1 OpenAI Responses with an API key

- `POST https://api.openai.com/v1/responses`, `Authorization: Bearer <key>`, `stream: true`, `store: false`.
- Input: the full conversation every step, with no `previous_response_id`. Settings:
  - `include: ["reasoning.encrypted_content"]` with reasoning on;
  - function tools;
  - tool results as `function_call_output`.
- Session instructions go as the first user item, not in `instructions`, so the cached prefix is shared across
  sessions (CodeAlta found this caches better than a per-session `prompt_cache_key`).
- Server-sent events to handle:
  - `response.output_item.added` / `.done`
  - `response.output_text.delta`
  - `response.reasoning_summary_text.delta`, `response.reasoning_text.delta`
  - `response.incomplete`, `response.failed`, `error`, `response.completed` (with usage)
- Testing: the key in `~/.openaikey/key.txt`, read by an opt-in test through a variable that names the file. The key
  is never printed or logged.

### 5.2 OpenAI subscription (ChatGPT sign-in), as in CodeAlta

**Sign-in:** authorization code with PKCE S256.

- Authorize at `https://auth.openai.com/api/accounts/authorize`, exchange the code at
  `https://auth.openai.com/api/accounts/oauth/token`.
- Client id `dynamic_agent_client`, resource `https://api.openai.com/v1`, and the scope
  `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`.
- Extra parameters: `agent_name_hint=<brand name>`, `ext_agent_host_id`, `nonce`. The ID token is checked against the
  JWKS.
- Redirect: `http://127.0.0.1:1455/auth/callback`. A server cannot receive that, so:
  - **Desktop app:** it listens on the loopback port, completes the exchange, and sends the tokens to the server as a
    new account, like "Add your Claude subscription".
  - **Web only:** the browser lands on a page that does not load. The Accounts page asks the person to paste that
    address, and the server takes the code from it and exchanges it with the PKCE verifier it kept.

**Account:** a new kind, `OpenAISubscription`, holding access token, refresh token, expiry and ChatGPT account id,
encrypted with Data Protection like every account secret.

**Refresh:** five minutes before expiry, with `grant_type=refresh_token`. Refresh tokens rotate, so a refresh must
happen once even when two nodes need it: an `AccountTokensRefreshed` event that only succeeds against the version it
read.

**Calls:**

- `POST https://api.openai.com/v1/responses` with these headers:
  - `Authorization: Bearer`
  - `ChatGPT-Account-Id`
  - `originator: <brand id>`
  - `User-Agent: <brand>/<version>`
  - `session-id` set to the session's id
- `store: false`, `stream: true`, and `prompt_cache_key` set to the session id.
- Strip what the plan endpoint refuses, as CodeAlta's `ChatGptPlanRequestNormalizer` does:
  - fields: `max_output_tokens`, `temperature`, `top_p`, `metadata`, `truncation`, `user`, `previous_response_id`;
  - `system` roles become `developer`;
  - tools are wrapped in a `namespace`.
- Models: listed live; a static fallback list as in CodeAlta's `CodexSubscriptionStaticModelCatalog`.

**Checked first, in a spike:**

- that `dynamic_agent_client` may be used by another agent host than CodeAlta;
- that the sign-in works for the person's plan (earlier, Codex device sign-in had to be enabled in ChatGPT's security
  settings).

This uses a person's own subscription for their own sessions only. An account is never shared.

### 5.3 OpenCode Go

- An API key from the OpenCode console, a new account kind `OpenCodeGoApiKey`.
- Endpoints from [the Go docs](https://opencode.ai/docs/go/):
  - Responses: `https://opencode.ai/inference/go/openai/v1/responses`
  - Messages: `https://opencode.ai/inference/go/anthropic/v1/messages`
  - models: `https://opencode.ai/inference/go/v1/models`

  Every text model works through either API, because OpenCode translates. We call each model on its native one, and
  Responses otherwise.
- Requirements OpenCode states:
  - a stable `x-opencode-session` header for each conversation (the session id);
  - our own User-Agent, not a library's;
  - typical coding-agent traffic.
- A Go key is for a single-member workspace, and Go Teams excludes service accounts, so a Go key is always a person's
  own account, never a shared one.

### 5.4 Anthropic Messages

- `POST https://api.anthropic.com/v1/messages`, with `x-api-key`, `anthropic-version` and `stream: true`. For OpenCode
  Go, the Go base URL with its key.
- Tool calls as `tool_use` blocks, results as `tool_result` blocks. Parallel results are merged into one user message.
- Thinking: adaptive thinking with effort. Signed thinking blocks are replayed to the same model. A refused signature
  is retried once with the thinking sent as text, as CodeAlta does.
- Prompt caching: up to four `cache_control` breakpoints, on the tools, the system prompt, the last message, and the
  message that ended the previous request.
- JSON Schemas with a root-level `anyOf`/`oneOf` are merged first, because the API refuses them.
- Streaming events:
  - `message_start`, `message_stop`
  - `content_block_start`, `content_block_delta`, `content_block_stop`
  - `message_delta` (usage, stop reason)
  - `error`
- Only Anthropic API keys. The `claude setup-token` subscription tokens stay for Claude Code on daemons.

### 5.5 Models, limits and prices

The context window, output limit and price of a model come from a checked-in models.dev snapshot
(`https://models.dev/api.json`), as in CodeAlta, refreshed by a small tool. A model missing from it still runs, with
compaction off and no cost shown.

## 6. Tools

### 6.1 Machine tools (run by the daemon)

A new machine RPC family, `ToolRpc`. The daemon runs each call inside the session's working directory with `PathGuard`,
the same guard file browsing uses.

| Tool | Group | Does |
|---|---|---|
| `read_file` | read | Lines of a text file, with offset and limit; images as images. |
| `list_dir` | read | Entries of a folder. |
| `glob` | read | Files matching a pattern. |
| `grep` | read | Matching lines (ripgrep when installed, else our own). |
| `write_file` | full | Create or replace a file. |
| `edit_file` | full | Replace exact text, once or everywhere. |
| `apply_patch` | full | A multi-file patch, for models trained on it (OpenAI). |
| `shell` | full | A command in the system shell, with timeout, output cap and streamed output. |

- **Timeouts and cancellation.** Machine RPC times out after 30 s today. Tool calls get their own timeout (the shell's
  is set by the model, at most 10 minutes) and a cancel message for an interrupt.
- **Streamed output.** Shell output streams as ephemeral messages, the way terminal output does.
- **Duplicates.** The daemon remembers each tool call id for a while, so a request sent again after a dropped
  connection does not run twice.
- **Changes.** File changes are reported as `Diff` items, as worktree sessions do now.
- **Offline machine.** A call to a machine that is offline returns "The machine is offline" to the model, which may
  wait or stop. It never runs later.

### 6.2 MCP servers

- **HTTP MCP servers** are called from the server. Their secrets are already there, in the catalog.
- **Stdio MCP servers** need a process. With a machine, the daemon starts them in the session's working directory and
  passes `tools/list` and `tools/call` through a new RPC. Without a machine, the session cannot use them, and starting
  it says so.
- Tools are named `mcp__<server>__<tool>`, with the server's schema passed through. Results keep text and images.

### 6.3 BoSoFa tools

`ask_user` (yes or no, shown as an approval), `notify_user` and `upload_file` (machine sessions only). These are the
same tools the daemon's MCP endpoint gives daemon harnesses, implemented in the server.

### 6.4 Approvals

The existing runtime requests and permission modes:

- **Read tools and MCP tools the profile marks safe** need no approval.
- **Writes** need approval unless the mode is `acceptEdits`.
- **Shell and other MCP tools** need approval unless the mode is `bypassPermissions`.
- **`plan`** offers read tools only.

A denial becomes the tool's result, with the person's message.

### 6.5 An MCP-only session

No machine, so no machine tools. Its tools are the profile's HTTP MCP servers and the BoSoFa tools without
`upload_file`. This is the first thing built (step 2), because it needs no daemon change.

## 7. System prompt

The instructions are assembled in this order, so the part shared by many sessions comes first and is cached:

1. **Base prompt.** Built in and brand-aware. It covers how to use the tools, the working directory, and to ask before
   destructive actions.
2. **The profile's system prompt.** It either replaces the base or appends to it (`mode: replace | append`).
3. **Runtime context.** Date, the machine's platform and shell, the working directory, and which tool groups exist.
   With no machine, it says there is no file system.
4. **Project instructions.** `AGENTS.md`, or `CLAUDE.md` when there is no `AGENTS.md`, read from the working directory
   through the daemon when the session has a machine.
5. **The session's own system prompt**, given when starting it.

The profile editor and the new-session form get a system prompt field. A session's prompt is stored with the session,
so the transcript shows what the model was told.

## 8. Context and cost

- Usage is counted on each step. Input tokens include cache tokens, as CodeAlta counts them.
- **Compaction** starts at 90% of the model's input limit, and once more on an overflow error. A summarizing call on
  the same executor keeps the last user message, and the summary becomes a `Notice` plus a compaction marker item.
  Later requests start from that marker.
- The run's cost goes into `RunEnded` and the session's total, from models.dev prices. Subscription accounts show the
  API price, marked as an estimate.
- Limits per run: the step ceiling, and an optional cost ceiling from the profile. Hitting one ends the run with a
  notice.

## 9. Server, API and UI changes

- **Protocol:**
  - `Harnesses.Agent = "agent"`;
  - new `AccountKind` values `OpenAISubscription` and `OpenCodeGoApiKey`;
  - `ToolRpc` requests and results;
  - delta kinds for tool output.
- **Domain** (raises `ApplicationGeneration`):
  - `AgentRunner`;
  - session fields: system prompt, tool group, reasoning effort, limits;
  - events `AgentRunClaimed`, `AgentStepRecorded`, `AgentToolCompleted`, `AccountTokensRefreshed`;
  - profile fields: system prompt and mode, most tool group allowed, step and cost ceilings, model default.
- **`SessionCreated`** allows no machine for `agent` sessions. Every other harness still needs one.
- **API:**
  - `GET /api/v1/accounts/{id}/models`
  - the OpenAI sign-in start and finish endpoints
  - session creation fields for the agent
- **UI:**
  - the new-session form gets an "Agent (server)" harness with account, model, effort, system prompt, and "No machine
    (MCP only)";
  - the Accounts page gets "Sign in with ChatGPT" (desktop) or the paste-the-address fallback (web), and OpenCode Go;
  - the session view shows tool calls with their streamed output.

## 10. Testing

- **Fake providers.** A local HTTP server replays recorded streams for Responses and Messages: text, tool calls,
  reasoning, errors, overflow. It is the replay harness's counterpart, so CI needs no key and no network.
- **Loop tests:**
  - tool dispatch and approvals;
  - a step that was streaming when the node died is resumed on another node of the in-process cluster;
  - a tool that was running when the node died gets the "unknown" result;
  - steer, interrupt, the step ceiling, compaction.
- **Live tests, opt-in and never in CI:**
  - OpenAI with the key file named by `BOSOFA_TEST_OPENAI_KEY_FILE` (`~/.openaikey/key.txt` here);
  - Anthropic with an Anthropic API key once there is one to test with;
  - the subscription sign-in and OpenCode Go by hand, when the person signs in.
- **End to end:** an MCP-only session answering through an MCP tool; a machine session editing a file and running the
  tests on a real daemon.

## 11. Steps

1. **Providers, alone.** The Responses and Messages clients with SSE parsing, the fake provider server and recorded
   streams, and the opt-in live OpenAI test. *Done when* a tool call goes round trip against the real OpenAI API with
   the test key.
2. **The loop, MCP only.** `AgentLoop`, `AgentRunner` with claims and failover, items and usage, deltas through
   `LiveHub` and `LiveRelay`, steer, interrupt, stop, approvals, BoSoFa tools, HTTP MCP servers. Accounts: OpenAI API
   key. *Done when* a session with no machine answers through an HTTP MCP server, and survives its node being killed
   in the cluster test.
3. **Machine tools.** `ToolRpc` in the daemon (read, list, glob, grep, write, edit, apply_patch, shell) with timeouts,
   cancellation, streamed output, de-duplication and diffs; stdio MCP through the daemon. *Done when* the agent fixes
   a failing test on a real machine with approvals on.
4. **Anthropic Messages.** Thinking, caching, schema fixes. Accounts: Anthropic API key.
5. **OpenAI subscription.** PKCE sign-in from the desktop app and the web paste fallback, token refresh as one event,
   the ChatGPT request rules. *Done when* a person signs in and runs a session on their plan.
6. **OpenCode Go.** The account kind, the model list, its headers, native API per model.
7. **System prompt and profiles.** The layers of §7, the profile and new-session fields, `AGENTS.md`.
8. **Context and cost.** Compaction, the models.dev snapshot, costs, step and cost ceilings.
9. **UI polish.** The model picker, tool-call cards with streamed output, account sign-in flows. Run every flow in the
   browser and the desktop app.

## 12. Open questions

1. **Shared keys.** Should an admin be able to add an API key every member may use, billed to the company? Accounts
   are personal now. OpenCode Go and the ChatGPT subscription can never be shared; an OpenAI or Anthropic API key
   could.
2. **Default ceilings.** 200 model calls a run, and no cost ceiling unless the profile sets one?
3. **Stdio MCP servers** in sessions with a machine: run them on the daemon (planned), or allow only HTTP MCP servers
   for the agent?
4. **Hosted tools.** Offer OpenAI's hosted `web_search`, or a web fetch tool of our own, on sessions without a machine?
5. **The name in the UI.** "Agent (server)", or something else?

## 13. Risks

| Risk | Answer |
|---|---|
| The ChatGPT sign-in client (`dynamic_agent_client`) is meant for CodeAlta only, or changes. | Spike first (step 5). API keys and OpenCode Go do not depend on it. |
| A tool with side effects runs twice after a failover. | The daemon remembers tool call ids, and an unfinished call is reported as unknown, never run again. |
| Transcripts with large tool output make the event log, which is kept forever, grow fast. | Inline limit of 16 KiB, the rest in blobs. Deltas stay ephemeral. |
| Provider wire formats change. | Thin clients with recorded streams in tests. A failed parse becomes a clear error in the run, not a stuck session. |
| A runaway loop spends money. | Step and cost ceilings, interrupt, and the cost on every run. |
