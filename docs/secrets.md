# Secrets and variables

Status: all six steps of §8 are done (the store, the masker, references and resolution, masking, host-only secrets, and
machine limits, rotation reminders and environments, §10), and agents can list names (§4.3). Modeled on CodeAlta's secrets (`doc/fleet/secrets.md` there), without its end-to-end sealing: values
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
| `Machines` | the only machines whose sessions get it; empty for any (§10.1) | same |
| `RotateEveryDays` | due for rotation that long after it was set; 0 for never (§10.3) | same |
| Metadata | `Hint` (`…` and the last 4 characters of a value longer than 12, as accounts), `SetById`, `SetAt` | `SetById`, `SetAt` |

- **Resolution**: a session's creator's personal value wins over the shared one of the same name. So one shared MCP
  server definition (`Authorization: Bearer ${secret:GRAFANA_TOKEN}`) gives every person their own token, and falls
  back to the team's.
- **Relations** (`BoSoFa.Server.Domain/Secrets.cs`, generation 15, 18 with §10): `SecretEntry` keyed by `(OwnerId, Name)`, with
  `OwnerId = 0` for shared and an environment's id for its values, holding secrets and variables alike (`Kind`), and `SecretVersion` for the protected values a
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
- A host-only secret in a profile's `Environment` is refused at save time when the admin's own value of that name, or the
  shared one, is host-only, and always at resolution, where a person's own host-only value counts (§6).
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
  - `ResolvedMcpServer` with resolved env, headers, URL and arguments (done). It needs no host-only flag: the daemon's
    gateway keeps every MCP value from the harness (§6);
  - `Mask`: every value the response holds, plus the session's account secret, for §5 (step 4).
- The server resolves with the **session's creator** as the person, checks that the session runs on the asking machine
  and is not stopped (as the account lease does), and resolves nothing for anybody else.
- A reference to a missing secret or variable, or to a variable written as a secret, fails the session's start with an
  error that names all of them (`409` with the message; the daemon shows it on the session). An empty value is never used
  instead.
- The server agent resolves the same way inside its backend action (its HTTP MCP servers on the server, its stdio ones
  sent over machine RPC as today).
- **Audit**: one `SecretsUsed` event per resolution (session, machine, names, versions and scopes, never values): per
  start of a harness, and per run of the server agent. Its handler writes the `secret.used` record and the `SecretUse` row of
  each value and machine, which the Secrets page shows to whoever manages the value (§10.3).
- Rotation reaches new sessions and newly started MCP servers. A running harness keeps the value it started with
  until it is restarted.

### 4.3 What agents see

Done. Names only: so an agent can write `${secret:NAME}` into a profile or ask the person to set one. Nothing reads or sets a value.

- Daemon harnesses get `bosofa_list_secrets` on the session's MCP endpoint (`McpEndpoint.SecretsTool`, pre-approved for
  Claude Code), the server agent `list_secrets` (`AgentToolbox.SecretsTool`, in every permission mode, needing nobody).
- Both answer what `SecretResolver.Names` finds for the session's creator on its machine, as a reference would resolve: each
  name once, with the scope that wins (the profile's environment if it is admitted, then the person's, then the shared one),
  its kind and whether it is for MCP servers only. A value for other machines is left out, and so are other people's.
- The daemon asks `GET /api/daemon/v1/sessions/{id}/secrets` at each call, so a value set meanwhile is listed; the server
  answers only the session's machine, and not for a stopped session. The server agent reads it at each call too.
- Listing is not audited: it names no value and sends none anywhere.

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

Done. One `SecretMasker` per daemon (`SessionHost.Masker`), which keeps every value it is given for as long as it runs:

- **What it is given**: the secrets among a profile's resolved values (`SessionProfileResponse.Mask`; the server sends the
  values of `${secret:…}` references and of older inline MCP values, never variables, so `eu-west` stays readable), the
  secrets of the accounts it leases, and those of each server agent tool call (`ToolRunRequest.Mask`).
- **One choke point before the outbox**: everything a session reports goes through `SessionHost.Append`, which masks it
  (`MaskReport`: `ItemReport.Text`, `Data` as JSON, `StatusReport.Error`, `RunEndedReport.Outcome`,
  `ApprovalRequestedReport.Input` as JSON and `Description`) before `Outbox.Append` writes it to disk. A run's diff goes
  through it too.
- **Live text**: `SessionHost.Delta` masks each item's deltas with a stream of its own, so a secret split across deltas
  is caught; the item's final report replaces its stream.
