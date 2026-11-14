import { api, type SessionItem } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createMemo, createSignal, For, onSettled, Show } from "solid-js";
import { CloseIcon, StopIcon, TerminalIcon } from "./icons";

/** Shell commands run from the prompt with `!` (docs/shell-commands.md). */

export const SHELL_COMMAND_MAX_LENGTH = 16_384;
export const SHELL_COMMANDS_RUNNING = 4;
export const SHELL_OUTPUTS_PER_MESSAGE = 8;

export type ShellOutputData = {
  command: string;
  folder: string;
  state: "done" | "stopped" | "timedOut" | "failed";
  exitCode?: number | null;
  milliseconds: number;
};

/** The ref a command's streamed output and its output item carry. */
export const shellRef = (commandSeq: number | string) => `shell-${commandSeq}`;

/** A draft with this kind of ref is the output of a command the person runs. */
export const isShellDraft = (ref: string) => ref.startsWith("shell-");

/** The command's seq, from its output's ref. */
export function commandSeqOf(ref: string | null | undefined): number | undefined {
  const match = /^shell-(\d+)$/.exec(ref ?? "");
  return match ? Number(match[1]) : undefined;
}

export function shellData(item: SessionItem | undefined): ShellOutputData | undefined {
  if (!item?.data) return undefined;
  try {
    return JSON.parse(item.data) as ShellOutputData;
  } catch {
    return undefined;
  }
}

/** The seqs of the outputs a message carried, from its `shell:` ref. */
export function carriedOutputs(item: SessionItem): number[] {
  return item.ref?.startsWith("shell:") ? item.ref.slice(6).split(",").map(Number).filter(Number.isFinite) : [];
}

/** The outputs that ended since the person's last message: what the next one offers to carry. */
export function pendingOutputs(items: readonly SessionItem[]): SessionItem[] {
  const pending: SessionItem[] = [];
  for (let i = items.length - 1; i >= 0; i--) {
    const item = items[i];
    if (item.kind === "User") break;
    // A command that never ran has nothing to tell the agent.
    if (item.kind === "ShellOutput" && shellData(item)?.state !== "failed") pending.unshift(item);
  }
  return pending;
}

/** The commands run in the session, the latest last, each once in a row: what shell mode recalls. */
export function commandHistory(items: readonly SessionItem[]): string[] {
  return items
    .filter((item) => item.kind === "ShellCommand")
    .map((item) => item.text)
    .filter((command, index, all) => command !== all[index + 1]);
}

