# Shell commands in the prompt (`!`)

Status: built (2026-11-14), decisions in §6. Not in it yet: the prompt of a new session (§5, later). Modeled on CodeAlta 4.4's shell commands (`b7e0f9ee9` "Add shell commands to
the prompt with !", #435, and `e7fb1d857` for the prompt of a new session; user guide `site/docs/workspace/prompt.md`
§ Shell commands there).

## 1. What CodeAlta does

- A `!` typed or pasted at the start of an **empty** prompt is not written. The prompt switches to shell mode instead:
  an orange frame, a `!` sigil, the status "Shell command · Enter runs it · Backspace on an empty line returns to the
  prompt", and an orange Run button. `/shell` does the same. In shell mode `Alt+↑` recalls earlier commands, not prompts.
- Enter runs the command **at once, whatever the session is doing**. It never goes into the session or its queue. It runs
  in the session's folder or worktree, in the shell of the agent's `shell_command` tool, with the session's variables. It
  is the user's own command: no permission is asked and the session's sandbox does not apply. stdin is closed.
- A panel over the timeline streams the output, then shows the exit code and how long it took. **Stop** kills the whole
  process tree. Closing the panel does not stop the command. Up to 4 commands run at once per session, and 50 are kept.
- Once a command has ended, its output is **attached to the next prompt** as a text attachment (`$ git status`). The user
  can remove it, or toggle **In the prompt** in the panel. The model gets a header ("The user ran this command in a shell,
  in `<folder>`; it exited with code 0."), the command and the output. It gets at most the first 10,000 and the last
  20,000 characters, and ANSI escapes and `\r` rewrites are removed.
- A **!** button above the prompt counts the commands, with a spinner while one runs. It opens the list: copy, paste into a
  terminal, run again, remove.
- Host side (`ShellCommandsService`, NeoAstra RPC `shellCommands.run`): one streaming channel per command, which sends
  `started{folder}`, `output*`, then `exit{code, omitted, tail}` or `error{status}`. At most 512 K characters are
  streamed and the last 20 K come with `exit`. Closing the channel kills the command.
- CodeAlta writes nothing to the session: the commands live only in the window and are gone when it closes.
- The prompt of a session not started yet runs the command in the project's folder, with the identity of the space the
  window shows. Its output goes with the prompt that starts the session.

## 2. How it maps to BoSoFa

BoSoFa is server-centric and multi-client, so three things change:

| CodeAlta | BoSoFa |
| --- | --- |
| The host runs the command in its own process | The session's **machine** runs it. The server asks through machine RPC, from a backend action |
| The commands live in the window | They are **transcript items** (`ShellCommand`, `ShellOutput`). Every client of the creator sees them, and they survive a reload. The live output is ephemeral deltas, as for tools |
| The page builds a text attachment | The next message names the outputs it carries (`SendMessageRequest.ShellOutputs`). The **domain handler** puts them into the text the harness gets, deterministically |

Almost everything needed already exists:

- `ToolRunRequest` with `Tool = shell` (`AgentToolHost` → `ShellTool`) runs a command in a folder of the machine with
  resolved variables. It closes stdin, kills the process tree, keeps the start and end of the output within
  `MachineTools.OutputLimit`, streams deltas under a `LiveRef`, masks secrets, can be joined again by its `Key` after a
  dropped connection, and stops with `ToolCancelRequest`. The daemon counts it as busy, so it is not updated meanwhile.
- `AgentMachine.RunAsync` handles offline machines and reconnects for such a call.
- `/btw` (`SessionSideQuestionAsked` → `SideQuestion`/`SideAnswer` items paired by `Ref`, `SideQuestionCard`) is the
  model for an item pair that sits apart from the conversation, runs beside any run, and is answered later.
- `Session.WorkingDirectory` is already the worktree once the daemon reports `WorkspaceReport`.
- Terminals (`TerminalOpenRequest`, `TerminalInputRequest`) give "Open in a terminal" for free.

## 3. Behavior

- **Where:** a session that runs on a machine (`MachineId != 0`), every harness, the server agent included. The command
  runs in `Session.WorkingDirectory` on that machine. A session without a machine does not offer `!`.
- **Who:** the person who may write to the session, the same as for a message (`Sessions.Controlled`: the creator, on a
  machine they may control). It is the user's command, so no approval is asked and the permission mode does not apply. It
  is audited (§4.4).
- **When:** any status but `Failed`, so also while a turn runs, while one waits for an approval, and once the session has
  stopped (to look at its worktree). A folder that is gone says so.
- **Environment:** `ShellTool`'s own: a clean environment plus the developer variables, the session's profile variables
  with references resolved for its creator (host-only secrets refused, as for the agent's shell), `TERM=dumb`,
  `NO_COLOR`, `PAGER=cat` and `GIT_TERMINAL_PROMPT=0`. A harness session gets the values as they are now, which may be
  newer than the ones its harness started with (secrets.md §4.2). Output is masked on the daemon and again on the server.
- **Limits:** a command of at most 16,384 characters, 4 running per session, 8 outputs per message, and 10 minutes
  (`MachineTools.LongestShellSeconds`). The output is what `ShellTool` keeps (start and end within
  `MachineTools.OutputLimit`, about 15 K characters). The live stream is cut at 256 KB.
- **Into the conversation:** every output that ended since the user's last message is offered on the next one, and
  included by default. The model reads, before the user's own text:

  ```text
  The user ran this command in a shell, in /src/app; it exited with code 1.

  $ dotnet test
  <output>
  ```

  "it exited with code N", "the user stopped it", "it ran out of time" or "it could not run: …". It reaches the harness
  once, with that message. The transcript's `User` item keeps the user's own text and lists the outputs it carried.

## 4. Design (as built)

### 4.1 Protocol (`BoSoFa.Protocol`)

- `SessionItemKind.ShellCommand` (`Text` the command, no `Ref`) and `ShellOutput` (`Ref` `shell-<command seq>`, `Text` the
  masked output, `IsError` unless it exited with 0, `Data` a `ShellOutputData { Command, Folder, State, ExitCode,
  Milliseconds }` with `State` `done`, `stopped`, `timedOut` or `failed`). The command's live output streams under the
  output's ref. The command item carries none, because a page drops the deltas of a ref an item already has.
- `RunShellCommandRequest(string Command)`. `SendMessageRequest.ShellOutputs` holds the seqs of the **`ShellOutput`** items
  that go with the message.
- `ToolRunResult` gains `ExitCode`, `TimedOut` and `Stopped`. A command stopped on request (`ToolCancelRequest`) now answers
  with what it printed and `[Stopped.]`, instead of throwing it away.
- `ShellCommands` (`ShellCommands.cs`) holds the limits (16,384 characters, 4 running, 8 per message), `RefOf`, `Context`
  (the block the model reads) and `Prepend`. `Prepend` puts the outputs before the text and shortens each in its middle
  so that all of them fit in the prompt limit.

### 4.2 Domain (`BoSoFa.Server.Domain/ShellCommands.cs`)

- Events: `SessionShellCommandRun`, `SessionShellCommandStopRequested`, and `SessionMessageSent.ShellOutputSeqs`.
- Relation `ShellCommandRun (SessionId, Seq) → ActionId`: a row per running command. It gives the limit and the action
  that Stop cancels. `ApplicationGeneration` is 19.
- Backend action `session-shell-command` (`SessionShellCommand { SessionId, Seq }` → `SessionShellCommandResult`).
  `ShellCommands.Finished` records the `ShellOutput`, works out the duration from the command item's time, and removes
  the row. An action cancelled before it started records `stopped`.
- Running checks `Controlled`, a machine, a status other than `Failed`, and the limit. It then appends the item, enqueues
  the action and records `session.shell_command` in the audit with the command cut to 200 characters.
- A message's outputs must be distinct `ShellOutput` items of the session, at most 8. The harness gets `Prepend(...)`.
  The `User` item keeps the person's text, with `Ref` set to `shell:<seqs>` so the page shows what it carried.
  `AgentRunners.UserReplay` keeps the text the model gets whenever it differs from the transcript's.
- Stopping the session and removing its machine stop its running commands.

### 4.3 Server (`BoSoFa.Server`)

- `POST /api/v1/sessions/{id}/shell-commands` and `POST /api/v1/sessions/{id}/shell-commands/{seq}/stop`.
- `Sessions/ShellCommandHandler`:
  - It resolves the profile's environment for the session's creator. `CatalogEndpoints.EnvironmentOf` refuses host-only
    secrets, and `SecretsUsed` is recorded.
  - It runs the command through `AgentMachine.RunCommandAsync`: the shell tool with no checkpoint and a key of
    `shell-<session>-<seq>`, which another attempt joins.
  - On Stop it sends `ToolCancelRequest` and returns the daemon's answer as `stopped`, with the output so far. An action
    returns a result whatever the reason it was cancelled, so the result is kept.
  - On an interruption (its node going away) it leaves the command running for the next attempt.
  - It strips the shell tool's own `[Exit code N]` and `[Stopped…]` lines and masks the output again.

### 4.4 Daemon

`ShellTool` sets `ExitCode`, `TimedOut` and `Stopped`. Nothing else changed: harness sessions and the server agent share
`ToolRunRequest`.

### 4.5 SPA (`web/apps/web`, also the desktop app)

- `components/ShellCommands.tsx`: `ShellCommandCard`, `ShellOutputChips`, `CarriedOutputs` and their helpers.
  - The card shows `! command`, the live output, Running and the seconds so far, and **Stop**. Once the command has ended
    it shows the exit code or how it ended, the time it took, the output (plain text, folded past 12 lines), **Copy
    output**, **Run again** and **Open in a terminal**.
  - **Open in a terminal** opens a terminal on the machine in the folder and types the command, without Enter, once the
    shell's prompt is drawn (`TerminalView.typeOnce`).
- `Composer` (`pages/Session.tsx`):
  - A `!` typed, pasted or put in by an input method at the start of an empty prompt switches to shell mode: an amber
    frame and sigil, a monospace prompt, a status line and a Run button.
  - Backspace or Escape on an empty line leaves it, and ↑/↓ recall the session's commands. `/shell` also switches to it.
  - Chips above the editor show the outputs since the last message, except those of commands that never ran. A message
    carries those that are left (`shellOutputs`).
  - The footer counts running commands. That count is here rather than in `SessionBar`.
  - In a stopped session the prompt takes only commands.

## 5. Steps

All done (2026-11-14).

1. **Daemon exit code**: `AgentToolHostTest`. `exit 3` gives `ExitCode = 3`, a timeout has `TimedOut` and no code, and a
   stopped command keeps what it printed.
2. **Domain and API**: `ShellCommandTest`, against a real daemon:
   - a server agent session's command runs in its folder with its profile's variables, and the secret is masked in the
     item, the audit and the API;
   - another person gets 404, and a blank command, one that is too long or a session without a machine gets 400;
   - 4 run at once and a fifth is refused. Stop keeps the output, and stopping the session stops the rest;
   - an offline machine says the command did not run;
   - a replay (harness) session runs commands in its folder.
3. **Into the next message**: `ShellCommandTest`:
   - the model's request has the output once and a later message has none;
   - a command's own item and an output named twice are refused;
   - `Prepend` is checked for its text and its limit.

   The text a harness on a machine receives is not checked by a test. It is the same `Prepend` result, sent in
   `SendMessageCommand`.
4. **SPA**: checked in the browser against a server on a scratch data folder and a daemon with `--replay-fixture`. A
   command ran while a turn waited for an approval. Live output, Stop, chips, recall, Backspace, a message carrying two
   of three outputs, and Open in a terminal all worked, in light and dark, with no `[CODE]` diagnostics.
5. **Docs**: this file and plan.md.

Later, not in the first version:

- **The prompt of a new session** (CodeAlta `e7fb1d857`). It would run on the draft's chosen machine and folder through a
  machine RPC with no session, keep the output in the draft, and let it go with the first prompt. It needs resolution
  without a session (the draft's profile and its creator) and its own audit.
- Commands longer than 10 minutes (a user limit apart from the model's).
- A wider output for the model, as CodeAlta's 30 K: `ShellOutput` would then keep the whole text in a blob.

## 6. Decisions (confirmed 2026-11-14)

1. **Persisted, not per window.** CodeAlta keeps nothing. BoSoFa records the command and its output in the transcript,
   for its other clients and for audit. The cost is event log volume, bounded by `OutputLimit` per command.
2. **Included by default** in the next message, as in CodeAlta. The user removes a chip to leave an output out.
3. **One path for every harness** (server-side backend action → `ToolRunRequest`). Harness sessions then resolve their
   variables again for each command, which records one `SecretsUsed` each, instead of reusing what their harness got.
4. **Allowed in a stopped session.**
