# Server agent: a harness that runs in the server

Status: milestone M9 in [plan.md](plan.md); steps 1 (the providers), 2 (the loop, MCP only) and 3 (machine tools) are done, the rest is planned.

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
- **Durability across nodes.** CodeAlta's session lives in one process. Ours must survive the node that runs it dying
  (§4.2).

## 3. Settings of an agent session

A session with harness `agent` has:

| Setting | Where it comes from |
|---|---|
| Provider account | One of the creator's harness accounts: OpenAI API key, OpenAI subscription, OpenCode Go key, Anthropic API key. The harness list shows each such account under the name its owner gave it (§9). |
| Model | Chosen when starting; the profile may give a default. Listed from the provider where it can say (`/v1/models`). |
| Reasoning effort | Optional; providers that cannot take it ignore it. |
| System prompt | Built-in base, then the profile's, then the session's (§7). |
| Machine and working directory | Optional. Without one there are no machine tools. |
| Machine tools | `none`, `read` (read, list, search) or `full` (also write, edit, shell). The profile sets the most it allows. |
| MCP servers | From the profile, as today. |
| Permission mode | As today: `default`, `acceptEdits`, `plan` (read tools only), `bypassPermissions`. |
| Limits | None on steps or cost: a run goes on until the model stops or someone interrupts it, as in CodeAlta (decided 2026-11-09). Tool output is kept inline up to 16 KiB, the rest in a blob; shell commands have a timeout. |

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

- **Ownership.** One node runs a session's loop at a time, recorded in the relation `AgentRunner`, which exists only
  while the session has work. The event carrying a person's message names the node that handled it, and the handler
  claims the session for that node if nobody runs it.
- **No forwarding.** A message for a session another node runs is simply an event: every node applies it, and the node
  running the session sees the change. Interrupt, stop and approval answers reach it the same way.
- **Failover.** While a node leads, it claims the sessions of nodes that left the cluster (`AgentRunClaimed`) and resumes
  them from their items. A node that restarts finds its own rows.
- **Fencing.** Every write of the loop names the row's `Version`. A repeated write, or one from a node that lost the
  session, is refused.
- **Durable steps.** Each model response is stored as items in one event before any of its tools run, and each tool
  result is stored before the next model call. The transcript is therefore the loop's state; nothing else must
  survive. Each pass of `AgentLoop` reads it and does the one thing it says is due.
- **Resuming after a crash.**
  - A model call that was streaming is asked again.
  - The first tool call without a result is not run again: its effect is unknown. It gets the result "The server
    restarted while this ran; whether it took effect is unknown," and the model decides what to do. Later calls of that
    step had not started and run normally (tools run one at a time).
  - A pending approval stays pending, and the call runs once it is answered.
- **Single node.** The same code; the claim always succeeds.

### 4.3 Transcript and items

The existing items fit: `User`, `Assistant`, `Thinking`, `ToolUse`, `ToolResult`, `RunEnded`, `Notice`, `Diff`. They
need:

- `SessionItem.Replay`, the loop's own form of an item: OpenAI `encrypted_content`, Anthropic thinking signatures, whole
  tool arguments, the provider, model and transport it came from, and whether a person's message was queued. It is JSON,
  or a blob reference above the inline limit, and it never reaches the browser.
- Usage on each step, in the event that records it: input, output, cache read, cache write and reasoning tokens. The run
  total goes into `RunEnded`, as for daemon harnesses. Cost follows in step 8.
- Tool results are kept to 16 KiB, and the model reads that part with a note of how much was cut. Machine tools can offer
  more with `read_file` and an offset (step 3).

A person's message is stored when it arrives, which can be between a tool call and its result. `AgentConversation` places
it where the model may see it: a steering message after the step's tool results, a queued one after the current run.

The event log is kept forever, so the size per step matters: deltas stay ephemeral, and large payloads go to blobs, as
they already do.

### 4.4 Live deltas

The runner publishes the answer's text to `LiveHub`; reasoning and tool output follow in step 9. `LiveRelay` follows two
kinds of source, a machine's daemon and a session's runner node, so a browser connected to another node still sees the
stream. A retried call sends a delta with `Reset`, which drops what was shown. Lost deltas cost nothing: the final items
come through the event log.

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

