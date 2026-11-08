import { api, getPlatform, uploadBlob, type FileEntry } from "@bosofa/api";
import { useNavigate, useParams } from "@solidjs/router";
import Limit from "../components/Limit";
import Terminals from "../components/Terminals";
import { createEffect, createMemo, createSignal, For, Loading, refresh, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";

type Listing = { path: string | null; parent: string | null; entries: FileEntry[]; truncated: boolean; error?: string };

const asText = (error: unknown) => (typeof error === "string" ? error : "The machine could not do that.");

function directory(path: string): FileEntry {
  return { name: path, path, isDirectory: true, isLink: false, size: 0, modifiedAt: "" };
}

function formatSize(size: number) {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

function windowName(minutes: number) {
  if (minutes === 10080) return "Week";
  return minutes % 60 === 0 ? `${minutes / 60} hours` : `${minutes} minutes`;
}

/** This machine's own Codex sign-in, read live; nothing shows without one or while the machine is away. */
function CodexLimits(props: { machineId: number }) {
  const usage = createMemo(async () => {
    const { data } = await api.GET("/api/v1/machines/{id}/codex-usage", { params: { path: { id: props.machineId } } });
    return data?.signedIn ? data : undefined;
  });
  return (
    <Show when={usage()}>
      {(codex) => (
        <section class="rounded-lg border border-line bg-panel p-4 text-sm">
          <div class="flex items-center justify-between gap-3">
            <h2 class="font-medium">
              Codex <Show when={codex().plan}>{(plan) => <span class="font-normal text-muted">{plan()}</span>}</Show>
            </h2>
            <span class="flex items-center gap-3 text-xs">
              <Show when={codex().credits}>
                {(credits) => <span class="text-muted">{Number(credits()).toFixed(2)} credits</span>}
              </Show>
              <button class="text-muted hover:underline" onClick={() => refresh(usage)}>
                Refresh
              </button>
            </span>
          </div>
          <div class="mt-2 flex gap-4 text-xs">
            <For each={codex().windows}>
              {(window) => <Limit name={windowName(Number(window.minutes))} window={window.usage} />}
            </For>
          </div>
        </section>
      )}
    </Show>
  );
}

/** Browses the folders a machine shares, with a text preview, download and upload. */
function Files(props: { machineId: number; onTerminalHere: (directory: string) => void }) {
  const id = () => ({ path: { id: props.machineId } });
  const [path, setPath] = createSignal<string | null>(null);
  const [opened, setOpened] = createSignal<FileEntry | null>(null);
  const [notice, setNotice] = createSignal<string>();

  const listing = createMemo(async (): Promise<Listing> => {
    const current = path();
    if (current === null) {
      const { data, error } = await api.GET("/api/v1/machines/{id}/files/roots", { params: id() });
      return { path: null, parent: null, entries: (data?.roots ?? []).map(directory), truncated: false, error: error && asText(error) };
    }
    const { data, error } = await api.GET("/api/v1/machines/{id}/files", { params: { ...id(), query: { path: current } } });
    return {
      path: current,
      parent: data?.parent ?? null,
      entries: data?.entries ?? [],
      truncated: data?.truncated ?? false,
      error: error && asText(error),
    };
  });

  const preview = createMemo(async () => {
    const file = opened();
    if (!file) return undefined;
    const { data, error } = await api.GET("/api/v1/machines/{id}/files/text", {
      params: { ...id(), query: { path: file.path } },
    });
    if (error) return asText(error);
    if (data?.binary) return "This file is binary; download it to see it.";
    const more = Number(data?.totalSize ?? 0) - Number(data?.nextOffset ?? 0);
    return (data?.text ?? "") + (more > 0 ? `\n… ${formatSize(more)} more` : "");
  });

  function open(entry: FileEntry) {
    setNotice(undefined);
    if (entry.isDirectory) {
      setOpened(null);
      setPath(entry.path);
    } else setOpened(entry);
  }

  async function download(entry: FileEntry) {
    setNotice(`Fetching ${entry.name}…`);
    const { data, error } = await api.POST("/api/v1/machines/{id}/files/fetch", {
      params: id(),
      body: { path: entry.path },
    });
    if (!data) return setNotice(asText(error));
    setNotice(undefined);
    await getPlatform().saveBlob(data.blob.sha256, data.name);
  }

  async function store(directory: string, file: File, sha256: string, overwrite: boolean): Promise<void> {
    const { data, error, response } = await api.POST("/api/v1/machines/{id}/files/store", {
      params: id(),
      body: { directory, name: file.name, sha256, overwrite },
    });
    if (data) {
      setNotice(`Uploaded ${file.name}.`);
      refresh(listing);
    } else if (!overwrite && response.status === 400 && asText(error).endsWith("already exists.")) {
      if (confirm(`${file.name} already exists here. Replace it?`)) await store(directory, file, sha256, true);
      else setNotice(undefined);
    } else setNotice(asText(error));
  }

  async function upload(event: Event & { currentTarget: HTMLInputElement }) {
    const input = event.currentTarget;
    const file = input.files?.[0];
    const target = path();
    input.value = "";
    if (!file || target === null) return;
    setNotice(`Uploading ${file.name}…`);
    const uploaded = await uploadBlob(file);
    if (!uploaded.sha256) return setNotice(uploaded.error);
    await store(target, file, uploaded.sha256, false);
  }

  return (
    <section class="rounded-lg border border-line bg-panel">
      <div class="px-4 py-3 border-b border-line flex items-center justify-between gap-3">
        <h2 class="text-sm font-medium truncate">{listing().path ?? "Files"}</h2>
        <Show when={listing().path !== null}>
          <span class="flex gap-3 text-xs shrink-0">
            <button class="text-accent hover:underline" onClick={() => props.onTerminalHere(listing().path!)}>
              Terminal here
            </button>
            <label class="text-accent cursor-pointer hover:underline">
              Upload here
              <input type="file" class="hidden" onChange={upload} />
            </label>
          </span>
        </Show>
      </div>
      <Show when={notice()}>
        <p class="px-4 py-2 border-b border-line text-sm text-muted">{notice()}</p>
      </Show>
      <Show when={listing().error}>
        <p class="px-4 py-2 border-b border-line text-sm text-red-600">{listing().error}</p>
      </Show>
      <ul class="divide-y divide-line text-sm">
        <Show when={listing().path !== null}>
          <li class="px-4 py-1.5">
            <button class="text-accent hover:underline" onClick={() => (setOpened(null), setPath(listing().parent))}>
              ..
            </button>
          </li>
        </Show>
        <For each={listing().entries} keyed={(entry) => entry.path}>
          {(entry) => (
            <li class="px-4 py-1.5 flex items-center justify-between gap-3">
              <button class="truncate text-left hover:underline" onClick={() => open(entry())}>
                {entry().name}
                {entry().isDirectory ? "/" : ""}
                {entry().isLink ? " →" : ""}
              </button>
              <Show when={!entry().isDirectory}>
                <span class="text-xs text-muted shrink-0">
                  {formatSize(Number(entry().size))}{" "}
                  <button class="text-accent hover:underline" onClick={() => download(entry())}>
                    Download
                  </button>
                </span>
              </Show>
            </li>
          )}
        </For>
        <Show when={listing().entries.length === 0 && !listing().error}>
          <li class="px-4 py-2 text-muted">
            {listing().path === null ? "This machine shares no folders yet: start a session, or run bosofad with --roots." : "Empty."}
          </li>
        </Show>
        <Show when={listing().truncated}>
          <li class="px-4 py-2 text-muted">Only the first entries are shown.</li>
        </Show>
      </ul>
      <Show when={opened()}>
        <div class="border-t border-line">
          <h3 class="px-4 py-2 text-xs text-muted">{opened()!.name}</h3>
          <pre class="px-4 pb-3 text-xs font-mono whitespace-pre-wrap break-words max-h-[60vh] overflow-auto">{preview()}</pre>
        </div>
      </Show>
    </section>
  );
}

export default function MachinePage() {
  const params = useParams<{ id: string }>();
  const machineId = () => Number(params.id);
  let open: (directory?: string) => void = () => {};
  const navigate = useNavigate();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent(location.pathname)}`, { replace: true });
    },
  );
  const machine = createMemo(async () => {
    const id = machineId();
    return ((await api.GET("/api/v1/machines")).data ?? []).find((candidate) => candidate.id === id);
  });

  return (
    <div class="space-y-4">
      <h1 class="text-lg font-medium">{machine()?.name ?? "Machine"}</h1>
      <Loading fallback={null}>
        <CodexLimits machineId={machineId()} />
      </Loading>
      <Files machineId={machineId()} onTerminalHere={(directory) => open(directory)} />
      <Terminals machineId={machineId()} ref={(controls) => (open = controls.open)} />
    </div>
  );
}
