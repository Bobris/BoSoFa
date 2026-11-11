import {
  api,
  type AgentModel,
  type HarnessAccount,
  type Machine,
  type Profile,
} from "@bosofa/api";
import { useNavigate, useSearchParams } from "@solidjs/router";
import {
  createEffect,
  createMemo,
  createSignal,
  For,
  Loading,
  onSettled,
  Show,
  untrack,
} from "solid-js";
import { branding } from "../branding";
import {
  ArrowUpIcon,
  BranchIcon,
  FolderIcon,
  MachineIcon,
  ShieldIcon,
  SparkIcon,
} from "../components/icons";
import { ModelPicker, type ModelOption } from "../components/ModelPicker";
import { Picker, type Choice } from "../components/Popover";
import { AttachmentStrip, createAttachments } from "../components/PromptImages";
import { useWorkspace } from "../workspace";

/** How a session starts: what a draft keeps besides its prompt, and what the next new session starts from. */
type Settings = {
  harness: string;
  machine: string;
  account: string;
  profile: string;
  directory: string;
  worktree: boolean;
  mode: string;
  model: string;
  effort: string;
  toolGroup: string;
  systemPrompt: string;
};

const defaults: Settings = {
  harness: "claude",
  machine: "",
  account: "",
  profile: "",
  directory: "",
  worktree: false,
  mode: "default",
  model: "",
  effort: "",
  toolGroup: "full",
  systemPrompt: "",
};

const settingsKey = "bosofa.newSession";

function parseSettings(json: string | null | undefined): Partial<Settings> {
  try {
    const value = JSON.parse(json ?? "null") as Partial<Settings> | null;
    return value && typeof value === "object" ? value : {};
  } catch {
    return {};
  }
}

function rememberedSettings(): Settings {
  try {
    const stored = parseSettings(localStorage.getItem(settingsKey));
    // Before drafts, only the folder was remembered.
    const directory =
      stored.directory ??
      localStorage.getItem("bosofa.lastWorkingDirectory") ??
      "";
    return { ...defaults, ...stored, directory };
  } catch {
    return { ...defaults };
  }
}

function remember(settings: Settings) {
  try {
    localStorage.setItem(settingsKey, JSON.stringify(settings));
  } catch {
    /* private mode */
  }
}

const permissionModes: Choice[] = [
  {
    value: "default",
    label: "Ask before tools",
    hint: "Approve each command and edit",
  },
  {
    value: "acceptEdits",
    label: "Accept file edits",
    hint: "Edits go ahead, commands ask",
  },
  {
    value: "plan",
    label: "Plan only",
    hint: "Reads and proposes, changes nothing",
  },
  {
    value: "bypassPermissions",
    label: "Bypass permissions",
    hint: "Everything runs without asking",
  },
];

const harnesses: Choice[] = [
  { value: "claude", label: "Claude Code" },
  { value: "codex", label: "Codex" },
  { value: "opencode", label: "OpenCode" },
  { value: "replay", label: "Replay", hint: "Plays a recorded transcript" },
];

const toolGroups: Choice[] = [
  { value: "full", label: "Read, change and run" },
  { value: "read", label: "Read only" },
  { value: "none", label: "No machine tools" },
];

function modelHint(model: AgentModel): string | undefined {
  const parts: string[] = [];
  if (model.contextWindow != null) {
    const size = Number(model.contextWindow);
    parts.push(
      size >= 1_000_000
        ? `${size / 1_000_000}M context`
        : `${Math.round(size / 1000)}K context`,
    );
  }
  if (model.inputPrice != null && model.outputPrice != null)
    parts.push(`$${model.inputPrice} / $${model.outputPrice} per M`);
  return parts.join(" · ") || undefined;
}

function greeting(): string {
  const hour = new Date().getHours();
  return hour < 5
    ? "Working late"
    : hour < 12
      ? "Good morning"
      : hour < 18
        ? "Good afternoon"
        : "Good evening";
}