A machine RPC family, `ToolRpc` (`src/BoSoFa.Daemon/Tools`). The daemon runs each call inside the session's working
directory with `PathGuard`, the same guard file browsing uses. `shell` is not confined by it, which is why it asks for
approval unless the mode is `bypassPermissions`.

| Tool | Group | Does |
|---|---|---|
| `read_file` | read | Numbered lines of a text file, with offset and limit. An image is only named, until the provider contracts carry images. |
| `list_dir` | read | Entries of a folder. |
| `glob` | read | Files matching a pattern, without those git ignores. |
| `grep` | read | Matching lines (ripgrep when installed, else our own). |
| `write_file` | full | Create or replace a file. |
| `edit_file` | full | Replace exact text, once or everywhere. |
| `apply_patch` | full | A multi-file patch, for models trained on it (OpenAI). A patch that does not fit changes nothing. |
| `shell` | full | A command in the system shell, with timeout, output cap and streamed output. |

- **Timeouts and cancellation.** Tool calls get their own timeout: the model sets the shell's, at most 10 minutes, and
  the other tools have 60 s. An interrupt sends `ToolCancelRequest`, and the whole process tree ends.
- **Streamed output.** Shell output streams as ordinary deltas under the ref `tool-<seq>`, which the result's item
  carries too, so the page replaces the stream with the result. The start and end of long output go to the model.
- **Duplicates.** A tool outlives the connection that asked for it. Each call has a key made of the session and the
  `ToolUse` item's seq, not the provider's call id, which some models reuse. A request sent again after a dropped
  connection joins the running call or gets its result, for 15 minutes. If the daemon restarted meanwhile, it does not
  know the key and answers that the outcome is unknown, instead of running the tool again.
- **Dropped connections.** The loop asks again for up to 60 s. After that the model is told the outcome is unknown.
- **Changes.** One `Diff` item per run, from git checkpoints as for daemon harnesses: the first tool that can change
  files takes `refs/<brand>/<session>/agent-<run>-before`, and the end of the run compares with `-after`. The refs come
  from the transcript, so a node taking over finds them. Outside a repository there is no diff.
- **Offline machine.** A call to a machine that is offline returns "The machine is offline. The tool did not run." to
  the model, which may wait or stop. It never runs later.
- **Environment.** The shell gets the same clean environment as harnesses plus common build tool variables, so no
  provider key leaks into it.

### 6.2 MCP servers

- **HTTP MCP servers** are called from the server. Their secrets are already there, in the catalog.
- **Stdio MCP servers** need a process. With a machine, the daemon starts them in the session's working directory and
  passes `tools/list` and `tools/call` through `ToolRpc` (decided 2026-11-09). Every call names the server, secrets
  included, so they stay in memory and a restarted daemon just starts it again; one idle for 15 minutes is stopped.
  Without a machine, the session cannot use them, and starting it says so.
- Tools are named `mcp__<server>__<tool>`, with the server's schema passed through. Results keep text; images are named
  in the text until the provider contracts carry them.

### 6.3 Web access

Every session, with a machine or without, gets both (decided 2026-11-09):

- **`web_fetch`, our own.** The server fetches a URL and returns its text (HTML turned into Markdown, capped like any
  tool output). It works the same on every provider.
  - The server never fetches its own network: no private, loopback or link-local addresses, after following
    redirects and resolving names.
  - Every fetch is recorded in the audit log.
- **The provider's hosted search**, where the provider offers one: OpenAI's `web_search` tool on the Responses API
  (also on the ChatGPT subscription, which CodeAlta allows), and Anthropic's web search tool on the Messages API.
  - The provider runs it, so its results arrive as provider items; we keep them like reasoning, as opaque data with a
    readable summary.
  - Where the endpoint lacks it (some OpenCode Go models), the model simply does not get the tool.

### 6.4 BoSoFa tools

`ask_user` (yes or no, shown as an approval), `notify_user` and `upload_file` (machine sessions only). These are the
same tools the daemon's MCP endpoint gives daemon harnesses, implemented in the server.

### 6.5 Approvals

The existing runtime requests and permission modes:

- **Read tools and web access** need no approval, nor will MCP tools a profile marks safe (step 7).
- **Writes** need approval unless the mode is `acceptEdits`.
- **Shell and other MCP tools** need approval unless the mode is `bypassPermissions`.
- **`plan`** offers read tools only.
- **A session on a machine** needs that machine to be one its creator controls, and a working directory.

