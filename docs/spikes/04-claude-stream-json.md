# Spike 4: driving Claude Code over stream-json from a Native AOT process

Milestone M0. The question: can a C# Native AOT process (the future `bosofad`) drive the `claude` CLI through its
stream-json protocol, permission prompts included? This file also records the protocol details that the ClaudeCode
adapter of [plan §5.4](../plan.md) needs.

**Verdict: yes.** Every goal worked on the first real run, with Claude Code **2.1.317** on macOS arm64 and model
`haiku` (`claude-haiku-5-5`). The goals were: permission allow, permission deny, partial text streaming, the result
message, `--resume`, interrupting a turn mid-way, and an SDK MCP server served over the control protocol. One bug was
in my own code: I answered an MCP notification wrongly, so that server showed `failed` in the first run. It is fixed,
and the pitfall is listed below. The spike made 3 CLI processes with 5 turns and cost about **$0.0034** in total.

| Item | Result |
|---|---|
| AOT publish `osx-arm64` | 4.06 MB single executable (plus a separate `.dSYM`), **0 warnings** with `TreatWarningsAsErrors`, reflection JSON disabled |
| Permission prompt → allow | `can_use_tool` received, answered `allow`, `allowed.txt` written |
| Permission prompt → deny | answered `deny` with a message; file not written; the model reads the message as an error `tool_result` |
| Partial text | `stream_event` / `content_block_delta` / `text_delta` (also `thinking_delta`, `input_json_delta`) |
| Result | `type:"result"` with `subtype`, `total_cost_usd`, `usage`, `session_id`, `permission_denials`, `terminal_reason` |
| Resume | `--resume=<id>` in a new process kept the context ("I created allowed.txt") |
| Interrupt | control request `interrupt` → `result` `error_during_execution` / `terminal_reason:"aborted_streaming"`; next turn in the same process worked |
| MCP over control protocol | `--mcp-config {"type":"sdk"}` + `initialize.sdkMcpServers`; `initialize` / `tools/list` / `tools/call` arrived as `mcp_message` |

Code: `spikes/claude-stream/` (`Program.cs` holds the scenarios, `ClaudeProcess.cs` the transport and control
correlation, `Protocol.cs` the source-generated contracts). Raw logs of every line in both directions:
`artifacts/spikes/claude-logs/{allow,deny,resume}-*.log`, with a per-kind index (count + first sample) in
`*.kinds.txt`.

```sh
dotnet publish spikes/claude-stream -c Release -r osx-arm64 -o artifacts/spikes/claude-stream-aot
./artifacts/spikes/claude-stream-aot/claude-stream-spike allow            # new session, Write → allow
./artifacts/spikes/claude-stream-aot/claude-stream-spike deny             # new session, Write → deny
./artifacts/spikes/claude-stream-aot/claude-stream-spike resume <session> # resume + MCP call + interrupt + one more turn
```

## 1. Process launch

### Arguments (exact, as run)

```
claude -p
  --input-format stream-json --output-format stream-json
  --verbose                          # mandatory with -p + stream-json output
  --include-partial-messages         # stream_event lines (raw Anthropic SSE events)
  --replay-user-messages             # echoes host input back (see pitfalls)
  --permission-prompt-tool stdio     # permission prompts become control_request can_use_tool
  --permission-mode default          # MUST be explicit, see pitfalls
  --setting-sources project,local    # spike only: skip the user's settings.json
  --strict-mcp-config
  --mcp-config '{"mcpServers":{"bosofa":{"type":"sdk","name":"bosofa"}}}'
  --allowedTools=mcp__bosofa__ping
  --tools Read,Write                 # spike only: smaller system prompt
  --model haiku
  [--resume=<session-id>]            # `=` form binds the value whatever it looks like
```