/** The start screen: write what to do, choose where and how, and a draft keeps it until it starts. */
export default function NewSessionPage() {
  const workspace = useWorkspace();
  const [search, setSearch] = useSearchParams<{
    draft?: string;
    machine?: string;
  }>();
  // The draft an open editor created itself: the address follows it without opening the editor again.
  const [adopted, setAdopted] = createSignal<string>();
  let current = "";
  const key = createMemo(() => {
    const draft = search.draft;
    const fresh = workspace.fresh();
    if (draft && draft === untrack(adopted)) return current;
    current = draft ? `draft:${draft}` : `new:${fresh}`;
    return current;
  });
  createEffect(
    () => key(),
    () => setAdopted(undefined),
  );
  return (
    <Show when={key()} keyed>
      {(editorKey) => (
        <DraftLoader
          draftId={
            editorKey.startsWith("draft:")
              ? editorKey.slice("draft:".length)
              : undefined
          }
          machine={untrack(() => search.machine)}
          onAdopt={(id) => {
            setAdopted(id);
            setSearch({ draft: id, machine: undefined }, { replace: true });
          }}
        />
      )}
    </Show>
  );
}

function DraftLoader(props: {
  draftId?: string;
  machine?: string;
  onAdopt: (id: string) => void;
}) {
  const draft = createMemo(async () => {
    const id = props.draftId;
    if (!id) return null;
    const { data } = await api.GET("/api/v1/drafts/{id}", {
      params: { path: { id: Number(id) } },
    });
    return data ?? null;
  });
  return (
    <Show when={draft() !== undefined}>
      <Editor
        draftId={draft()?.id != null ? String(draft()!.id) : undefined}
        prompt={draft()?.prompt ?? ""}
        settings={{
          ...rememberedSettings(),
          ...(draft() ? parseSettings(draft()!.settings) : {}),
          ...(props.machine ? { machine: props.machine } : {}),
        }}
        onAdopt={props.onAdopt}
      />
    </Show>
  );
}