A denial becomes the tool's result, with the person's message.

### 6.6 An MCP-only session

No machine, so no machine tools. Its tools are the profile's HTTP MCP servers, web access, and the BoSoFa tools
without `upload_file`. This is the first thing built (step 2), because it needs no daemon change.

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
- No step or cost ceiling (decided 2026-11-09). The cost so far shows on the running session, and the person stops a
  run with an interrupt.

## 9. Server, API and UI changes

- **Protocol:**
  - `Harnesses.Agent = "agent"`;
  - new `AccountKind` values `OpenAISubscription` and `OpenCodeGoApiKey`;
  - `ToolRpc` requests and results;
  - delta kinds for tool output.
- **Domain** (raises `ApplicationGeneration`):
  - `AgentRunner`;
  - session fields: system prompt, tool group, reasoning effort;
  - events `AgentItemsRecorded`, `AgentApprovalRaised`, `AgentRunFinished`, `AgentRunClaimed`, `AccountTokensRefreshed`;
  - profile fields: system prompt and mode, most tool group allowed, model default.
- **`SessionCreated`** allows no machine for `agent` sessions. Every other harness still needs one.
- **API:**
  - `GET /api/v1/accounts/{id}/models`
  - the OpenAI sign-in start and finish endpoints
  - session creation fields for the agent
- **UI:**
  - the new-session form lists each of the person's provider accounts as a harness, under the name they gave the
    account ("Work OpenAI", "My Claude API"), next to Claude Code, Codex and OpenCode (decided 2026-11-09). Choosing
    one shows model, effort, system prompt, and "No machine (MCP only)";
  - the Accounts page gets "Sign in with ChatGPT" (desktop) or the paste-the-address fallback (web), and OpenCode Go;
  - the session view shows tool calls with their streamed output.

## 10. Testing

- **Fake providers.** A local HTTP server replays recorded streams for Responses and Messages: text, tool calls,
  reasoning, errors, overflow. It is the replay harness's counterpart, so CI needs no key and no network.
- **Loop tests:**
  - tool dispatch and approvals;
  - a step that was streaming when the node died is resumed on another node of the in-process cluster;
  - a tool that was running when the node died gets the "unknown" result;
  - steer, interrupt, compaction.
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
   *Done (2026-11-09):*
   - The code is in `src/BoSoFa.Server/Agents`:
     - `IModelTurnExecutor` and its contracts;
     - `OpenAIResponsesExecutor`, for an API key and OpenCode Go;
     - `AnthropicMessagesExecutor`, for an API key and OpenCode Go;
     - `RetryingTurnExecutor`;
     - error classification.
   - `ProviderTest` covers them with fake streams:
     - two OpenAI streams recorded from the real API;
     - Anthropic streams written from its documented events, since there is no Anthropic key to record with yet.
   - The live test (`BOSOFA_TEST_OPENAI_KEY_FILE`, `BOSOFA_TEST_OPENAI_MODEL`) ran the tool loop to its answer on
     gpt-5.4-nano, gpt-5.4-mini and gpt-5.5.
   - Learned from the real API:
     - replayed output items must drop `id` and `status`: with `store: false` there is nothing to resolve them against;
     - non-reasoning models accept `include: reasoning.encrypted_content` too;
     - a model may call a tool several times at once (gpt-5.4-mini did), so the loop must answer every call of a step.
2. **The loop, MCP only.** `AgentLoop`, `AgentRunner` with claims and failover, items and usage, deltas through
   `LiveHub` and `LiveRelay`, steer, interrupt, stop, approvals, BoSoFa tools, HTTP MCP servers. Accounts: OpenAI API
   key. *Done when* a session with no machine answers through an HTTP MCP server, and survives its node being killed
   in the cluster test.
   *Done (2026-11-09):*
   - `AgentLoop`, `AgentRunnerService`, `AgentConversation`, `AgentToolbox` and `McpHttpClient` in `src/BoSoFa.Server/Agents`;
     `AgentRunner` and the loop's events in `src/BoSoFa.Server.Domain/Agents.cs`.
   - The new-session form lists each OpenAI account as a harness under its label, with "No machine (MCP only)". Images
     are refused for now.
   - `AgentSessionTest` runs a real server against a fake provider and MCP server: approvals and their denial, plan and
     bypass modes, `ask_user` and `notify_user`, interrupt, steer and queue, provider failure, stop, reasoning larger than
     an item, and a restart during a model call, a tool and a pending approval.
   - `AnAgentSessionSurvivesItsNodeBeingKilled` kills the node mid-answer in a three-process cluster, while a client on
     another node follows the text through the relay.
   - `LiveOpenAIAnswersThroughAnMcpServer` ran the whole loop on the real API (gpt-5.4-nano and gpt-5.5), and a session
     was started and answered in the browser.
