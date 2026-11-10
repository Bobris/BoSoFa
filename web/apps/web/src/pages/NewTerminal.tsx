import { api, type Machine } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createMemo, createSignal, For, Show } from "solid-js";
import { FolderIcon, MachineIcon, TerminalIcon } from "../components/icons";
import { useWorkspace } from "../workspace";

const lastMachineKey = "bosofa.lastTerminalMachine";

function remembered(): string {
  try {
    return localStorage.getItem(lastMachineKey) ?? "";
  } catch {
    return "";
  }
}

/** Opens a shell on one of my machines, in a folder or in the home folder. */
export default function NewTerminalPage() {
  const workspace = useWorkspace();
  const navigate = useNavigate();
  const machines = createMemo(async (): Promise<Machine[]> => {
    const user = workspace.me();
    if (!user) return [];
    return ((await api.GET("/api/v1/machines")).data ?? [])
      .filter((machine) => String(machine.ownerId) === String(user.id))
      .sort((a, b) => Number(b.online) - Number(a.online) || a.name.localeCompare(b.name));
  });
  const [chosen, setChosen] = createSignal(remembered());
  const [directory, setDirectory] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string>();
  const machine = () => machines().find((candidate) => String(candidate.id) === chosen() && candidate.online) ?? machines().find((candidate) => candidate.online);
  const recentFolders = createMemo(() => {
    const id = machine()?.id;
    const seen = new Set<string>();
    for (const session of workspace.sessions()) if (String(session.machineId) === String(id) && session.workingDirectory) seen.add(session.workingDirectory);
    return [...seen].slice(0, 8);
  });

  async function open(event: Event) {
    event.preventDefault();
    const target = machine();
    if (!target || busy()) return;
    setBusy(true);
    setError(undefined);
    const { data, error: failure } = await api.POST("/api/v1/machines/{id}/terminals", {
      params: { path: { id: Number(target.id) } },
      body: { workingDirectory: directory().trim() || null, cols: 120, rows: 32 },
    });
    setBusy(false);
    if (!data) return setError(typeof failure === "string" ? failure : "The machine could not open a terminal.");
    try {
      localStorage.setItem(lastMachineKey, String(target.id));
    } catch {
      /* private mode */
    }
    workspace.refreshTerminals();
    navigate(`/terminals/${target.id}/${data.terminal.id}`);
  }

  return (
    <div class="mx-auto flex min-h-[calc(100vh-7rem)] max-w-xl flex-col justify-center pb-[10vh]">
      <div class="mb-6 flex animate-rise flex-col items-center text-center">
        <span class="mb-3 flex size-11 items-center justify-center rounded-2xl bg-ink text-canvas">
          <TerminalIcon class="size-5" />
        </span>
        <h1 class="font-serif text-3xl tracking-tight">Open a terminal</h1>
        <p class="mt-1.5 text-sm text-muted">A shell on your machine. It keeps running when you leave, and others you let in can watch.</p>
      </div>
      <Show
        when={machines().some((candidate) => candidate.online)}
        fallback={<p class="text-center text-sm text-muted">None of your machines is online.</p>}
      >
        <form class="animate-rise space-y-4 rounded-2xl border border-line bg-panel p-4 shadow-sm" onSubmit={open}>
          <div>
            <p class="mb-1.5 text-xs font-medium text-muted">Machine</p>
            <div class="grid gap-1.5 sm:grid-cols-2">
              <For each={machines()} keyed={(candidate) => candidate.id}>
                {(candidate) => (
                  <button
                    type="button"
                    disabled={!candidate().online}
                    class={[
                      "flex items-center gap-2 rounded-xl border px-3 py-2 text-left text-sm transition-colors disabled:opacity-45",
                      String(machine()?.id) === String(candidate().id) ? "border-accent/60 bg-accent/5" : "border-line hover:bg-hover",
                    ]}
                    onClick={() => setChosen(String(candidate().id))}
                  >
                    <MachineIcon class="size-4 text-muted" />
                    <span class="min-w-0 flex-1 truncate">{candidate().name}</span>
                    <span class={["size-2 rounded-full", candidate().online ? "bg-green-500" : "bg-line"]} />
                  </button>
                )}
              </For>
            </div>
          </div>
          <label class="block">
            <span class="mb-1.5 block text-xs font-medium text-muted">Folder</span>
            <span class="flex items-center gap-2 rounded-xl border border-line px-3 py-2 focus-within:border-accent/50">
              <FolderIcon class="size-4 text-muted" />
              <input
                class="min-w-0 flex-1 bg-transparent font-mono text-sm outline-none placeholder:font-sans placeholder:text-muted"
                placeholder="The home folder"
                list="terminal-folders"
                value={directory()}
                onInput={(event) => setDirectory(event.currentTarget.value)}
              />
              <datalist id="terminal-folders">
                <For each={recentFolders()}>{(folder) => <option value={folder} />}</For>
              </datalist>
            </span>
          </label>
          <div class="flex items-center justify-end gap-3">
            <Show when={error()}>{(message) => <p class="mr-auto text-sm text-red-600">{message()}</p>}</Show>
            <button type="submit" class="rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50" disabled={busy() || !machine()}>
              {busy() ? "Opening…" : "Open terminal"}
            </button>
          </div>
        </form>
      </Show>
    </div>
  );
}
