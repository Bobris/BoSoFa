import { AttachmentStrip, createAttachments } from "../components/PromptImages";
import { api, type HarnessAccount, type Machine, type Profile } from "@bosofa/api";
import { useNavigate, useSearchParams } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";
import { branding } from "../branding";

const permissionModes = [
  { value: "default", label: "Ask before tools (default)" },
  { value: "acceptEdits", label: "Accept file edits" },
  { value: "plan", label: "Plan only" },
  { value: "bypassPermissions", label: "Bypass permissions" },
];

const lastDirectoryKey = "bosofa.lastWorkingDirectory";

function remembered(key: string): string {
  try {
    return localStorage.getItem(key) ?? "";
  } catch {
    return "";
  }
}

/** Start a harness on one of my machines. */
export default function NewSession() {
  const navigate = useNavigate();
  const [search] = useSearchParams<{ machine: string }>();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent("/sessions/new")}`, { replace: true });
    },
  );
  const machines = createMemo(async (): Promise<Machine[]> => {
    const user = me();
    if (!user) return [];
    const all = (await api.GET("/api/v1/machines")).data ?? [];
    // Online machines first, so the default choice is one that can start right away.
    return all
      .filter((machine) => String(machine.ownerId) === String(user.id))
      .sort((a, b) => Number(b.online) - Number(a.online));
  });

  const accounts = createMemo(async (): Promise<HarnessAccount[]> =>
    me() ? ((await api.GET("/api/v1/accounts")).data ?? []) : [],
  );
  const profiles = createMemo(async (): Promise<Profile[]> => (me() ? ((await api.GET("/api/v1/profiles")).data ?? []) : []));
  const [profile, setProfile] = createSignal("");
  const [machine, setMachine] = createSignal<string>();
  const [account, setAccount] = createSignal("");
  // The replay plays Claude Code, so it takes Claude accounts.
  const usable = () => accounts().filter((a) => a.harness === (harness() === "replay" ? "claude" : harness()));
  const [harness, setHarness] = createSignal("claude");
  const [directory, setDirectory] = createSignal(remembered(lastDirectoryKey));
  const [mode, setMode] = createSignal("default");
  const [model, setModel] = createSignal("");
  const [prompt, setPrompt] = createSignal("");
  const [worktree, setWorktree] = createSignal(false);
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  const attachments = createAttachments();
  const selected = () => machine() ?? search.machine ?? String(machines()[0]?.id ?? "");

  async function start(event: SubmitEvent) {
    event.preventDefault();
    setBusy(true);
    const { data, response, error: problem } = await api.POST("/api/v1/sessions", {
      body: {
        machineId: Number(selected()),
        harness: harness(),
        workingDirectory: directory().trim(),
        prompt: prompt(),
        permissionMode: profile() && mode() === "default" ? null : mode(), // the profile's own default applies
        model: model().trim() || null,
        accountId: usable().some((a) => String(a.id) === account()) ? Number(account()) : null,
        useWorktree: worktree(),
        profileName: profile() || null,
        images: attachments.hashes(),
      },
    });
    setBusy(false);
    if (!data) {
      setError(typeof problem === "string" ? problem : `Starting the session failed (${response.status}).`);
      return;
    }
    try {
      localStorage.setItem(lastDirectoryKey, directory().trim());
    } catch {
      /* private mode */
    }
    navigate(paths.sessions(Number(data.id))());
  }

  const field = "w-full rounded-md border border-line bg-panel px-3 py-2 text-sm";

  return (
    <Show when={me()}>
      <form class="mx-auto max-w-2xl space-y-4" onSubmit={start}>
        <h1 class="text-xl font-semibold">New session</h1>
        <Show
          when={machines().length > 0}
          fallback={
            <p class="text-sm text-muted">
              You have no machines yet. Run <code class="font-mono">{branding().daemon} enroll --server {location.origin}</code> on
              one and approve it.
            </p>
          }
        >
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block space-y-1">
              <span class="text-sm text-muted">Machine</span>
              <select class={field} value={selected()} onChange={(event) => setMachine(event.currentTarget.value)}>
                <For each={machines()}>
                  {(option) => (
                    <option value={String(option.id)}>
                      {option.name}
                      {option.online ? "" : " (offline)"}
                    </option>
                  )}
                </For>
              </select>
            </label>
            <label class="block space-y-1">
              <span class="text-sm text-muted">Harness</span>
              <select class={field} value={harness()} onChange={(event) => setHarness(event.currentTarget.value)}>
                <option value="claude">Claude Code</option>
                <option value="codex">Codex</option>
                <option value="opencode">OpenCode</option>
                <option value="replay">Replay (recorded transcript)</option>
              </select>
            </label>
          </div>
          <label class="block space-y-1">
            <span class="text-sm text-muted">Profile (MCP servers and skills)</span>
            <select class={field} value={profile()} onChange={(event) => setProfile(event.currentTarget.value)}>
              <option value="">None</option>
              <For each={profiles()} keyed={(option) => option.name}>
                {(option) => <option value={option().name}>{option().name}</option>}
              </For>
            </select>
          </label>
          <label class="block space-y-1">
            <span class="text-sm text-muted">Account</span>
            <select class={field} value={account()} onChange={(event) => setAccount(event.currentTarget.value)}>
              <option value="">The machine's own sign-in</option>
              <For each={usable()}>{(option) => <option value={String(option.id)}>{option.label}</option>}</For>
            </select>
          </label>
          <label class="block space-y-1">
            <span class="text-sm text-muted">Working directory on the machine</span>
            <input
              class={[field, "font-mono"]}
              required
              placeholder="/Users/me/src/project"
              value={directory()}
              onInput={(event) => setDirectory(event.currentTarget.value)}
            />
          </label>
          <label class="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={worktree()} onChange={(event) => setWorktree(event.currentTarget.checked)} />
            Run in a new git worktree, on its own bosofa/… branch
          </label>
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block space-y-1">
              <span class="text-sm text-muted">Permissions</span>
              <select class={field} value={mode()} onChange={(event) => setMode(event.currentTarget.value)}>
                <For each={permissionModes}>{(option) => <option value={option.value}>{option.label}</option>}</For>
              </select>
            </label>
            <label class="block space-y-1">
              <span class="text-sm text-muted">Model (optional)</span>
              <input
                class={field}
                placeholder="sonnet, opus, haiku…"
                value={model()}
                onInput={(event) => setModel(event.currentTarget.value)}
              />
            </label>
          </div>
          <label class="block space-y-1">
            <span class="text-sm text-muted">Prompt</span>
            <textarea
              class={[field, "min-h-32 resize-y"]}
              required
              value={prompt()}
              onInput={(event) => setPrompt(event.currentTarget.value)}
              onPaste={attachments.onPaste}
              onDragOver={attachments.onDragOver}
              onDrop={attachments.onDrop}
            />
          </label>
          <AttachmentStrip attachments={attachments} />
          <div class="flex items-center gap-3">
            <button
              type="submit"
              class="rounded-md bg-accent px-4 py-2 font-medium text-white disabled:opacity-50"
              disabled={busy() || !selected() || !directory().trim() || !prompt().trim() || attachments.uploading()}
            >
              Start
            </button>
            <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
          </div>
        </Show>
      </form>
    </Show>
  );
}