3. **Machine tools.** `ToolRpc` in the daemon (read, list, glob, grep, write, edit, apply_patch, shell) with timeouts,
   cancellation, streamed output, de-duplication and diffs; stdio MCP through the daemon. *Done when* the agent fixes
   a failing test on a real machine with approvals on.
   *Done (2026-11-09):*
   - `AgentToolHost`, `FileTools`, `ApplyPatchTool`, `ShellTool` and `StdioMcpServers` in `src/BoSoFa.Daemon/Tools`;
     `AgentMachine` in the server. The protocol went to version 10, so daemons must be updated with the server.
   - The new-session form offers a machine or "No machine (MCP only)" for an agent, and shows running commands' output.
   - `AgentToolHostTest` runs every tool on real files and processes. `AgentMachineTest` runs a real server and daemon:
     approvals per mode, the diff, an offline machine, an interrupt that kills the command, the daemon's connection
     dropping while a command runs (it runs once), and stdio MCP on the machine.
   - `LiveOpenAIFixesAFailingTest` has a real model find and fix a failing test with every step approved. The same was
     done from the browser with the published Native AOT daemon.
   - Not done: worktrees for agent sessions are refused, and images (attached or read from a file) do not reach the
     model yet. Both wait for later steps.
4. **Anthropic Messages.** Thinking, caching, schema fixes. Accounts: Anthropic API key.
5. **OpenAI subscription.** PKCE sign-in from the desktop app and the web paste fallback, token refresh as one event,
   the ChatGPT request rules. *Done when* a person signs in and runs a session on their plan.
6. **OpenCode Go.** The account kind, the model list, its headers, native API per model.
7. **System prompt and profiles.** The layers of §7, the profile and new-session fields, `AGENTS.md`.
8. **Context, cost and web.** Compaction, the models.dev snapshot, costs, `web_fetch` with its address checks, and the
   providers' hosted search.
9. **UI polish.** The model picker, tool-call cards with streamed output, account sign-in flows. Run every flow in the
   browser and the desktop app.

## 12. Decisions (2026-11-09)

1. **Accounts are personal for now.** Sharing an account with other people or teams comes later, so an account keeps
   one owner and every use records who ran it; a later grant (a person, a team, everyone) adds to that without
   changing the sessions. OpenCode Go keys and ChatGPT subscriptions stay personal even then, by their terms.
2. **No limits** on steps or cost per run. The running cost shows, and an interrupt stops it.
3. **Stdio MCP servers** run on the session's machine through the daemon; sessions without a machine use HTTP MCP
   servers only.
4. **Web access:** both our own `web_fetch` and the provider's hosted search where it exists (§6.3).
5. **Name in the UI:** each provider account appears as a harness under the name its owner gave it.

## 13. Risks

| Risk | Answer |
|---|---|
| The ChatGPT sign-in client (`dynamic_agent_client`) is meant for CodeAlta only, or changes. | Spike first (step 5). API keys and OpenCode Go do not depend on it. |
| A tool with side effects runs twice after a failover. | The daemon remembers tool call ids, and an unfinished call is reported as unknown, never run again. |
| Transcripts with large tool output make the event log, which is kept forever, grow fast. | Inline limit of 16 KiB, the rest in blobs. Deltas stay ephemeral. |
| Provider wire formats change. | Thin clients with recorded streams in tests. A failed parse becomes a clear error in the run, not a stuck session. |
| A runaway loop spends money (there are no ceilings, by decision). | The running cost shows on the session, and interrupt stops it. Ceilings can be added to profiles later. |
| `web_fetch` is used to reach the server's own network. | Private, loopback and link-local addresses are refused after redirects and name resolution; fetches are audited. |