- **Server agent tools**: `AgentToolHost` masks a call's result before it goes back; its streamed output goes through
  `SessionHost.Delta`.
- **Log**: `MaskingWriter` wraps the session host's log and the link's.

### 5.3 On the server, as the second line

Done. The event log is kept forever, so a value that reaches it can never be removed. `ServerSecretMasker` holds every
secret the server has: people's and shared secrets, their values replaced in the last 30 days (`SecretVersion`), harness
accounts, and values older MCP definitions keep. It reads them again at the next use after a handler signals a change
(`IDomainSignals.SecretsChanged`: a secret set, an account added, MCP values moved); a value read once stays masked. It
masks, before publishing:

- `DaemonReportsReceived` (`MachineEndpoints.StoreReportsAsync`), before it becomes `SessionItem` rows and events, and the
  live deltas a daemon relays, one delta at a time (the daemon already masked them as a stream);
- the server agent's tool results before they enter its transcript, which is what the model is given next, and
  everything it records (`AgentItemsRecorded`, `AgentRunFinished`, `AgentApprovalRaised`) and streams.

Not done: masking the server's own logs (a masking logger provider). They hold no session output today.

### 5.4 What masking does not do

It stops values from landing in transcripts by accident. It does not stop an agent that has a value in its shell from
leaking it on purpose: `rev`, a substring, a `curl` to another host. When an agent must not hold a credential, the
secret is host-only (§6).

## 6. Host-only secrets

