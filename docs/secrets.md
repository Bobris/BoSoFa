# Secrets and variables

Status: steps 1 to 3 of §8 (the store, the masker, references and resolution) and the daemon's MCP gateway (§6) are done; the rest is planned. Modeled on CodeAlta's secrets (`doc/fleet/secrets.md` there), without its end-to-end sealing: values
live on the server, protected by .NET Data Protection, as harness accounts already are (plan §5.4.1). No external secret
store (Key Vault secrets, Vault, 1Password) is supported.

## 1. What and why

Today a value a session needs is either a harness account (plan §5.4.1) or a value typed into one MCP server of the
catalog (`McpServer.ProtectedSecrets`). Nothing else is possible: a profile cannot give the agent's shell a
`GRAFANA_TOKEN`, two MCP servers cannot share one token, every person shares the admin's value of an MCP server, and
nothing rotates or audits a value.

- A **secret** is a named value that is written and used, never read back: not by the UI, the API or an agent.
- A **variable** is a named value that is not secret, readable and editable (`GRAFANA_URL`).
- Configuration **refers to them by name**: `${secret:NAME}` and `${var:NAME}`, so no definition holds a value again.
- Whatever a session returns is **masked**: a value is replaced by `***` before it is written to the outbox, the event
  log, the live stream, a log, or sent to a model.
- A **host-only** secret reaches MCP servers only, never the agent's shell.

Harness accounts stay as they are: they have a kind, an expiry, a lease and limits of their own. Their values join the
masking (§5).

## 2. Model

| Field | Secret | Variable |
| --- | --- | --- |
| `Name` | `[A-Za-z_][A-Za-z0-9_]{0,63}`, like an environment variable; one kind per name | same |
| `Scope` | `Personal` (its owner's sessions) or `Shared` (set by an admin, every member's sessions) | same |
| `Version` | 1, +1 on every rotation | +1 on every change |
| Value | `ProtectedValue`: Data Protection ciphertext | `Value`, in clear |
| `HostOnly` | MCP servers only, never the shell | n/a |
| Metadata | `Hint` (`…` and the last 4 characters of a value longer than 12, as accounts), `SetById`, `SetAt` | `SetById`, `SetAt` |

- **Resolution**: a session's creator's personal value wins over the shared one of the same name. So one shared MCP
  server definition (`Authorization: Bearer ${secret:GRAFANA_TOKEN}`) gives every person their own token, and falls
  back to the team's.
- **Relations** (`BoSoFa.Server.Domain/Secrets.cs`, generation 15): `SecretEntry` keyed by `(OwnerId, Name)`, with
  `OwnerId = 0` for shared, holding secrets and variables alike (`Kind`), and `SecretVersion` for the protected values a
  rotation or a deletion replaced, kept 30 days and at most 16 per secret, for masking only (§5.3). (`Secret` would clash
  with `Microsoft.AspNetCore.DataProtection.Secret` wherever values are protected.)
- **Events**: `SecretSet` and `SecretDeleted`, for both kinds. The endpoint reads the current row, makes the ciphertext for
  the next version and publishes it with that version and the secret's id; the handler takes it only if the row is still at
  the version before (else `Conflict`, and the endpoint tries again), so handlers stay deterministic. A name keeps its
  kind: making a secret a variable means deleting it first. Every change is audited (`secret.set`, `secret.rotated`,
  `secret.deleted`), never with its value.

## 3. Storage: Data Protection only

- Each value is protected by `IDataProtector` with purpose `BoSoFa.Secret.v1` and sub-purposes the secret's id and its
  version. A ciphertext can then not be swapped into another secret, another person's secret of the same name, or
  another version.
- The ciphertext lives in the `SecretSet` event and the `Secret` row, as an account's does. The same consequences hold
  (§5.4.1 of the plan):
  - The event log keeps every version's ciphertext forever; deleting or rotating removes the row, not the event.
  - The key ring is what keeps them unreadable: on AKS in Blob, wrapped by the Key Vault key (`NodeStorage`); on a
    single node the files under `data/keys`. Never delete old keys.