function elapsed(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.floor(ms / 60_000)} min ${Math.round((ms % 60_000) / 1000)} s`;
}

/** How a command ended, in a few words. */
export function shellEnding(data: ShellOutputData): string {
  switch (data.state) {
    case "done":
      return `Exit code ${data.exitCode ?? "?"} · ${elapsed(data.milliseconds)}`;
    case "stopped":
      return `Stopped · ${elapsed(data.milliseconds)}`;
    case "timedOut":
      return `Ran out of time · ${elapsed(data.milliseconds)}`;
    default:
      return "Did not run";
  }
}

/** The output of a terminal as plain text: no escape sequences, and a line rewritten in place (`\r`) as it ended. */
export function plainOutput(output: string): string {
  return (
    output
      // eslint-disable-next-line no-control-regex
      .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-Z\\-_]/gu, "")
      .replace(/\r\n/gu, "\n")
      .split("\n")
      .map((line) => line.slice(line.lastIndexOf("\r") + 1))
      .join("\n")
  );
}

/** Opens a terminal on the machine in the command's folder and types the command there, without running it. */
async function openInTerminal(machineId: number, folder: string, command: string, navigate: (to: string, options?: { state?: unknown }) => void) {
  const { data } = await api.POST("/api/v1/machines/{id}/terminals", {
    params: { path: { id: machineId } },
    body: { workingDirectory: folder || null, cols: 120, rows: 32 },
  });
  if (data) navigate(`/terminals/${machineId}/${data.terminal.id}`, { state: { type: command } });
  return !!data;
}

const button = "flex h-7 items-center gap-1 rounded-md px-2 text-xs text-muted hover:bg-hover hover:text-ink disabled:opacity-40";

/** A command the person ran, set apart from the conversation: its output while it runs, then how it ended. */
export function ShellCommandCard(props: {
  item: SessionItem;
  output?: SessionItem;
  draft?: string;
  sessionId: number;
  machineId: number;
  canControl: boolean;
  onRunAgain: (command: string) => void;
}) {
  const navigate = useNavigate();
  const data = createMemo(() => shellData(props.output));
  const text = createMemo(() => plainOutput(props.output ? props.output.text : (props.draft ?? "")));
  const lines = createMemo(() => text().split("\n").length);
  const [open, setOpen] = createSignal<boolean>();
  const [notice, setNotice] = createSignal<string>();
  const [copied, setCopied] = createSignal(false);
  const [now, setNow] = createSignal(Date.now());
  onSettled(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  });
  // A long output is folded once it ended; one that runs shows its end as it comes.
  const unfolded = () => open() ?? (!props.output || lines() <= 12);
  const ok = () => data()?.state === "done" && data()?.exitCode === 0;

  async function stop() {
    await api.POST("/api/v1/sessions/{id}/shell-commands/{seq}/stop", {
      params: { path: { id: props.sessionId, seq: Number(props.item.seq) } },
    });
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(text());
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      setNotice("The output could not be copied.");
    }
  }

  return (
    <div class="rounded-lg border border-amber-500/40 bg-amber-500/[0.04] text-sm" data-shell-command={props.item.seq}>
      <div class="flex items-start gap-2 px-3 pt-2">
        <span class="mt-0.5 select-none font-mono font-semibold text-amber-600 dark:text-amber-400">!</span>
        <code class="min-w-0 flex-1 whitespace-pre-wrap break-all font-mono text-[13px]">{props.item.text}</code>
        <span class="shrink-0 select-none pt-0.5 text-xs text-muted">
          <Show
            when={data()}
            fallback={<span class="animate-pulse">Running · {Math.max(0, Math.round((now() - Date.parse(props.item.at)) / 1000))} s</span>}
          >
            {(ended) => <span class={{ "text-red-600": !ok() }}>{shellEnding(ended())}</span>}
          </Show>
        </span>
      </div>
      <Show when={data()?.state !== "failed"} fallback={<p class="px-3 py-2 text-red-600 whitespace-pre-wrap">{props.output?.text}</p>}>
        <Show when={text().length > 0} fallback={<p class="px-3 py-1.5 text-xs text-muted">{props.output ? "No output." : ""}</p>}>
          <Show
            when={unfolded()}
            fallback={
              <button class="px-3 py-1.5 text-xs text-muted hover:text-ink" onClick={() => setOpen(true)}>
                Show {lines()} lines of output
              </button>
            }
          >
            <pre class="mx-3 my-2 max-h-96 overflow-auto rounded-md bg-[#17181f] px-3 py-2 font-mono text-xs leading-relaxed text-white/85 whitespace-pre-wrap">
              {text()}
            </pre>
          </Show>
        </Show>
      </Show>
      <div class="flex flex-wrap items-center gap-1 border-t border-amber-500/20 px-2 py-1">
        <span class="select-none px-1 text-xs text-muted">Your command · not part of the conversation unless your next message takes it</span>
        <span class="flex-1" />
        <Show when={!props.output && props.canControl}>
          <button class={button} onClick={() => void stop()} title="Stop the command and what it started">
            <StopIcon class="size-3.5" /> Stop
          </button>
        </Show>
        <Show when={props.output}>
          <Show when={text().length > 0}>
            <button class={button} onClick={() => void copy()}>
              {copied() ? "Copied" : "Copy output"}
            </button>
          </Show>
          <Show when={props.canControl}>
            <button class={button} onClick={() => props.onRunAgain(props.item.text)}>
              Run again
            </button>
          </Show>
        </Show>
        <Show when={props.canControl && props.machineId}>
          <button
            class={button}
            title="Open a terminal on the machine in this folder, with the command typed in"
            onClick={async () => {
              if (!(await openInTerminal(props.machineId, data()?.folder ?? "", props.item.text, navigate)))
                setNotice("The machine could not open a terminal.");
            }}
          >
            <TerminalIcon class="size-3.5" /> Open in a terminal
          </button>
        </Show>
      </div>
      <Show when={notice()}>{(message) => <p class="px-3 pb-2 text-xs text-red-600">{message()}</p>}</Show>
    </div>
  );
}

/** The outputs the next message carries, each one removable. */
export function ShellOutputChips(props: { outputs: readonly SessionItem[]; excluded: ReadonlySet<string>; onToggle: (seq: string) => void }) {
  return (
    <div class="flex flex-wrap gap-1.5 px-3 pt-2 empty:hidden">
      <For each={props.outputs}>
        {(output) => (
          <Show when={!props.excluded.has(String(output.seq))}>
            <span
              class="flex max-w-full items-center gap-1 rounded-md border border-amber-500/40 bg-amber-500/10 py-0.5 pl-2 pr-1 font-mono text-xs"
              title="This command's output goes with your message"
            >
              <span class="truncate">$ {shellData(output)?.command ?? ""}</span>
              <button
                type="button"
                class="rounded p-0.5 text-muted hover:bg-hover hover:text-ink"
                aria-label="Leave this output out of the message"
                onClick={() => props.onToggle(String(output.seq))}
              >
                <CloseIcon class="size-3" />
              </button>
            </span>
          </Show>
        )}
      </For>
    </div>
  );
}

/** What a sent message carried: the commands whose outputs went with it. */
export function CarriedOutputs(props: { commands: readonly string[] }) {
  return (
    <div class="mb-1 flex flex-wrap gap-1">
      <For each={props.commands}>
        {(command) => <span class="rounded bg-amber-500/15 px-1.5 py-0.5 font-mono text-xs">$ {command}</span>}
      </For>
    </div>
  );
}