- `--permission-prompt-tool stdio` does not appear in `--help` (2.1.317), but the Agent SDKs and CodeAlta use it and
  it works. The help has a newer `--permission-prompts host|none` (default `host`, meaning "the SDK host or
  --permission-prompt-tool"), so `stdio` may become redundant. Keep passing it.
- `--permission-mode` choices in the help: `acceptEdits, auto, bypassPermissions, manual, dontAsk, plan`. The CLI
  accepted `default` and reported `permissionMode:"default"`.
- Other options the adapter will need (CodeAlta uses them; I did not exercise them): `--session-id=<uuid>` to choose
  the id of a new conversation up front, `--fork-session`, `--resume-session-at <entry-uuid>` (hidden, used for
  rewind), `--effort`, `--disallowedTools=...`, `--append-system-prompt`, `--thinking-display summarized`
  (otherwise thinking blocks stream **empty**: `"thinking":""`, `"thinking_display":"updates"`), `--restricted`.
- Working directory: the CLI's `cwd` is the session's working folder. Transcripts go to
  `<config>/projects/<cwd with / replaced by ->/<session-id>.jsonl`.

### Environment

The spike starts the child from an **empty** environment, as a service daemon would. It passes only
`HOME USER LOGNAME SHELL TMPDIR PATH`, plus:

| Variable | Why |
|---|---|
| `CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS=1` | emits `system/session_state_changed` (`running` / `requires_action` / `idle`) |
| `CLAUDE_AGENT_SDK_CLIENT_APP=bosofa/<ver>` | names the host in the CLI's user agent (CodeAlta does the same) |
| `DISABLE_AUTOUPDATER=1` | the daemon owns harness versions (plan §5.4 catalog) |
| `CLAUDE_CODE_STARTUP_FAILURE_RESULTS=1` (recommended, not set here) | a refused start ends with a `result` carrying `startup_failure_reason` instead of only stderr |
| `CLAUDE_CONFIG_DIR=<profile dir>` (product) | account profile, see §8 |

The empty environment matters. This spike ran inside a Claude Desktop session, which exports about 30 variables:
`CLAUDECODE`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_MESSAGING_SOCKET`, `CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH`,
`CLAUDE_AGENT_SDK_MCP_NO_PREFIX`, `CLAUDE_CODE_SIMPLE`, and others. A child that inherits them changes behavior: it
treats itself as nested, expects auth refresh from the host, names MCP tools differently, and so on. `bosofad` must
build the child environment explicitly. CodeAlta removes a fixed list instead (`ClaudeCodeLauncher.InheritedSessionVariables`).
Starting from empty is stricter. If a user's provider variables (`ANTHROPIC_BASE_URL`, Bedrock/Vertex) must pass, put
them on an allowlist. With the empty environment and no API key, the CLI used the keychain login (`apiKeySource:"none"`,
`account.subscriptionType:"Claude Max"`).

## 2. Framing

- Newline-delimited JSON (UTF-8, no BOM) on stdin and stdout. Write one object per line and flush after each line.
  Skip stdout lines that do not start with `{`, because some builds print diagnostics there. Stderr was empty in all
  runs.
- Three groups of lines:
  1. **Messages** (CLI → host): `system`, `user`, `assistant`, `stream_event`, `result`, `rate_limit_event`.
  2. **Control requests** in both directions:
     `{"type":"control_request","request_id":"…","request":{"subtype":"…",…}}`.
  3. **Control responses**:
     `{"type":"control_response","response":{"subtype":"success"|"error","request_id":"…","response":{…}|"error":"…"}}`.
     The CLI's request ids are UUIDs. Host ids are free-form (the spike used `bsf_<n>`).
- `control_cancel_request` `{"type":"control_cancel_request","request_id":"…"}` exists. The CLI sends it when it no
  longer needs an answer, for example to a pending `can_use_tool` after an interrupt. CodeAlta handles it. **It was
  not observed in this spike.**
- Graceful shutdown: close stdin. The CLI finishes and exits with code 0 within about 1 s.

## 3. Handshake: `initialize`

The host sends `initialize` first. Its fields are **camelCase**, unlike the snake_case envelope:

```json
> {"type":"control_request","request_id":"bsf_1","request":{"subtype":"initialize",
    "hooks":{"PreToolUse":[{"hookCallbackIds":["bsf_pretool"],"timeout":30}]},
    "sdkMcpServers":["bosofa"],
    "appendSystemPrompt":"You are driven by an automated test harness. Keep every answer very short."}}
```

**Ordering:** before the CLI answers `initialize`, it opens the SDK MCP servers: it sends `mcp_message initialize`
and waits for the reply. The host therefore has to serve control requests **concurrently** while it awaits its own
`initialize`. A host that handles one line at a time and blocks on the response deadlocks.

The response (about 60 KB with this user's skills and plugins; trimmed here):

```json
< {"type":"control_response","response":{"subtype":"success","request_id":"bsf_1","response":{
    "commands":[{"name":"deep-research","description":"…","argumentHint":"","builtin":true}, …],
    "agents":[{"name":"Explore","description":"…"}, …],
    "output_style":"default","available_output_styles":["default","Concise", …],
    "models":[{"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)",
               "supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"], …}, …],
    "account":{"email":"…","organization":"…","subscriptionType":"Claude Max","apiProvider":"firstParty"},
    "pid":95189,"claude_code_version":"2.1.317","current_permission_mode":"default",
    "hooks_applied":true,"session_state":"idle","capabilities":["ui_surface_v1","mcp_call_result_not_for_model_v1"],
    "fast_mode_state":"off","remote_control_available":true, …}}}
```

These fields are useful for machine inventory and the UI: `claude_code_version`, `models`, `account`,
`current_permission_mode`, `commands`.

## 4. Message catalog (everything observed)

Counts are summed over the three runs. Samples are trimmed with `…`. Almost every message carries `session_id` and
`uuid`. Messages that belong to the main conversation carry `parent_tool_use_id: null`. A non-null value means a
subagent, which these runs did not have.

| Kind | Seen | Notes |
|---|---|---|
| `system/init` | 5 | Emitted at the **start of each turn** (each user message), not once per process. |
| `system/status` | 8 | `status:"requesting"` before each API request. |
| `system/session_state_changed` | 11 | `running` → `requires_action` (permission pending) → `running` → `idle`. Needs the environment flag. |
| `system/thinking_tokens` | 2 | Progress estimate while the model thinks. |
| `user` (string content, `isReplay:true`) | 5 | Echo of the host's user line with its assigned `uuid` and `timestamp` (`--replay-user-messages`). |
| `user` + `tool_result` | 3 | Tool output fed back to the model, with `tool_use_result` (structured) and `tool_result_meta` (permission decision). |
| `user` + `text` `[Request interrupted by user]` | 1 | Synthetic message written after an interrupt (no `isReplay`). |
| `assistant` (`text` / `tool_use` / `thinking`) | 8 | One complete message per content block, sharing the `message.id` of the API message. |
| `stream_event/*` | many | Raw SSE: `message_start`, `content_block_start` (`text` / `tool_use` / `thinking`), `content_block_delta` (`text_delta` / `input_json_delta` / `thinking_delta` / `signature_delta`), `content_block_stop`, `message_delta`, `message_stop`. |
| `rate_limit_event` | 2 | Subscription windows and how much of each is used. |
| `result/success`, `result/error_during_execution` | 4 | Exactly one per turn. |
| `control_request/can_use_tool` | 2 | Permission prompt. |
| `control_request/hook_callback` | 3 | A hook registered in `initialize` fired. |
| `control_request/mcp_message` | 13 | JSON-RPC for the SDK MCP server (it includes the failed first run's retries). |
| `control_response` | 19 | Answers to host requests, **plus echoes of the host's own control responses** (pitfalls). |

### Samples

```json
{"type":"system","subtype":"init","cwd":"/…/claude-work","session_id":"b9da8321-…","tools":["Read","Write","mcp__bosofa__ping"],
 "mcp_servers":[{"name":"bosofa","status":"connected","source":"sdk"}],"model":"claude-haiku-5-5","permissionMode":"default",
 "slash_commands":[…],"apiKeySource":"none","claude_code_version":"2.1.317","output_style":"default","agents":[…],"skills":[…],
 "plugins":[…],"memory_paths":{"auto":"…/memory/"},"messaging_socket_path":"/tmp/cc-socks/95189.sock","uuid":"…", …}

{"type":"system","subtype":"status","status":"requesting","session_id":"…","uuid":"…"}
{"type":"system","subtype":"session_state_changed","state":"requires_action","session_id":"…","uuid":"…"}
{"type":"system","subtype":"thinking_tokens","estimated_tokens":272,"estimated_tokens_delta":222,"session_id":"…","uuid":"…"}

{"type":"user","message":{"role":"user","content":"Use the Write tool to create …"},"session_id":"…","parent_tool_use_id":null,
 "uuid":"be075de2-…","timestamp":"2026-11-08T00:03:34.172Z","isReplay":true}

{"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-haiku-5-5","id":"msg_011Cgp…","role":"assistant",
 "content":[],"usage":{"input_tokens":2,"cache_creation_input_tokens":3664,"cache_read_input_tokens":1915,"output_tokens":16, …}}},
 "session_id":"…","parent_tool_use_id":null,"uuid":"…"}
{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01Lx…","name":"Write","input":{}}}, …,"api_message_id":"msg_011Cgp…"}
{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"file_path\": \"/…"}}, …}
{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":", two, three, four, five"}}, …}
{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"","estimated_tokens":50}},"thinking_display":"updates", …}
{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"CAQS…"}}, …}
{"type":"stream_event","event":{"type":"content_block_stop","index":0}, …}
{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":112, …}}, …}
{"type":"stream_event","event":{"type":"message_stop"}, …}
{"type":"stream_event","event":{"type":"message_stop"},"abandoned_blocks":{"api_message_id":"msg_011CgpUw…","from_block_index":2}, …}  // after interrupt

{"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_011Cgp…","role":"assistant",
 "content":[{"type":"tool_use","id":"toolu_01Lx…","name":"Write","input":{"file_path":"/…/allowed.txt","content":"hello from bosofa"}}],
 "stop_reason":null,"usage":{…}},"parent_tool_use_id":null,"session_id":"…","uuid":"…"}

{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01Lx…","type":"tool_result","content":"File created successfully at: …"}]},
 "tool_use_result":{"type":"create","filePath":"/…/allowed.txt","content":"hello from bosofa","structuredPatch":[],"originalFile":null},
 "tool_result_meta":[{"id":"toolu_01Lx…","permission_decision":{"decision":"accept","source":"user_temporary"}}], …}

{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1794100800,"rateLimitType":"five_hour","overageStatus":"rejected",
 "isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.14,…},"seven_day":{"utilization":0.1,…}}},"uuid":"…","session_id":"…"}
```

The partial text arrives through `text_delta`. The complete `assistant` message for the same block comes right after
`content_block_stop`, or just before it. Use `api_message_id` + `index` to correlate the deltas with that final item.
The tool-call arguments stream as `input_json_delta` fragments (10 for one small `Write`).

## 5. Permission flow

Sequence observed (allow run, timestamps in ms since start):

```
3484 < assistant  tool_use Write {file_path, content}           ← complete call
3503 < control_request hook_callback  (PreToolUse, bsf_pretool) ← hooks run first
3503 > control_response {}                                      ← "no opinion"
3518 < system session_state_changed requires_action
3518 < control_request can_use_tool
3518 > control_response {behavior:"allow", updatedInput:{…}}
3527 < system session_state_changed running
3535 < user tool_result "File created successfully …"
```

Request:

```json
{"type":"control_request","request_id":"7ef7654e-…","request":{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write",
 "input":{"file_path":"/…/claude-work/allowed.txt","content":"hello from bosofa"},"description":"allowed.txt",
 "permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu_01Lx…"}}
```

The answers, with camelCase fields as in the TS SDK's `PermissionResult`:

```json
> {"type":"control_response","response":{"subtype":"success","request_id":"7ef7654e-…",
   "response":{"behavior":"allow","updatedInput":{"file_path":"/…/allowed.txt","content":"hello from bosofa"}}}}

> {"type":"control_response","response":{"subtype":"success","request_id":"…",
   "response":{"behavior":"deny","message":"The BoSoFa spike denied this write on purpose. Do not retry; just reply: denied."}}}
```

- `allow` **must** echo `updatedInput`, which may be edited. CodeAlta also sends `updatedPermissions` set to the
  request's `permission_suggestions` to mean "allow for this session"; I did not test that here. Bash prompts
  typically suggest `addRules` entries.
- A `deny` message becomes an error `tool_result` that the model reads
  (`"is_error":true,"content":"The BoSoFa spike denied…"`). It carries `tool_result_meta.permission_decision`
  `{"decision":"reject","source":"user_reject"}`. The turn goes on: the model answered "denied". `"interrupt":true`
  on a deny also stops the turn; CodeAlta uses it, and I did not test it.
- The result lists denials: `"permission_denials":[{"tool_name":"Write","tool_use_id":"toolu_…","tool_input":{…}}]`.
- Hooks registered in `initialize` see **every** tool call before the permission check: a `PreToolUse` without a
  matcher. An empty response `{}` means no opinion. A hook can deny with
  `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"…"}}`.
  That works in every permission mode, including `bypassPermissions`. It is the right place for server-side policy
  in BoSoFa.
- Tools listed in `--allowedTools` (here `mcp__bosofa__ping`) skip `can_use_tool` but still fire the hook.

```json
{"type":"control_request","request_id":"d2cf6839-…","request":{"subtype":"hook_callback","callback_id":"bsf_pretool","tool_use_id":"toolu_01Lx…",
 "input":{"session_id":"…","transcript_path":"~/.claude/projects/-Users-…-claude-work/b9da8321-….jsonl","cwd":"…","prompt_id":"…",
  "permission_mode":"default","effort":{"level":"medium"},"hook_event_name":"PreToolUse","tool_name":"Write",
  "tool_input":{"file_path":"…","content":"hello from bosofa"},"tool_use_id":"toolu_01Lx…"}}}
```

## 6. Result, cost, usage

```json
{"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"done","stop_reason":"end_turn","terminal_reason":"completed",
 "session_id":"b9da8321-…","uuid":"…","duration_ms":2539,"duration_api_ms":2450,"ttft_ms":1467,"total_cost_usd":0.00090624,
 "usage":{"input_tokens":4,"cache_creation_input_tokens":3867,"cache_read_input_tokens":7494,"output_tokens":115,
          "output_tokens_details":{"thinking_tokens":0},"service_tier":"standard","iterations":[…]},
 "modelUsage":{"claude-haiku-5-5":{"inputTokens":4,"outputTokens":115,"cacheReadInputTokens":7494,"cacheCreationInputTokens":3867,
               "costUSD":0.00090624,"contextWindow":1000000,"maxOutputTokens":128000,"costBasis":"list"}},
 "model_usage_since_last_result":{"claude-haiku-5-5":{…,"costUSD":0.00090624}},
 "permission_denials":[],"result_index":0,"queued_turn_count":0,"api_error_status":null,"subagent_stats":{…},"safety_stops":0, …}
```

- `usage` and `model_usage_since_last_result` are **per turn**. `total_cost_usd` and `modelUsage` are **cumulative
  for the process**, not for the conversation: in the resume run the values went 0.00111 → 0.00111 → 0.00125, while
  per-turn `costUSD` was 0.000206 for turn 1 and 0.000140 for turn 3. Record per-turn cost from
  `model_usage_since_last_result[*].costUSD`. `result_index` counts the results of the process (0, 1, 2).
- `costUSD` is computed at list price (`costBasis:"list"`). On a subscription it is an estimate, not a bill.
- An interrupted turn reports `usage` of zeros and `model_usage_since_last_result:{}`, even though the aborted
  request did use tokens: its `message_delta` showed `cache_creation_input_tokens: 83`. Small amounts of usage are
  lost.
- `subtype` values seen: `success`, `error_during_execution` (interrupt). The SDK also defines `error_max_turns`,
  `error_max_budget_usd` and others; they were not seen. Use `is_error` together with `terminal_reason`
  (`completed`, `aborted_streaming`) to derive BoSoFa's `RunEnded(status, disposition)`.

## 7. Resume and interrupt

**Resume.** A new process was started with `--resume=b9da8321-…`, the id of the allow run. The `system/init` of the
first turn carried the same `session_id`, and the model answered "I created allowed.txt in the current directory".
That came from the transcript, since this process had never seen the file. Without `--fork-session` the id stays the
same. The `session_id` sent in the user line is ignored: the spike sent `""` on the first turn of a new session. CLI
startup to the `initialize` response took about 0.3 s warm and about 2 s cold.

**Interrupt.** The spike sent the request after the second `text_delta`:

```json
> {"type":"control_request","request_id":"bsf_2","request":{"subtype":"interrupt"}}
< {"type":"control_response","response":{"subtype":"success","request_id":"bsf_2","response":{"still_queued":[]}}}
< {"type":"assistant","message":{…,"content":[{"type":"text","text":"one, two, three, four, five, six, seven, eight"}]}, …}   ← partial block finalized
< {"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}, …}
< {"type":"stream_event","event":{"type":"message_stop"},"abandoned_blocks":{"api_message_id":"msg_…","from_block_index":2}, …}
< {"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_streaming","stop_reason":null,"result_index":1, …}
< {"type":"system","subtype":"session_state_changed","state":"idle", …}
```

- The CLI acknowledged within 5 ms, and the turn's `result` followed 3 ms later. Deltas that were already buffered
  still arrived after the ack.
- The partial text is kept: it is written as a final `assistant` message, followed by the synthetic interrupted user
  message. The next turn in the **same process** worked normally ("OK").
- `still_queued` lists user messages that were queued behind the running turn. CodeAlta sends
  `{"subtype":"interrupt","cancel_queued":true}` to drop them too.
- User messages written while a turn runs are **queued** by the CLI (`queued_turn_count` in `result`). That is
  `DispatchMode.Queue`. I did not exercise it here.

## 8. MCP over the control protocol (SDK servers)

`--mcp-config '{"mcpServers":{"bosofa":{"type":"sdk","name":"bosofa"}}}'` together with `"sdkMcpServers":["bosofa"]`
in `initialize` makes the CLI talk MCP JSON-RPC through `mcp_message` control requests. No port is opened. The answer
is wrapped in `mcp_response`:

```json
< {"type":"control_request","request_id":"…","request":{"subtype":"mcp_message","server_name":"bosofa",
   "message":{"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.317"}},"jsonrpc":"2.0","id":0}}}
> {"type":"control_response","response":{"subtype":"success","request_id":"…","response":{"mcp_response":{"jsonrpc":"2.0","id":0,
   "result":{"protocolVersion":"2025-11-25","capabilities":{"tools":{"listChanged":false}},"serverInfo":{"name":"bosofa","version":"0.1"}}}}}}
< … "message":{"jsonrpc":"2.0","method":"notifications/initialized"}            → answer {"mcp_response":{"jsonrpc":"2.0","result":{}}}
< … "message":{"method":"tools/list","jsonrpc":"2.0","id":1}                   → {"tools":[{"name":"ping","description":"…","inputSchema":{…}}]}
< … "message":{"method":"tools/call","params":{"name":"ping","arguments":{},"_meta":{"claudecode/toolUseId":"toolu_01TJ…","progressToken":2}},"jsonrpc":"2.0","id":2}
> … {"mcp_response":{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"pong from bosofad"}],"isError":false}}}
```

- The model sees the tool as `mcp__bosofa__ping`. `_meta["claudecode/toolUseId"]` links the call to the
  `assistant` `tool_use` block.
- A notification has no `id`. It must still get a **success** control response. In the first run the spike failed
  to answer it: it serialized a `default(JsonElement)` and answered with an error. The CLI then retried the handshake
  three times and marked the server `"status":"failed"`, and the model never saw the tool.
- This could replace, or sit beside, the loopback Streamable HTTP endpoint of plan §5.5 for Claude Code. The daemon
  would then serve the BoSoFa tools in-process, with no port and no bearer. Other harnesses would still need the
  HTTP endpoint, so a shared tool registry behind both transports is the natural design.
- Host → CLI `mcp_message` (e.g. `notifications/tools/list_changed`, used by CodeAlta) was not tested.

## 9. Account profiles: `CLAUDE_CONFIG_DIR`

As instructed, the spike used the user's own `~/.claude` login. What I verified about isolation:

- With `CLAUDE_CONFIG_DIR=<empty dir>`, `claude auth status` reports `"loggedIn":false` and
  `"configDirectory":"<dir>"`. The CLI creates `<dir>/.claude.json`, which normally lives at `~/.claude.json`.
  Transcripts move to `<dir>/projects/`.
- On macOS the OAuth credentials are stored in the login keychain, in an item **per config directory**. This machine
  has `Claude Code-credentials` (the default folder) and `Claude Code-credentials-<8 hex>` items, one for each other
  `CLAUDE_CONFIG_DIR` that has been signed in. On Linux and Windows the credentials are in `<dir>/.credentials.json`.
  So a profile directory is a self-contained account, apart from keychain items on macOS.

How a BoSoFa profile would be signed in:

1. **Interactive, once per machine and profile** (plan §5.4 "through a BoSoFa terminal"): run
   `CLAUDE_CONFIG_DIR=<data>/profiles/<id>/claude claude auth login [--claudeai|--console] [--email …]` in a
   daemon-provided PTY. It prints or opens a browser URL; the user completes OAuth and pastes back the code if asked.
   Afterwards, `claude auth status` (JSON, no model call) confirms the login and gives the profile's
   `email` / `subscriptionType` for inventory. The daemon should run as the user who owns the keychain, or the
   macOS keychain item is not reachable from a system service (untested).
2. **Headless or CI:** `claude setup-token` creates a long-lived subscription token. Give it to the child as
   `CLAUDE_CODE_OAUTH_TOKEN`, delivered like the API key: an ECIES-encrypted secret per machine. `--console` or
   `ANTHROPIC_API_KEY` gives API billing. CodeAlta's rule applies: decide about `ANTHROPIC_API_KEY` explicitly,
   because `-p` silently bills a key it finds in the environment.

`--setting-sources project,local` dropped the user's `settings.json` (its `defaultMode: bypassPermissions` and
plugins). User skills, slash commands and `~/.claude/CLAUDE.md` were still loaded: `system/init` listed the user's
skills, and `initialize.commands` was about 60 KB. A per-profile `CLAUDE_CONFIG_DIR` is the real isolation.

## 10. AOT and trimming

- `PublishAot`, `IsAotCompatible`, `InvariantGlobalization`, `JsonSerializerIsReflectionEnabledByDefault=false`,
  `TreatWarningsAsErrors`. Result: **0 trim/AOT warnings**; the binary is 4,059,696 bytes (3.9 MB), Mach-O arm64,
  linked against system frameworks only.
- What ran under AOT: `System.Diagnostics.Process` with redirected stdio and `Environment.Clear()`; `JsonDocument`
  for every inbound line (`Clone()` before the document is disposed); `JsonSerializer` with two source-generated
  contexts. `SpikeJson` is snake_case for the envelope and `McpJson` camelCase for MCP payloads. A few properties
  carry an explicit `[JsonPropertyName]`, because `initialize` and `PermissionResult` use camelCase inside a
  snake_case envelope. Typed binding of `result` works (`msg.Deserialize(SpikeJson.Default.ResultLine)`).
- Closed generics such as `ControlRequestLine<InitializeRequest>` are fine with source generation when each one is
  registered with `[JsonSerializable]`.
- `JsonElement?` properties are the AOT-friendly way to pass untyped parts through: `updatedInput`, MCP `result`, and
  the `id`. A `default(JsonElement)` (`Undefined`) throws `InvalidOperationException` at serialization time; this
  was the MCP bug above.

## 11. Pitfalls

1. **Inherited permission mode.** This user's `settings.json` has `defaultMode: bypassPermissions`. Without
   `--permission-mode`, or with user settings loaded, no `can_use_tool` would ever arrive and every tool would run
   unasked. The adapter must always pass the mode, and should read back `current_permission_mode` from `initialize`.
   For hard guarantees it should use a `PreToolUse` hook, which sees calls in every mode.
2. **Inherited environment.** See §1: start from a clean environment.
3. **Echoed control responses.** With `--replay-user-messages`, every `control_response` the host writes comes
   back on stdout, with the same `request_id` and the key order of the host's JSON. Correlate responses only against
   ids the host issued itself (`bsf_*`). Never treat an unknown `control_response` as an error. I did not run without
   the flag to confirm the flag is the cause.
4. **Serve while awaiting `initialize`.** The CLI's MCP handshake runs before the `initialize` response (§3).
5. **MCP notifications** need a success response with an `mcp_response` that has no `id` (§8).
6. **`system/init` once per turn.** Do not treat it as "process started". Use the `initialize` response for that.
7. **Cost is cumulative per process**, interrupted turns lose their usage, and `costUSD` is at list price (§6).
8. **Thinking is empty** unless `--thinking-display summarized` is passed (`thinking_delta.thinking == ""`).
9. **Open-ended stream.** New message kinds and fields appear between versions, for example `rate_limit_event`,
   `thinking_tokens`, `abandoned_blocks`, `model_usage_since_last_result`. Skip unknown kinds. Answer unknown control
   request subtypes with an `error` response, or the CLI waits. CodeAlta sees `remote_control_work_secret` and
   answers it with an empty success. **Pin the CLI version** in the `HarnessRelease` catalog and record fixtures per
   version for the Replay adapter.
10. **`--permission-prompt-tool stdio` is hidden.** It works in 2.1.317, but the help now documents
    `--permission-prompts host`. Re-check on every pin bump.
11. **Concurrent stdout readers.** Handle control requests off the reader loop, as the spike does with `Task.Run`,
    and serialize writes to stdin with a lock. A permission prompt may wait minutes for a human.

## 12. Proposed `IHarnessAdapter` mapping (plan §5.4)

**One CLI process per active session.** Close it after an idle timeout, and resume it with `--resume`. A change of
model, cwd, tools or profile restarts the process; the permission mode can be changed live with
`set_permission_mode`.

```csharp
sealed class ClaudeCodeAdapter : IHarnessAdapter
{
    public HarnessCapabilities Capabilities => HarnessCapabilities.Resume | HarnessCapabilities.Queue
        | HarnessCapabilities.Fork /* --fork-session */ | HarnessCapabilities.Rollback /* --resume-session-at */
        | HarnessCapabilities.Compact /* "/compact" user message, untested */ | HarnessCapabilities.PlanMode /* --permission-mode plan */
        | HarnessCapabilities.Subagents /* parent_tool_use_id != null */ | HarnessCapabilities.McpInjection /* sdk servers via control protocol */;
    // Steer: not native. A message sent mid-turn is queued, not merged; emulate with interrupt + send.
}
```

| Adapter call | Claude Code |
|---|---|
| `StartAsync(spec)` | Spawn with the §1 arguments: `--session-id=<new uuid>` so BoSoFa knows the id up front, `--permission-mode <spec>`, `--model`, `--effort`, `--mcp-config` with sdk servers, cwd = workspace path, env with `CLAUDE_CONFIG_DIR` = profile dir. Send `initialize` (hooks: one `PreToolUse` callback for policy and live tool tracking; `sdkMcpServers`; `appendSystemPrompt`), then answer `mcp_message` while waiting. |
| `ResumeAsync(sessionId, at?)` | Spawn with `--resume=<id>`, plus `[--fork-session] [--resume-session-at <uuid>]`, then `initialize`. |
| `SendAsync(msg, Queue)` | Write `{"type":"user","message":{"role":"user","content":…},"parent_tool_use_id":null,"session_id":…}`. Content is a string or an array of blocks (text / image). The replay echo gives the persisted `uuid`. |
| `SendAsync(msg, Steer)` | `interrupt` then `user` (emulated). |
| `InterruptAsync` | `control_request {"subtype":"interrupt","cancel_queued":true}`, then wait for the turn's `result`. |
| `RespondAsync(id, r)` | `control_response` to the pending `can_use_tool`: `allow` + `updatedInput` [+ `updatedPermissions` for "always"], or `deny` + `message` [+ `interrupt`]. Answers to `AskUserQuestion` go back as `allow` with `updatedInput.answers` (CodeAlta). |
| set mode / model | `set_permission_mode {mode}`; `set_model` exists in the SDK (untested). |

| CLI line | `HarnessEvent` |
|---|---|
| host `user` write / replay echo | `RunStarted` (run id = replay `uuid`); a `system/session_state_changed: running` confirms it |
| `stream_event content_block_start` | `ItemStarted` (kind from `content_block.type`: text / thinking / tool_use; key = `api_message_id` + `index`) |
| `stream_event content_block_delta` (`text_delta`, `thinking_delta`, `input_json_delta`) | `ItemDelta` (ephemeral, plan §5.3: sent only when connected) |
| `assistant` message (per block) | `ItemCompleted` (durable): text, thinking, or tool call with its full input |
| `hook_callback PreToolUse` | policy check; `ItemStarted(ToolCall)` when the item is not already open |
| `control_request can_use_tool` | `RuntimeRequest(Permission{tool, input, description, suggestions, toolUseId})`, durable; `control_cancel_request` → `RuntimeRequestCancelled` |
| `user` + `tool_result` | `ItemCompleted(ToolResult{toolUseId, content, is_error, tool_use_result, permission_decision})` |
| `user` `[Request interrupted by user]` | marker only (folded into `RunEnded`) |
| `result` | `Usage` (per turn from `model_usage_since_last_result`) + `RunEnded(status: is_error ? Failed/Interrupted : Completed, disposition: terminal_reason)` |
| `rate_limit_event` | `Usage`/`QuotaUpdated` on the profile (`unifiedWindows`) |
| `system/init` | session metadata (tools, MCP status, model, permission mode); MCP `status:"failed"` → warning |
| `system/status`, `thinking_tokens`, `session_state_changed` | ephemeral status for the UI |
| `parent_tool_use_id != null` | the same items, nested under the subagent's tool call |
| unknown | dropped, but recorded raw in the session's debug log and the Replay fixtures |

Durable versus ephemeral (plan §5.3): `ItemCompleted`, `RuntimeRequest`, `RunEnded`, and `Usage` go to the outbox;
`stream_event` deltas and `status` are ephemeral.

## 13. Not tested (follow-ups)

`control_cancel_request`; `updatedPermissions`; deny with `interrupt:true`; a message sent mid-turn (queue
semantics); `set_permission_mode` / `set_model` / `get_context_usage`; images in user content; subagent events; a
`/compact` message and `system/compact_boundary`; `--fork-session` and `--resume-session-at`; hook denials;
`CLAUDE_CONFIG_DIR` login end to end, which would need an interactive OAuth; a daemon running as a launchd system
service reaching the user's keychain; Windows and Linux (`claude.exe` / npm shim resolution).