- A value is unprotected only when a session is resolved (§4), on the node that answers, and kept in memory for that
  request alone.
- **Nothing returns a value**: DTOs carry name, scope, version, hint, host-only, who set it and when, and where it is
  used (profiles, MCP servers).

## 4. References and delivery

### 4.1 Where references are written

| Where | Example |
| --- | --- |
| Environment variable or header of an MCP server of the catalog (`PublishMcpServer.Values`) | `GRAFANA_TOKEN = ${secret:GRAFANA_TOKEN}`, `Authorization = Bearer ${secret:GRAFANA_TOKEN}` |
| URL of an MCP server | `${var:GRAFANA_URL}/mcp`, or a secret |
| Command or argument of an MCP server | `${var:GRAFANA_URL}`, never a secret (a command line is visible to every user of the machine) |
| `Environment` of a profile: the harness process, and so the agent's shell, and the server agent's shell tool | `VAULT_TOKEN = ${secret:VAULT_TOKEN}`, `AWS_REGION = ${var:AWS_REGION}` |

Done. At save time (`CatalogEndpoints`):

- Every `${` must begin a reference: `${secret:NAME}` or `${var:NAME}`; `${env:…}` and malformed ones are refused.
- A value whose name looks like a credential (`token`, `passw`, `secret`, `key`, `authorization`, `cookie`, `credential`)
  must contain a `${secret:…}`: `Bearer ${secret:X}` passes, a literal is refused.
- Environment names are like environment variables, header names HTTP tokens.
- Whether a reference is set is not checked at save time: it depends on whose session it is.
- A host-only secret in a profile's `Environment` will be refused at save time and at resolution (step 5).
- **Migration** (`McpValueMigration`, once at start on every node; a definition already moved is left alone): the values
  older definitions keep encrypted in themselves become **shared** secrets `<SERVER>_<NAME>` (uppercase, other characters
  `_`), set by "the server", and the definition refers to them (`McpValuesMoved`). Shared, because every member's sessions
  used those values before; a person can set their own under the same name. A shared secret of that name that exists is
  kept. Until a definition moves (its key ring cannot open it, say), its values keep working and the catalog names them.

### 4.2 Resolution

- The daemon already asks for a session's profile just in time (`SessionProfileResponse`). Resolution grows:
  - `Environment`: the profile's variables with references resolved (done); the daemon gives them to Claude Code, Codex
    and ACP agents under its own variables, which win, and the server agent sends them with each shell tool call
    (`ToolRunRequest.Environment`);
  - `ResolvedMcpServer` with resolved env, headers, URL and arguments (done), and whether each value is host-only (step 5);
  - `Mask`: every value the response holds, plus the session's account secret, for §5 (step 4).
- The server resolves with the **session's creator** as the person, checks that the session runs on the asking machine
  and is not stopped (as the account lease does), and resolves nothing for anybody else.
- A reference to a missing secret or variable, or to a variable written as a secret, fails the session's start with an
  error that names all of them (`409` with the message; the daemon shows it on the session). An empty value is never used
  instead.
- The server agent resolves the same way inside its backend action (its HTTP MCP servers on the server, its stdio ones
  sent over machine RPC as today).
- **Audit**: one `secret.used` record per resolution (session, machine, names, versions and scopes, never values): per
  start of a harness, and per run of the server agent. The Secrets page showing recent uses is still to come.
- Rotation reaches new sessions and newly started MCP servers. A running harness keeps the value it started with
  until it is restarted.

### 4.3 What agents see

- Names only: a `bosofa_secrets_list` tool on the session's MCP endpoint lists names, kinds and host-only flags, so an
  agent can write `${secret:NAME}` into a profile or ask the person to set one. Nothing reads or sets a value.

## 5. Masking

### 5.1 The masker

`SecretMasker` in `BoSoFa.Protocol` (pure code, AOT-compatible, shared by the daemon and the server; done), after
CodeAlta's `AgentSecretMasker`:

- Ordinal replacement of each value by `***`, found with one `SearchValues<string>`; where values begin one another, the
  longest wins. Values shorter than 4 characters are not masked.
- From 8 characters on, also the forms a value takes in text: escaped in JSON (`JavaScriptEncoder.Default` and
  `UnsafeRelaxedJsonEscaping`, with upper- and lowercase `\uXXXX`), URL-encoded (`%XX` both cases, and `+` for spaces), and
  in Base64 (with and without padding, Base64url), including the start of the Base64 of a longer text that begins with it
  (`echo $TOKEN | base64`, up to the value's last whole 3-byte group).
- `MaskJson` masks inside strings only, so a secret that looks like a number of the JSON leaves the JSON valid.
- `CreateStream()` for deltas: holds back the end of a part that could begin a value; a value that begins before that end
  is complete already and is masked whole. Masking a text cut anywhere shows what masking it whole shows (tested at every
  cut and on random cuts).
- `Retain(value)`: once given, a value stays masked for the life of the masker, also after rotation or deletion.
- `MaskInPlace` for log lines, `MaskException` (a `MaskedException` that tells the same, masked) for exceptions that reach
  logs or telemetry.

### 5.2 On the daemon

- **One choke point before the outbox**: `SessionHost.Report` masks every report (`ItemReport.Text`, `Data` with
  `MaskJson`, `StatusReport.Error`, `ApprovalRequestedReport.Input` and `Description`) before `Outbox.Append`, which
  writes to disk.
- The live deltas the harness sessions send (`LiveMessages`) go through a per-item `CreateStream()`.
- Machine tools of the server agent (`ShellTool`, file tools, stdio MCP results) are masked in `AgentToolHost` before
  they return.
- The daemon's own log writer masks lines in place.
- The values are those of the session's resolution, retained for the life of the daemon.

### 5.3 On the server, as the second line

The event log is kept forever, so a value that reaches it can never be removed. The server masks again before
publishing:

- `DaemonReportsReceived` before it becomes `SessionItem` rows and events, and the live relay to the UI;
- the server agent's tool results before they enter its transcript **and before they go to the model**, and the
  model's own text deltas;
- server logs, through a masking logger provider.

The server's masker holds the values of the person's scope (personal and shared secrets, their accounts), read again
when a `Secret*` event commits, and earlier versions from `SecretVersion` for 30 days (at most 256), because a running
session may still print the old value.

### 5.4 What masking does not do

It stops values from landing in transcripts by accident. It does not stop an agent that has a value in its shell from
leaking it on purpose: `rev`, a substring, a `curl` to another host. When an agent must not hold a credential, the
secret is host-only (§6).

## 6. Host-only secrets

What "host-only" can promise depends on where the MCP server runs, because the agent's shell runs as the same OS user
as the daemon:

| Session | MCP server | Where the value is | Strength |
| --- | --- | --- | --- |
| Server agent | HTTP | Server memory only | Strong |
| Server agent | stdio on the daemon | Environment of the MCP process | Weak: same user, `/proc/<pid>/environ`, `ps eww` |
| Claude Code, Codex, ACP | HTTP, through the gateway | Daemon memory only | Strong |
| Claude Code, Codex, ACP | stdio, through the gateway | Environment of the MCP process, a child of the daemon | Weak, as above |

**The MCP gateway in the daemon (done)**: no harness is told a server's environment or headers. `McpEndpoint.ServersFor`
registers the profile's servers with `McpGateway` and gives the harness, for each, `http://localhost:<port>/mcp/<session>/<server>`
with the session's bearer:

- A stdio server is started by the daemon (`StdioMcpServers`, as for server agent sessions) in the session's working
  directory, and its JSON-RPC is relayed. `initialize` gets the server's own answer; requests the server sends to its
  client are refused, and a harness's `notifications/cancelled` is not passed on. The gateway answers `server/discover` (the
  probe of the stateless protocol, which Claude Code sends first) with "method not found" itself, so the harness falls back
  to `initialize` at once.