Done. A secret set with **For MCP servers only** (`SetSecretRequest.HostOnly`, `SecretEntry.HostOnly`; secrets only, and each
version says it again) goes where MCP servers take it: their environment, headers, URL. It never goes into a session's
environment: `SecretResolver` refuses it there (`shell: true`, the profile's `Environment`) and the session does not start,
saying so; saving a profile already refuses one the admin can see. The Secrets page shows such a secret as
**MCP servers only**. Tests: `SecretTest` (refusals), `AgentMachineTest` (a stdio server gets its host-only secret, the
shell's `env` has no trace of it), `AgentSessionTest` (the remote server gets its host-only header), `ProfileTest` (a
host-only MCP secret of a replayed Claude Code session is neither in its MCP configuration nor in any file of the daemon),
and `HarnessMcpGatewayTest` (the real Claude Code and Codex: not on any command line either).

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
  A secret can be **For MCP servers only** (§6). **Settings** sets a value's machines and rotation interval without a new
  version; each row shows where the value last went, and what is due for rotation (§10).
- The profile editor has an **Environment** section for variables, and picks the environment (§10.2) its sessions use.
- Never hard-code the product name in what people see (`Brand`).

## 8. Steps

1. **Store** (done): domain relations and events, Data Protection purpose, endpoints, the Secrets and variables page.
   `SecretTest`: scopes and admin rights, kinds, rotation and deletion, replaced values kept for masking, a ciphertext
   moved to another secret or version does not open, and no value in an API answer, the audit or the server's data.
2. **Masker** (done) in `BoSoFa.Protocol` with `SecretMaskerTest` (forms, JSON validity, every cut and random cuts of a
   stream, in place, exceptions, minimum length, retain), plus the `--mcp-config` file fix (done with §6).
3. **References and resolution** (done): profile `Environment`, references in MCP servers, migration of inline values,
   `SessionProfileResponse` growth, errors that name a missing reference, `secret.used` audit.
4. **Masking everywhere** (done, but for the server's own logs): the daemon's choke point and logs, the server's second line, the server agent's tool
   results and model input. End-to-end (`AgentMachineTest`, on the recorded provider streams): a session whose profile
   gives `DEPLOY_TOKEN` runs `echo "$DEPLOY_TOKEN in $AWS_REGION"`; the transcript says `*** in eu-west`, no model request
   holds the value, and neither the daemon's data directory nor the server's event log, rows or blobs contain it.
   `DaemonMaskingTest` covers the outbox, the deltas and the log; `SecretTest` the server masking a report a daemon did
   not mask, with a rotated secret's earlier value.
5. **Host-only** (done): the flag and the refusals (the daemon's MCP gateway for harness sessions is done). Test: a Claude Code (replay) and
   a server agent session reach a host-only HTTP MCP server, and neither the harness's command line, its config files
   nor the shell's environment contain the value.
6. **Machines, rotation and environments** (done, §10): a machine list per value, where values went and rotation reminders,
   and environments with reviewers and machines. `EnvironmentTest`: a value goes to its machines only (a wider one serves the
   others), settings change without a version, a removed machine flags what it held until rotated; an environment's session
   waits with nothing sent to its machine, only a reviewer other than its creator sees and answers it, approval starts it
   and its values win, rejection fails it, a stop withdraws it, its machines are enforced at start and resolution, and an
   environment a profile names cannot be deleted.
7. **Names for agents** (done, §4.3): `bosofa_list_secrets` and the server agent's `list_secrets`. `SecretNamesTest`: the
   names a session's creator can refer to on its machine (their own limited to another machine gives way to the shared one,
   an environment's comes first, other people's are left out), refused to another machine, and never a value in the answer.

## 9. Limits

- Harnesses keep their own transcripts on the machine (`CLAUDE_CONFIG_DIR/projects/*.jsonl`, `CODEX_HOME/sessions`).
  They are written by the harness before BoSoFa sees anything, so a value the shell printed is there unmasked. It is on
  the machine the value was given to, which is no new exposure, but it is not masked.
- The person's own terminals on a machine are theirs and are not masked.
- The server can read every value (it holds the key ring). This is the deliberate trade against CodeAlta's sealing:
  a value set before a machine enrolls works on it, and no device has to approve a new machine.

## 10. Machines, rotation and environments

Done. Generation 18 (`SecretUse`, `SecretEnvironment`, `EnvironmentApproval`; new fields of `SecretEntry` and `Profile`).

### 10.1 A value's machines

- `SecretEntry.Machines`: the machines whose sessions get the value; empty for any. A person picks among their own machines,
  an admin among all for shared values and those of environments (`SecretSettingsRequest`, `PUT …/{name}/settings`, or with
  the value in `SetSecretRequest`; a rotation that names none keeps them).
- Resolution skips a value that is not for the session's machine and goes on to the next scope, so a personal token kept to
  `build-mac` lets the team's shared one serve the laptop. With nothing left, the start names it: "the secret X is not for
  this machine".
- A server agent session without a machine keeps every value on the server, so any limited value serves it.
- Ids of removed machines stay in the list (ids are never reused), so removing a machine never widens a value to every
  machine.

### 10.2 Environments

A named set of secrets and variables (`SecretEnvironment`, CodeAlta's and GitHub's environments), such as `production`, that
admins keep under **Environment** on the Secrets page (`/api/v1/environments`):

- **Values** are `SecretEntry` rows owned by the environment's id (people and environments get ids from the same allocator),
  so masking, rotation, retired versions and the audit work as for the others. A profile names the environment
  (`ProfileDto.EnvironmentName`), and its value of a name comes first, then the person's, then the shared one.
- **Machines**: its sessions start only on them (empty: any); creating a session elsewhere is refused with a message, and
  resolution checks again, so narrowing an environment stops later starts of sessions already approved.
- **Reviewers**: when it has any, `SessionCreated` holds the session (`EnvironmentApproval`, `Pending`, with the machine's
  start command, or nothing for a server agent, whose loop is then not claimed): its status stays `Starting`, nothing goes
  to its machine, it takes no message, and a notice names who may approve it. A reviewer other than the session's creator
  approves (the command is issued, or the loop claimed) or rejects (the session fails, saying so) under **Waiting for your
  approval** on the Secrets page; the sidebar says when something waits (`/api/v1/environment-approvals`, polled with the
  other lists). A stop withdraws the request. The approval holds for the session's life, restarts included; resolution
  refuses a session that lacks one, so reviewers added later stop sessions started without them.
- The reviewer sees who asks, the profile, harness, machine and folder, never the prompt or title: those stay the creator's.
- An environment a profile names, or a session waits for, cannot be deleted; deleting one retires its values like a delete.
- Audit: `environment.created`, `environment.changed`, `environment.deleted`, `environment.requested`,
  `environment.approved`, `environment.rejected`.

Not done: approvals bound to branches or automations (there are none yet), and a reviewer seeing more of the session.

### 10.3 Where values went, and rotation

- `SecretUse (OwnerId, Name, MachineId)` keeps the last version and time each value went to each machine. Its owner, or an
  admin for shared and environment values, sees it on the value's row.
- `RotateEveryDays` makes a value due that long after it was set (`SecretDto.RotateBy`); the page marks it
  **rotation due** and counts what is due at the top.
- Removing a machine flags every secret whose current version it got (`RotationReason`: "It was given to laptop, which was
  removed."), and forgets its `SecretUse` rows. A new version clears the flag.