function Editor(props: {
  draftId?: string;
  prompt: string;
  settings: Settings;
  onAdopt: (id: string) => void;
}) {
  const workspace = useWorkspace();
  const navigate = useNavigate();
  const initial = untrack(() => props.settings);
  const [settings, setSettings] = createSignal<Settings>(initial);
  const [prompt, setPrompt] = createSignal(untrack(() => props.prompt));
  // A plain variable, not a signal: a start right after a save must see the id that save just got.
  let draftId = untrack(() => props.draftId);
  const [saved, setSaved] = createSignal<"" | "saving" | "saved">(
    untrack(() => (props.draftId ? "saved" : "")),
  );
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  const [showSystemPrompt, setShowSystemPrompt] = createSignal(
    initial.systemPrompt !== "",
  );
  const attachments = createAttachments();
  // What was chosen and written last, at once: a signal shows a write only after the next flush, and saving must not lag behind.
  let latest = initial;
  let latestPrompt = untrack(() => props.prompt);
  const set = <K extends keyof Settings>(name: K, value: Settings[K]) => {
    latest = { ...latest, [name]: value };
    setSettings(latest);
    changed();
  };

  const machines = createMemo(async (): Promise<Machine[]> => {
    const user = workspace.me();
    if (!user) return [];
    const all = (await api.GET("/api/v1/machines")).data ?? [];
    // Online machines first, so the default choice is one that can start right away.
    return all
      .filter((machine) => String(machine.ownerId) === String(user.id))
      .sort((a, b) => Number(b.online) - Number(a.online));
  });
  const accounts = createMemo(async (): Promise<HarnessAccount[]> =>
    workspace.me() ? ((await api.GET("/api/v1/accounts")).data ?? []) : [],
  );
  const profiles = createMemo(async (): Promise<Profile[]> =>
    workspace.me() ? ((await api.GET("/api/v1/profiles")).data ?? []) : [],
  );

  const agentAccounts = () =>
    accounts().filter((account) => account.servesAgent);
  const agentAccount = () =>
    settings().harness.startsWith("agent:")
      ? settings().harness.slice("agent:".length)
      : undefined;
  const isAgent = () => agentAccount() !== undefined;
  const agentModels = createMemo(async (): Promise<AgentModel[]> => {
    const id = agentAccount();
    if (!id) return [];
    return (
      (
        await api.GET("/api/v1/accounts/{id}/models", {
          params: { path: { id: Number(id) } },
        })
      ).data ?? []
    );
  });
  // The replay plays Claude Code, so it takes Claude accounts.
  const harnessAccounts = () =>
    accounts().filter(
      (account) =>
        account.harness ===
        (settings().harness === "replay" ? "claude" : settings().harness),
    );

  /** The machine to run on: the one chosen if it is still mine, else the first online one. The server agent may have none. */
  const machine = () => {
    const chosen = machines().find(
      (candidate) => String(candidate.id) === settings().machine,
    );
    if (chosen) return chosen;
    if (isAgent()) return undefined;
    return machines()[0];
  };
  const needsDirectory = () => !isAgent() || machine() !== undefined;
  const efforts = () =>
    agentModels().find((option) => option.id === settings().model)
      ?.reasoningEfforts ?? [];
  const agentOptions = (): ModelOption[] =>
    agentModels().map((option) => ({
      id: option.id,
      name: option.name,
      description: modelHint(option),
      efforts: option.reasoningEfforts,
    }));
  // A harness on a machine says what it has; asking starts it there, so only a change of machine or harness asks again.
  const listsModels = () =>
    !isAgent() && ["claude", "codex", "opencode"].includes(settings().harness);
  const modelSource = createMemo(
    () => {
      const chosen = machine();
      return listsModels() && chosen
        ? {
            id: Number(chosen.id),
            online: chosen.online,
            harness: settings().harness,
          }
        : undefined;
    },
    {
      equals: (a, b) =>
        a?.id === b?.id && a?.online === b?.online && a?.harness === b?.harness,
    },
  );
  const machineModels = createMemo(
    async (): Promise<{ models: ModelOption[]; status?: string }> => {
      const source = modelSource();
      if (!source) return { models: [] };
      if (!source.online)
        return {
          models: [],
          status:
            "The machine is offline, so its models are not known yet. Name one, or keep the default.",
        };
      const { data, error } = await api.GET("/api/v1/machines/{id}/models", {
        params: { path: { id: source.id }, query: { harness: source.harness } },
      });
      if (!data)
        return {
          models: [],
          status:
            typeof error === "string"
              ? error
              : "The models could not be listed.",
        };
      return {
        models: data.models.map((model) => ({
          id: model.id,
          name: model.name,
          description: model.description,
          efforts: model.reasoningEfforts,
          defaultEffort: model.defaultEffort,
        })),
      };
    },
  );
  const takesImages = () =>
    !isAgent() ||
    agentModels().find((option) => option.id === settings().model)
      ?.acceptsImages !== false;
  // Folders this machine's earlier sessions worked in, most recent first.
  const recentFolders = createMemo(() => {
    const id = machine()?.id;
    if (id == null) return [];
    const seen = new Set<string>();
    for (const session of workspace.sessions())
      if (String(session.machineId) === String(id) && session.workingDirectory)
        seen.add(session.workingDirectory);
    return [...seen].slice(0, 12);
  });

  const problem = (): string | undefined => {
    if (!prompt().trim()) return "Write what the session should do.";
    if (attachments.uploading()) return "Images are still uploading.";
    if (isAgent() && !settings().model.trim()) return "Choose a model.";
    if (!isAgent() && !machine()) return "Choose a machine.";
    if (needsDirectory() && !settings().directory.trim())
      return "Choose the folder to work in.";
    return undefined;
  };

  // Saving: a moment after the last change, one save at a time, so a new draft is created only once.
  let timer: ReturnType<typeof setTimeout> | undefined;
  let queue: Promise<void> = Promise.resolve();
  let dirty = false;
  let started = false;
  function changed() {
    remember(latest);
    dirty = true;
    clearTimeout(timer);
    timer = setTimeout(flushSave, 700);
  }
  function flushSave(): Promise<void> {
    clearTimeout(timer);
    queue = queue.then(save, save);
    return queue;
  }
  async function save() {
    if (!dirty || started) return;
    dirty = false;
    const text = latestPrompt;
    const id = draftId;
    if (!text.trim()) {
      // Nothing written is nothing to keep.
      if (id) {
        await api.DELETE("/api/v1/drafts/{id}", {
          params: { path: { id: Number(id) } },
        });
        draftId = undefined;
        setSaved("");
        workspace.refreshDrafts();
      }
      return;
    }
    setSaved("saving");
    const body = { prompt: text, settings: JSON.stringify(latest) };
    if (id) {
      const { response } = await api.PUT("/api/v1/drafts/{id}", {
        params: { path: { id: Number(id) } },
        body,
      });
      if (response.status === 404) {
        // Discarded elsewhere: keep what is written as a new one.
        draftId = undefined;
        dirty = true;
        return save();
      }
    } else {
      const { data } = await api.POST("/api/v1/drafts", { body });
      if (data) {
        draftId = String(data.id);
        props.onAdopt(String(data.id));
      }
    }
    setSaved("saved");
    workspace.refreshDrafts();
  }
  onSettled(() => () => {
    if (dirty && !started) void flushSave(); // leaving with changes not saved yet
  });

  async function start(event?: Event) {
    event?.preventDefault();
    if (problem() || busy()) return;
    setBusy(true);
    setError(undefined);
    await flushSave(); // the draft that becomes the session is the one saved last
    const agent = agentAccount();
    const current = latest;
    const {
      data,
      response,
      error: failure,
    } = await api.POST("/api/v1/sessions", {
      body: {
        machineId: Number(machine()?.id ?? 0),
        harness: agent ? "agent" : current.harness,
        workingDirectory: needsDirectory() ? current.directory.trim() : "",
        prompt: latestPrompt,
        permissionMode:
          current.profile && current.mode === "default" ? null : current.mode, // the profile's own default applies
        model: current.model.trim() || null,
        accountId: agent
          ? Number(agent)
          : harnessAccounts().some((a) => String(a.id) === current.account)
            ? Number(current.account)
            : null,
        useWorktree: !agent && current.worktree,
        profileName: current.profile || null,
        images: takesImages() ? attachments.hashes() : [],
        systemPrompt:
          agent && current.systemPrompt.trim()
            ? current.systemPrompt.trim()
            : null,
        reasoningEffort: agent
          ? efforts().includes(current.effort)
            ? current.effort
            : null
          : listsModels() && current.effort
            ? current.effort
            : null,
        toolGroup: agent && machine() ? current.toolGroup : null,
        draftId: draftId ? Number(draftId) : null,
      },
    });
    setBusy(false);
    if (!data) {
      setError(
        typeof failure === "string"
          ? failure
          : `Starting the session failed (${response.status}).`,
      );
      return;
    }
    started = true;
    remember(current);
    workspace.refreshSessions();
    workspace.refreshDrafts();
    navigate(`/sessions/${data.id}`);
  }

  let textarea: HTMLTextAreaElement | undefined;
  const grow = () => {
    if (!textarea) return;
    textarea.style.height = "auto";
    textarea.style.height = `${Math.min(textarea.scrollHeight, window.innerHeight * 0.45)}px`;
  };
  onSettled(() => {
    grow();
    textarea?.focus();
    textarea?.setSelectionRange(textarea.value.length, textarea.value.length);
  });

  const harnessChoices = (): Choice[] => [
    ...harnesses,
    ...agentAccounts().map((account) => ({
      value: `agent:${account.id}`,
      label: account.label,
      hint: "Server agent · needs no machine",
    })),
  ];
  const harnessLabel = () =>
    harnessChoices().find((choice) => choice.value === settings().harness)
      ?.label ?? "Choose";
  const machineChoices = (): Choice[] => [
    ...(isAgent()
      ? [
          {
            value: "",
            label: "No machine",
            hint: "Tools come only from MCP servers",
          },
        ]
      : []),
    ...machines().map((option) => ({
      value: String(option.id),
      label: option.name,
      hint: option.online ? "Online" : "Offline: starts when it connects",
    })),
  ];
  const pill =
    "flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-sm text-muted transition-colors hover:bg-hover hover:text-ink";
  const firstName = () => workspace.me()?.name.split(/\s+/)[0] ?? "";

  return (
    <div class="mx-auto flex min-h-[calc(100vh-7rem)] max-w-3xl flex-col justify-center pb-[10vh]">
      <h1 class="mb-7 animate-rise text-center font-serif text-3xl tracking-tight text-ink sm:text-[2.5rem]">
        {greeting()}
        <Show when={firstName()}>, {firstName()}</Show>
      </h1>
      <Show
        when={machines().length > 0 || agentAccounts().length > 0}
        fallback={
          <div class="mx-auto max-w-lg rounded-2xl border border-line bg-panel p-6 text-sm shadow-sm">
            <h2 class="text-base font-medium">Connect a machine to start</h2>
            <p class="mt-2 text-muted">
              Sessions run on your computers. Install the daemon on one and
              approve it, or add a provider account to run an agent on the
              server.
            </p>
            <pre class="mt-4 overflow-x-auto rounded-lg bg-canvas px-3 py-2 font-mono text-xs">
              {branding().daemon} enroll --server {location.origin}
            </pre>
            <div class="mt-4 flex gap-3">
              <a
                href="/install"
                class="rounded-lg bg-accent px-3 py-1.5 font-medium text-white"
              >
                Install the daemon
              </a>
              <a
                href="/accounts"
                class="rounded-lg border border-line px-3 py-1.5 hover:bg-hover"
              >
                Add an account
              </a>
            </div>
          </div>
        }
      >
        <form
          class="animate-rise rounded-2xl border border-line bg-panel shadow-[0_1px_2px_rgb(0_0_0/0.04),0_4px_16px_rgb(0_0_0/0.04)] transition-shadow focus-within:border-accent/40 focus-within:shadow-[0_2px_4px_rgb(0_0_0/0.05),0_8px_28px_rgb(0_0_0/0.07)]"
          onSubmit={start}
        >
          <div class="px-4 pt-3">
            <AttachmentStrip attachments={attachments} />
          </div>
          <textarea
            ref={(element) => (textarea = element)}
            class="block max-h-[45vh] min-h-24 w-full resize-none bg-transparent px-4 pb-2 text-[15px] leading-relaxed outline-none placeholder:text-muted"
            rows={3}
            placeholder="What should be done? Paste or drop images too."
            value={prompt()}
            onInput={(event) => {
              latestPrompt = event.currentTarget.value;
              setPrompt(latestPrompt);
              grow();
              changed();
            }}
            onKeyDown={(event) => {
              if (
                event.key === "Enter" &&
                !event.shiftKey &&
                !event.isComposing
              ) {
                event.preventDefault();
                void start();
              }
            }}
            onPaste={attachments.onPaste}
            onDragOver={attachments.onDragOver}
            onDrop={attachments.onDrop}
          />
          <div class="flex flex-wrap items-center gap-0.5 px-2 pb-2">
            <Picker
              up={false}
              icon={<SparkIcon class="size-4" />}
              heading="Harness"
              label={harnessLabel()}
              value={settings().harness}
              choices={harnessChoices()}
              onChange={(value) => {
                set("harness", value);
                set("model", "");
                set("effort", "");
              }}
            />
            <Picker
              icon={<MachineIcon class="size-4" />}
              heading="Machine"
              label={machine()?.name ?? "No machine"}
              value={machine() ? String(machine()!.id) : ""}
              choices={machineChoices()}
              onChange={(value) => set("machine", value)}
            />
            <Show when={needsDirectory()}>
              <label
                class={[pill, "min-w-0 max-w-72 cursor-text font-mono text-xs"]}
                title="The folder on the machine to work in"
              >
                <FolderIcon class="size-4 shrink-0" />
                <input
                  class="w-48 min-w-0 bg-transparent text-ink outline-none placeholder:font-sans placeholder:text-sm placeholder:text-muted"
                  placeholder="Folder on the machine"
                  list="recent-folders"
                  value={settings().directory}
                  onInput={(event) =>
                    set("directory", event.currentTarget.value)
                  }
                />
                <datalist id="recent-folders">
                  <For each={recentFolders()}>
                    {(folder) => <option value={folder} />}
                  </For>
                </datalist>
              </label>
            </Show>
            <span class="flex-1" />
            {/* The model and the start button stay together when the row wraps. */}
            <div class="ml-auto flex items-center">
              <Show
                when={isAgent()}
                fallback={
                  <Show
                    when={listsModels()}
                    fallback={
                      <input
                        class="h-8 w-32 rounded-lg bg-transparent px-2.5 text-right text-sm text-muted outline-none placeholder:text-muted hover:bg-hover focus:bg-hover focus:text-ink"
                        placeholder="Default model"
                        title="The model, if not the harness's default"
                        value={settings().model}
                        onInput={(event) =>
                          set("model", event.currentTarget.value)
                        }
                      />
                    }
                  >
                    <Loading
                      fallback={
                        <span class={[pill, "animate-pulse"]}>Models…</span>
                      }
                    >
                      <ModelPicker
                        models={machineModels().models}
                        status={machineModels().status}
                        offersDefault
                        model={settings().model}
                        effort={settings().effort}
                        onModel={(value) => set("model", value)}
                        onEffort={(value) => set("effort", value)}
                      />
                    </Loading>
                  </Show>
                }
              >
                <ModelPicker
                  models={agentOptions()}
                  model={settings().model}
                  effort={settings().effort}
                  onModel={(value) => set("model", value)}
                  onEffort={(value) => set("effort", value)}
                />
              </Show>
              <button
                type="submit"
                class="ml-1 flex size-8 items-center justify-center rounded-full bg-accent text-white shadow-sm transition-opacity disabled:opacity-30"
                disabled={problem() !== undefined || busy()}
                title={problem() ?? "Start (↵)"}
              >
                <ArrowUpIcon class="size-4" />
              </button>
            </div>
          </div>
        </form>

        <div class="mt-2 flex flex-wrap items-center gap-0.5 px-1">
          <Picker
            icon={<ShieldIcon class="size-4" />}
            heading="Permissions"
            label={
              permissionModes.find((mode) => mode.value === settings().mode)
                ?.label ?? "Permissions"
            }
            value={settings().mode}
            choices={permissionModes}
            onChange={(value) => set("mode", value)}
          />
          <Show when={!isAgent()}>
            <button
              type="button"
              class={[
                pill,
                {
                  "bg-accent/10 text-accent hover:bg-accent/15 hover:text-accent":
                    settings().worktree,
                },
              ]}
              title="Work in a new git worktree on its own branch, so the folder itself stays as it is"
              onClick={() => set("worktree", !settings().worktree)}
            >
              <BranchIcon class="size-4" />
              {settings().worktree ? "New worktree" : "In the folder"}
            </button>
          </Show>
          <Show when={profiles().length > 0}>
            <Picker
              heading="Profile: MCP servers and skills"
              label={settings().profile || "No profile"}
              value={settings().profile}
              choices={[
                { value: "", label: "No profile" },
                ...profiles().map((option) => ({
                  value: option.name,
                  label: option.name,
                })),
              ]}
              onChange={(value) => set("profile", value)}
            />
          </Show>
          <Show when={!isAgent() && harnessAccounts().length > 0}>
            <Picker
              heading="Account"
              label={
                harnessAccounts().find(
                  (a) => String(a.id) === settings().account,
                )?.label ?? "Machine's own sign-in"
              }
              value={
                harnessAccounts().some(
                  (a) => String(a.id) === settings().account,
                )
                  ? settings().account
                  : ""
              }
              choices={[
                { value: "", label: "Machine's own sign-in" },
                ...harnessAccounts().map((a) => ({
                  value: String(a.id),
                  label: a.label,
                })),
              ]}
              onChange={(value) => set("account", value)}
            />
          </Show>
          <Show when={isAgent() && machine()}>
            <Picker
              heading="Machine tools"
              label={
                toolGroups.find((group) => group.value === settings().toolGroup)
                  ?.label ?? "Tools"
              }
              value={settings().toolGroup}
              choices={toolGroups}
              onChange={(value) => set("toolGroup", value)}
            />
          </Show>
          <Show when={isAgent()}>
            <button
              type="button"
              class={[pill, { "bg-hover text-ink": showSystemPrompt() }]}
              onClick={() => setShowSystemPrompt(!showSystemPrompt())}
            >
              System prompt
            </button>
          </Show>
          <span class="ml-auto hidden px-2 text-xs text-muted sm:inline">
            {saved() === "saving"
              ? "Saving draft…"
              : saved() === "saved"
                ? "Draft saved"
                : "↵ to start · ⇧↵ new line"}
          </span>
        </div>

        <Show when={isAgent() && showSystemPrompt()}>
          <textarea
            class="mt-2 min-h-20 w-full resize-y rounded-xl border border-line bg-panel px-3 py-2 text-sm outline-none focus:border-accent/40"
            placeholder="Added to the built-in system prompt"
            value={settings().systemPrompt}
            onInput={(event) => set("systemPrompt", event.currentTarget.value)}
          />
        </Show>
        <Show when={!takesImages() && attachments.items().length > 0}>
          <p class="mt-2 px-2 text-xs text-muted">
            This model cannot read images, so they will not be sent.
          </p>
        </Show>
        <Show when={machine() && !machine()!.online && !isAgent()}>
          <p class="mt-2 px-2 text-xs text-amber-600">
            {machine()!.name} is offline; the session starts when it connects.
          </p>
        </Show>
        <Show when={error()}>
          {(message) => (
            <p class="mt-2 px-2 text-sm text-red-600">{message()}</p>
          )}
        </Show>
      </Show>
    </div>
  );
}