- A remote server is called by the daemon: the Streamable HTTP exchange passes through (event streams, `Mcp-Session-Id`),
  with the server's headers in place of the bearer. A 401 loses its `WWW-Authenticate`, so the harness does not try to sign
  in against the daemon.
- Claude Code's `--mcp-config` is a `0600` file under the daemon's `mcp/` folder, no longer a command-line argument, which
  every user of the machine could read with `ps`. It holds only the gateway's addresses and the session's bearer; the folder
  is emptied when the daemon starts, since the bearers were of the previous run.

`HarnessMcpGatewayTest` runs the real Claude Code and Codex, where installed, against the gateway: each calls a tool of a stdio
server holding a secret without the model (`mcp_call`, `mcpServer/tool/call`), so it needs no account and costs nothing.

HTTP MCP servers are the recommended shape for host-only secrets. A stdio one gets an honest "same user can read it" note
in the UI until machines have a sandbox.

## 7. API and UI

- `GET /api/v1/secrets` lists the caller's own secrets and variables, then the shared ones. `PUT
  /api/v1/secrets/{scope}/{name}` (`Personal` or `Shared`) sets or rotates, with `{ kind, value }` in the body, never in
  the URL; `DELETE` removes. Shared is admin-only. Done (`SecretEndpoints`).
- **Secrets and variables** page (`/secrets`, in the settings menu): the caller's values, then the shared ones (editable by
  admins). Each row shows the name, the reference (a click copies it), the hint or the variable's value, the version, and
  who set it and when; **Rotate** (or **Edit**) opens a write-only field in place, and delete asks beside its row. Done.
  Still to come with the steps that need them: host-only, where a value is used, and recent uses.
- The profile editor gets an **Environment** section; MCP server editors accept references and warn about a missing
  one.
- Never hard-code the product name in what people see (`Brand`).

## 8. Steps

1. **Store** (done): domain relations and events, Data Protection purpose, endpoints, the Secrets and variables page.
   `SecretTest`: scopes and admin rights, kinds, rotation and deletion, replaced values kept for masking, a ciphertext
   moved to another secret or version does not open, and no value in an API answer, the audit or the server's data.
2. **Masker** (done) in `BoSoFa.Protocol` with `SecretMaskerTest` (forms, JSON validity, every cut and random cuts of a
   stream, in place, exceptions, minimum length, retain), plus the `--mcp-config` file fix (done with §6).
3. **References and resolution** (done): profile `Environment`, references in MCP servers, migration of inline values,
   `SessionProfileResponse` growth, errors that name a missing reference, `secret.used` audit.
4. **Masking everywhere**: the daemon's choke point and logs, the server's second line, the server agent's tool
   results and model input. End-to-end test on the recorded provider streams: a session whose profile gives
   `GRAFANA_TOKEN` runs `echo grafana=$GRAFANA_TOKEN`; the transcript says `grafana=***`, no model request holds the
   value, and neither the daemon's data directory nor the server's event log, rows or blobs contain it.
5. **Host-only**: the flag and the refusals (the daemon's MCP gateway for harness sessions is done). Test: a Claude Code (replay) and
   a server agent session reach a host-only HTTP MCP server, and neither the harness's command line, its config files
   nor the shell's environment contain the value.
6. **Later**: environments with approvals and machine labels (CodeAlta's `environment = "production"`), a machine
   allow-list per secret, and rotation reminders.

## 9. Limits

- Harnesses keep their own transcripts on the machine (`CLAUDE_CONFIG_DIR/projects/*.jsonl`, `CODEX_HOME/sessions`).
  They are written by the harness before BoSoFa sees anything, so a value the shell printed is there unmasked. It is on
  the machine the value was given to, which is no new exposure, but it is not masked.
- The person's own terminals on a machine are theirs and are not masked.
- The server can read every value (it holds the key ring). This is the deliberate trade against CodeAlta's sealing:
  a value set before a machine enrolls works on it, and no device has to approve a new machine.
