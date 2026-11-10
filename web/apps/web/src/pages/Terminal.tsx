import { api } from "@bosofa/api";
import { useNavigate, useParams } from "@solidjs/router";
import { createMemo, createSignal, Show } from "solid-js";
import { CloseIcon, TerminalIcon } from "../components/icons";
import { TerminalView } from "../components/Terminals";
import { useWorkspace } from "../workspace";

/** One terminal filling the page: a shell on a machine that keeps running when nobody watches. */
export default function TerminalPage() {
  const params = useParams<{ machine: string; terminal: string }>();
  const workspace = useWorkspace();
  const navigate = useNavigate();
  const [exited, setExited] = createSignal(false);
  const entry = createMemo(() =>
    workspace.terminals().find((candidate) => String(candidate.machineId) === params.machine && candidate.terminal.id === params.terminal),
  );

  async function close() {
    await api.DELETE("/api/v1/machines/{id}/terminals/{terminalId}", {
      params: { path: { id: Number(params.machine), terminalId: params.terminal } },
    });
    workspace.refreshTerminals();
    navigate("/");
  }

  return (
    <div class="-mx-4 -my-4 flex h-[calc(100dvh-var(--bar))] flex-col sm:-mx-6 sm:-my-6">
      <header class="flex h-12 shrink-0 items-center gap-2 border-b border-line px-4">
        <TerminalIcon class="size-4 text-muted" />
        <span class="min-w-0 truncate font-mono text-sm" title={entry()?.terminal.workingDirectory}>
          {entry()?.terminal.workingDirectory ?? "Terminal"}
        </span>
        <Show when={entry()}>
          {(found) => (
            <span class="shrink-0 text-sm text-muted">
              on{" "}
              <a class="hover:text-ink hover:underline" href={`/machines/${found().machineId}`}>
                {found().machineName}
              </a>{" "}
              · {found().terminal.shell}
            </span>
          )}
        </Show>
        <Show when={exited()}>
          <span class="shrink-0 rounded bg-line px-1.5 text-xs text-muted">exited</span>
        </Show>
        <button
          type="button"
          class="ml-auto flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-sm text-muted hover:bg-hover hover:text-ink"
          title="End the shell on the machine"
          onClick={close}
        >
          <CloseIcon class="size-4" />
          Close
        </button>
      </header>
      <Show when={`${params.machine}/${params.terminal}`} keyed>
        {(_terminal) => (
          <TerminalView
            class="min-h-0 flex-1"
            machineId={Number(params.machine)}
            terminalId={params.terminal}
            onExit={() => {
              setExited(true);
              workspace.refreshTerminals();
            }}
          />
        )}
      </Show>
    </div>
  );
}
