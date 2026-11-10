import { AttachmentStrip, createAttachments, SentImages } from "../components/PromptImages";
import { api, getPlatform, type RuntimeRequest, type Session, type SessionItem } from "@bosofa/api";
import { useNavigate, useParams } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, Match, onSettled, Show, Switch, untrack } from "solid-js";
import { StatusBadge } from "../components/StatusBadge";
import { createLiveSession, isThinking, isToolOutput, type Draft } from "../live";
import { paths } from "../router";
import { fetchMe } from "../session";

function pretty(json: string | null | undefined): string {
  if (!json) return "";
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

type RunEnded = { costUsd?: number; inputTokens?: number; outputTokens?: number; durationMs?: number };

function runSummary(item: SessionItem): string {
  let run: RunEnded = {};
  try {
    run = JSON.parse(item.data ?? "{}") as RunEnded;
  } catch {
    /* keep the outcome alone */
  }
  const parts = [item.text === "completed" ? "Turn completed" : `Turn ended: ${item.text}`];
  if (run.durationMs) parts.push(`${(run.durationMs / 1000).toFixed(1)} s`);
  if (run.inputTokens || run.outputTokens)
    parts.push(`${(run.inputTokens ?? 0).toLocaleString()} in / ${(run.outputTokens ?? 0).toLocaleString()} out`);
  if (run.costUsd) parts.push(`$${run.costUsd.toFixed(4)}`);
  return parts.join(" · ");
}

/** A device-code sign-in notice carries its link and code as data. */
function signIn(item: SessionItem): { verificationUrl: string; userCode: string } | undefined {
  try {
    const data = JSON.parse(item.data ?? "null") as { verificationUrl?: string; userCode?: string } | null;
    // Only ever an https link: the data comes from the daemon.
    if (data?.verificationUrl?.startsWith("https://") && data.userCode)
      return { verificationUrl: data.verificationUrl, userCode: data.userCode };
  } catch {
    /* an ordinary notice */
  }
  return undefined;
}

type DiffData = { files?: { path: string; added: number; removed: number }[]; added?: number; removed?: number };

function DiffItem(props: { item: SessionItem }) {
  const data = createMemo((): DiffData => {
    try {
      return JSON.parse(props.item.data ?? "{}") as DiffData;
    } catch {
      return {};
    }
  });
  const lines = createMemo(() => props.item.text.split("\n"));
  return (
    <details class="rounded-md border border-line bg-panel text-sm">
      <summary class="cursor-pointer select-none px-3 py-1.5">
        <span class="font-medium">Changed {data().files?.length ?? 0} files</span>{" "}
        <span class="text-green-600">+{data().added ?? 0}</span> <span class="text-red-600">−{data().removed ?? 0}</span>
      </summary>
      <ul class="border-t border-line px-3 py-2 font-mono text-xs">
        <For each={data().files ?? []}>
          {(file) => (
            <li>
              {file.path} <span class="text-green-600">+{file.added}</span>{" "}
              <span class="text-red-600">−{file.removed}</span>
            </li>
          )}
        </For>
      </ul>
      <pre class="max-h-96 overflow-auto border-t border-line px-3 py-2 text-xs">
        <For each={lines()} keyed={false}>
          {(line) => (
            <div
              class={{
                "text-green-600": line().startsWith("+"),
                "text-red-600": line().startsWith("-"),
                "text-muted": line().startsWith("@@"),
              }}
            >
              {line() || " "}
            </div>
          )}
        </For>
      </pre>
    </details>
  );
}

/** A file the agent uploaded with bosofa_upload_file. */
function uploaded(item: SessionItem): { blob: string; fileName: string } | undefined {
  try {
    const data = JSON.parse(item.data ?? "null") as { blob?: string; fileName?: string } | null;
    if (data?.blob && /^[0-9a-f]{64}$/.test(data.blob)) return { blob: data.blob, fileName: data.fileName ?? "download" };
  } catch {
    /* an ordinary notice */
  }
  return undefined;
}

type ToolView = {
  results: Map<number, SessionItem>;
  paired: Set<number>;
  draftOf: (seq: number) => Draft | undefined;
  waitingFor: (item: SessionItem) => boolean;
  running: () => boolean;
};

/** Each result goes with the latest call of its id: some models use the same id at every step. */
function pairTools(items: readonly SessionItem[]): { results: Map<number, SessionItem>; paired: Set<number> } {
  const open = new Map<string, number>();
  const results = new Map<number, SessionItem>();
  const paired = new Set<number>();
  for (const item of items) {
    if (item.kind === "ToolUse" && item.toolUseId) open.set(item.toolUseId, Number(item.seq));
    if (item.kind === "ToolResult" && item.toolUseId && open.has(item.toolUseId)) {
      results.set(open.get(item.toolUseId)!, item);
      paired.add(Number(item.seq));
      open.delete(item.toolUseId);
    }
  }
  return { results, paired };
}

function argumentsOf(item: SessionItem): Record<string, unknown> {
  try {
    return (JSON.parse(item.data ?? "{}") as Record<string, unknown>) ?? {};
  } catch {
    return {};
  }
}

/** What the call is about, in a few words: the command, the file or the pattern, under the names the harnesses use. */
function toolHeadline(item: SessionItem): string {
  const args = argumentsOf(item);
  for (const name of ["command", "path", "file_path", "pattern", "url", "query", "question", "message"]) {
    const value = args[name];
    if (typeof value === "string" && value) return value;
  }
  return item.text === "{}" ? "" : item.text;
}

/** The arguments as they matter: an edit as a change, a command as a command, anything else as JSON. */
function ToolArguments(props: { item: SessionItem }) {
  const args = createMemo(() => argumentsOf(props.item));
  const oldText = () => args().old_text ?? args().old_string;
  const newText = () => args().new_text ?? args().new_string;
  return (
    <Switch fallback={<pre class="overflow-x-auto px-3 py-2 text-xs">{pretty(props.item.data)}</pre>}>
      <Match when={typeof args().command === "string"}>
        <pre class="overflow-x-auto whitespace-pre-wrap px-3 py-2 text-xs">$ {args().command as string}</pre>
      </Match>
      <Match when={typeof oldText() === "string" && typeof newText() === "string"}>
        <pre class="overflow-x-auto px-3 py-2 text-xs">
          <div class="text-red-600 whitespace-pre-wrap">{oldText() as string}</div>
          <div class="text-green-600 whitespace-pre-wrap">{newText() as string}</div>
        </pre>
      </Match>
      <Match when={typeof args().input === "string"}>
        <pre class="max-h-96 overflow-auto px-3 py-2 text-xs">{args().input as string}</pre>
      </Match>
      <Match when={typeof args().content === "string"}>
        <pre class="max-h-96 overflow-auto px-3 py-2 text-xs">{args().content as string}</pre>
      </Match>
    </Switch>
  );
}

/** One tool call from start to finish: what it does, its state, and its output as it comes. */
function ToolCard(props: { item: SessionItem; result?: SessionItem; draft?: Draft; waiting: boolean; running: boolean }) {
  const failed = () => props.result?.isError ?? false;
  const state = () => {
    if (props.result) return failed() ? "failed" : "done";
    if (props.waiting) return "waiting";
    return props.running ? "running" : "stopped";
  };
  // A command's verdict is at the end of its output; anything else introduces itself.
  const outcome = () => {
    const lines = props.result?.text.split("\n").filter((line) => line.trim()) ?? [];
    return (typeof argumentsOf(props.item).command === "string" ? lines.at(-1) : lines[0])?.slice(0, 90) ?? "";
  };
  return (
    <details class={["rounded-md border bg-panel text-sm", failed() ? "border-red-500/40" : "border-line"]} open={props.draft !== undefined}>
      <summary class="flex cursor-pointer select-none items-center gap-2 px-3 py-1.5">
        <span
          title={state()}
          class={[
            "inline-block size-2 shrink-0 rounded-full",
            {
              "bg-green-500": state() === "done",
              "bg-red-500": state() === "failed",
              "bg-amber-500": state() === "waiting",
              "animate-pulse bg-accent": state() === "running",
              "bg-muted": state() === "stopped",
            },
          ]}
        />
        <span class="shrink-0 font-mono font-semibold">{props.item.toolName}</span>
        <span class="min-w-0 truncate font-mono text-muted">{toolHeadline(props.item)}</span>
        <Show when={state() === "waiting"}>
          <span class="ml-auto shrink-0 text-xs text-amber-600">waiting for you</span>
        </Show>
        <Show when={outcome() && outcome() !== toolHeadline(props.item)}>
          <span class={["ml-auto hidden max-w-[40%] shrink-0 truncate text-xs sm:inline", failed() ? "text-red-600" : "text-muted"]}>{outcome()}</span>
        </Show>
      </summary>
      <div class="border-t border-line">
        <ToolArguments item={props.item} />
        <Show when={props.result?.text ?? props.draft?.text}>
          {(output) => <pre class="max-h-96 overflow-auto whitespace-pre-wrap border-t border-line px-3 py-2 text-xs">{output()}</pre>}
        </Show>
        <Show when={props.result?.data}>
          <div class="border-t border-line px-3 pt-2">
            <SentImages data={props.result?.data} />
          </div>
        </Show>
      </div>
    </details>
  );
}

function Item(props: { item: SessionItem; tools?: ToolView }) {
  return (
    <Switch>
      <Match when={props.item.kind === "User"}>
        <div class="flex justify-end">
          <div class="max-w-[85%] rounded-lg bg-accent/10 px-3 py-2 whitespace-pre-wrap">
            <SentImages data={props.item.data} />
            {props.item.text}
          </div>
        </div>
      </Match>
      <Match when={props.item.kind === "Assistant"}>
        <div class="whitespace-pre-wrap leading-relaxed">{props.item.text}</div>
      </Match>
      <Match when={props.item.kind === "Compaction"}>
        <details class="text-sm text-muted">
          <summary class="cursor-pointer select-none">Earlier conversation summarized</summary>
          <div class="mt-1 whitespace-pre-wrap">{props.item.text}</div>
        </details>
      </Match>
      <Match when={props.item.kind === "Instructions"}>
        <details class="text-sm text-muted">
          <summary class="cursor-pointer select-none">System prompt</summary>
          <div class="mt-1 whitespace-pre-wrap">{props.item.text}</div>
        </details>
      </Match>
      <Match when={props.item.kind === "Thinking" && props.item.text}>
        <details class="text-sm text-muted">
          <summary class="cursor-pointer select-none">Thinking</summary>
          <div class="mt-1 whitespace-pre-wrap italic">{props.item.text}</div>
        </details>
      </Match>
      <Match when={props.item.kind === "ToolUse"}>
        <ToolCard
          item={props.item}
          result={props.tools?.results.get(Number(props.item.seq))}
          draft={props.tools?.draftOf(Number(props.item.seq))}
          waiting={props.tools?.waitingFor(props.item) ?? false}
          running={props.tools?.running() ?? false}
        />
      </Match>
      <Match when={props.item.kind === "ToolResult" && !props.tools?.paired.has(Number(props.item.seq))}>
        <details class={["rounded-md border text-sm", props.item.isError ? "border-red-500/40" : "border-line"]}>
          <summary class={["cursor-pointer select-none px-3 py-1.5", { "text-red-600": props.item.isError }]}>
            {props.item.isError ? "Tool error" : "Tool output"}{" "}
            <span class="text-muted">{props.item.text.split("\n", 1)[0].slice(0, 120)}</span>
          </summary>
          <pre class="max-h-96 overflow-auto border-t border-line px-3 py-2 text-xs whitespace-pre-wrap">
            {props.item.text}
          </pre>
        </details>
      </Match>
      <Match when={props.item.kind === "Diff"}>
        <DiffItem item={props.item} />
      </Match>
      <Match when={props.item.kind === "RunEnded"}>
        <p class={["text-xs", props.item.isError ? "text-red-600" : "text-muted"]}>{runSummary(props.item)}</p>
      </Match>
      <Match when={props.item.kind === "Notice" && signIn(props.item)}>
        {(login) => (
          <div class="rounded-lg border border-amber-500/50 bg-amber-500/5 p-3 text-sm">
            <p>Sign in to Codex on this machine. The session continues afterwards.</p>
            <p class="mt-2">
              <a class="text-accent underline" href={login().verificationUrl} target="_blank" rel="noreferrer">
                {login().verificationUrl}
              </a>{" "}
              and enter <code class="font-mono font-semibold">{login().userCode}</code>
            </p>
            <p class="mt-2 text-xs text-muted">
              If the page says device code sign-in is off, turn it on in ChatGPT's security settings and start the session again.
            </p>
          </div>
        )}
      </Match>
      <Match when={props.item.kind === "Notice" && uploaded(props.item)}>
        {(file) => (
          <p class="text-sm text-muted">
            {props.item.text}{" "}
            <button class="text-accent underline" onClick={() => void getPlatform().saveBlob(file().blob, file().fileName)}>
              Download
            </button>
          </p>
        )}
      </Match>
      <Match when={props.item.kind === "Notice"}>
        <p class={["text-sm", props.item.isError ? "text-red-600" : "text-muted"]}>{props.item.text}</p>
      </Match>
    </Switch>
  );
}

const CONTEXT_CELLS = 12;

function formatTokens(tokens: number): string {
  return tokens >= 1_000_000 ? `${(tokens / 1_000_000).toFixed(1)}M` : `${Math.round(tokens / 1000)}K`;
}

/** The window of the plan closest to its limit, and whether use is running ahead of the clock. */
function planWindow(session: Session, now: number) {
  const usage = session.accountUsage;
  if (!usage) return undefined;
  const windows = [
    { window: usage.fiveHour, hours: 5, name: "5 hours" },
    { window: usage.sevenDay, hours: 168, name: "week" },
  ].flatMap((entry) => (entry.window ? [{ ...entry, window: entry.window }] : []));
  // A window that has reset says nothing any more.
  const current = windows.filter((entry) => !entry.window.resetsAt || Date.parse(entry.window.resetsAt) > now);
  if (current.length === 0) return undefined;
  const closest = current.reduce((a, b) => (Number(b.window.utilization) > Number(a.window.utilization) ? b : a));
  const left = closest.window.resetsAt ? Date.parse(closest.window.resetsAt) - now : undefined;
  const elapsed = left === undefined ? undefined : 1 - left / (closest.hours * 3_600_000);
  const used = Number(closest.window.utilization);
  return { percent: Math.round(used * 100), left, ahead: elapsed !== undefined && used > elapsed + 0.02, name: closest.name };
}

function formatLeft(ms: number): string {
  const minutes = Math.max(0, Math.floor(ms / 60_000));
  if (minutes >= 1440) return `${Math.floor(minutes / 1440)}d`;
  return `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, "0")}`;
}

function BarDivider() {
  return <span class="text-white/25">|</span>;
}

/** A glance at the session: who answers, how full the context is, what is about to lapse, and where it works. What is unknown is left out. */
function SessionBar(props: { session: Session }) {
  const [now, setNow] = createSignal(Date.now());
  onSettled(() => {
    const timer = setInterval(() => setNow(Date.now()), 15_000);
    return () => clearInterval(timer);
  });
  const context = createMemo(() => {
    const used = Number(props.session.contextTokens ?? 0);
    const limit = Number(props.session.contextLimit ?? 0);
    return used > 0 && limit > 0 ? { used, limit, percent: Math.min(100, Math.round((used / limit) * 100)) } : undefined;
  });
  const contextColor = () => {
    const percent = context()?.percent ?? 0;
    return percent < 50 ? "bg-green-500" : percent < 80 ? "bg-amber-400" : "bg-red-500";
  };
  const cacheMinutes = createMemo(() => {
    const expires = props.session.cacheExpiresAt;
    if (!expires) return undefined;
    const left = Date.parse(expires) - now();
    return left > 0 ? Math.ceil(left / 60_000) : undefined;
  });
  const plan = createMemo(() => planWindow(props.session, now()));
  const project = () => props.session.workingDirectory.split(/[\\/]/).filter(Boolean).slice(-2).join("/");
  return (
    <div class="mb-2 flex flex-wrap items-center gap-x-2.5 gap-y-1 rounded-md bg-[#17181f] px-3 py-1.5 font-mono text-xs text-white/85">
      <Show when={props.session.model}>{(model) => <span>{model()}</span>}</Show>
      <Show when={context()}>
        {(fill) => (
          <>
            <BarDivider />
            <span class="flex items-center gap-2" title={`${formatTokens(fill().used)} of ${formatTokens(fill().limit)} tokens. Near the limit the conversation is summarized.`}>
              <span class="flex h-3 overflow-hidden rounded-sm bg-[repeating-linear-gradient(135deg,rgba(255,255,255,.18)_0_1px,transparent_1px_3px)]" style={{ width: `${CONTEXT_CELLS * 6}px` }}>
                <span class={contextColor()} style={{ width: `${fill().percent}%` }} />
              </span>
              <span>{fill().percent}%</span>
            </span>
          </>
        )}
      </Show>
      <Show when={cacheMinutes()}>
        {(minutes) => (
          <span class="text-amber-300" title="The prompt cache lapses then, and the next message costs much more.">
            ⧗ {minutes()} min
          </span>
        )}
      </Show>
      <Show when={plan()}>
        {(usage) => (
          <>
            <BarDivider />
            <span title={`The ${usage().name} limit of this plan${usage().ahead ? ", used faster than the clock" : ""}.`}>
              <span class={usage().percent >= 90 ? "text-red-400" : usage().percent >= 70 ? "text-amber-300" : ""}>
                {usage().percent}%{usage().ahead ? "↑" : ""}
              </span>
              <Show when={usage().left}>{(left) => <> {formatLeft(left())}</>}</Show>
            </span>
          </>
        )}
      </Show>
      <Show when={project()}>
        {(name) => (
          <>
            <BarDivider />
            <Show when={Number(props.session.machineId) > 0} fallback={<span>{name()}</span>}>
              <a
                class="hover:underline"
                href={`/machines/${props.session.machineId}?path=${encodeURIComponent(props.session.workingDirectory)}`}
                title={props.session.workingDirectory}
              >
                {name()}
              </a>
            </Show>
          </>
        )}
      </Show>
      <Show when={props.session.branch}>
        {(branch) => (
          <>
            <BarDivider />
            <span class="text-green-300" title="Working in its own copy of the project.">
              ⎇ {branch()}
            </span>
          </>
        )}
      </Show>
      <Show when={Number(props.session.costUsd) > 0}>
        <BarDivider />
        <span title="At list prices.">${Number(props.session.costUsd).toFixed(4)}</span>
      </Show>
    </div>
  );
}

function Approval(props: { request: RuntimeRequest; canControl: boolean; sessionId: number }) {
  const [message, setMessage] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string>();

  async function answer(allow: boolean) {
    setBusy(true);
    const { response } = await api.POST("/api/v1/sessions/{id}/requests/{requestId}/answer", {
      params: { path: { id: props.sessionId, requestId: Number(props.request.id) } },
      body: { allow, message: message().trim() || null },
    });
    setBusy(false);
    if (!response.ok) setError(`Answering failed (${response.status}).`);
  }

  return (
    <div class="rounded-lg border border-amber-500/50 bg-amber-500/5 p-3 space-y-2">
      <p class="text-sm">
        <span class="font-semibold">{props.request.toolName}</span> wants to run
        <Show when={props.request.description}>{(description) => <span class="text-muted"> · {description()}</span>}</Show>
      </p>
      <pre class="max-h-64 overflow-auto rounded bg-panel px-3 py-2 text-xs">{pretty(props.request.input)}</pre>
      <Show
        when={props.canControl}
        fallback={<p class="text-xs text-muted">Only the machine's owner can answer.</p>}
      >
        <div class="flex flex-wrap items-center gap-2">
          <button
            class="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
            disabled={busy()}
            onClick={() => answer(true)}
          >
            Allow
          </button>
          <button
            class="rounded-md border border-line px-3 py-1.5 text-sm disabled:opacity-50"
            disabled={busy()}
            onClick={() => answer(false)}
          >
            Deny
          </button>
          <input
            class="min-w-48 flex-1 rounded-md border border-line bg-transparent px-2 py-1.5 text-sm"
            placeholder="Reason or answer for the model (optional)"
            value={message()}
            onInput={(event) => setMessage(event.currentTarget.value)}
          />
        </div>
      </Show>
      <Show when={error()}>{(text) => <p class="text-sm text-red-600">{text()}</p>}</Show>
    </div>
  );
}

function Composer(props: { sessionId: number; disabled: boolean; running: boolean; session?: Session }) {
  const [text, setText] = createSignal("");
  const [error, setError] = createSignal<string>();
  const attachments = createAttachments();

  /** Queue waits for the running turn; Steer redirects it now. */
  async function send(mode: "Queue" | "Steer" = "Queue") {
    const value = text().trim();
    if (!value || attachments.uploading()) return;
    const { response, error: problem } = await api.POST("/api/v1/sessions/{id}/messages", {
      params: { path: { id: props.sessionId } },
      body: { text: value, mode, images: attachments.hashes() },
    });
    if (response.ok) {
      setText("");
      setError(undefined);
      attachments.clear();
    } else setError(typeof problem === "string" ? problem : `Sending failed (${response.status}).`);
  }

  async function interrupt() {
    await api.POST("/api/v1/sessions/{id}/interrupt", { params: { path: { id: props.sessionId } } });
  }

  return (
    <div class="sticky bottom-0 -mx-4 sm:-mx-6 border-t border-line bg-canvas/95 px-4 sm:px-6 py-3 backdrop-blur">
      <Show when={props.session}>{(session) => <SessionBar session={session()} />}</Show>
      <AttachmentStrip attachments={attachments} />
      <div class="flex items-end gap-2">
        <textarea
          class="min-h-11 flex-1 resize-y rounded-md border border-line bg-panel px-3 py-2 text-sm"
          onPaste={attachments.onPaste}
          onDragOver={attachments.onDragOver}
          onDrop={attachments.onDrop}
          rows={2}
          placeholder={props.disabled ? "Only the machine's owner can write here." : "Message (⌘/Ctrl+Enter to send or queue)"}
          disabled={props.disabled}
          value={text()}
          onInput={(event) => setText(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter" && (event.metaKey || event.ctrlKey)) {
              event.preventDefault();
              void send();
            }
          }}
        />
        <Show when={props.running && !props.disabled}>
          <button class="rounded-md border border-line px-3 py-2 text-sm" onClick={interrupt}>
            Interrupt
          </button>
        </Show>
        <Show when={props.running && !props.disabled}>
          <button
            class="rounded-md border border-accent px-3 py-2 text-sm text-accent disabled:opacity-50"
            title="Send now and redirect the running turn"
            disabled={text().trim() === "" || attachments.uploading()}
            onClick={() => send("Steer")}
          >
            Steer
          </button>
        </Show>
        <button
          class="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
          title={props.running ? "Send after the running turn ends" : undefined}
          disabled={props.disabled || text().trim() === "" || attachments.uploading()}
          onClick={() => send("Queue")}
        >
          {props.running ? "Queue" : "Send"}
        </button>
      </div>
      <Show when={error()}>{(message) => <p class="mt-1 text-sm text-red-600">{message()}</p>}</Show>
    </div>
  );
}

/** One session, live: transcript, streamed text, approvals and the composer. */
export default function SessionPage() {
  const navigate = useNavigate();
  const params = useParams<{ id: string }>();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null)
        navigate(`${paths.login}?returnUrl=${encodeURIComponent(location.pathname)}`, { replace: true });
    },
  );
  // Keyed: moving to another session builds a new view (and live subscription) for it.
  return (
    <Show when={me() ? params.id : undefined} keyed>
      {(id) => <SessionView id={Number(id)} />}
    </Show>
  );
}

function SessionView(props: { id: number }) {
  const sessionId = untrack(() => props.id);
  const live = createLiveSession(sessionId);
  const running = () =>
    live.session?.status === "Running" ||
    live.session?.status === "Starting" ||
    live.session?.status === "WaitingForApproval";

  const paired = createMemo(() => pairTools(live.items));
  const tools: ToolView = {
    get results() {
      return paired().results;
    },
    get paired() {
      return paired().paired;
    },
    draftOf: (seq) => live.drafts.find((draft) => draft.ref === `tool-${seq}`),
    waitingFor: (item) => live.requests.some((request) => request.toolUseId === item.toolUseId),
    running,
  };
  // A tool's output shows in its card; anything else streaming goes at the end.
  const loneDrafts = createMemo(() =>
    live.drafts.filter((draft) => !(isToolOutput(draft) && live.items.some((item) => draft.ref === `tool-${item.seq}`))),
  );

  let end: HTMLDivElement | undefined;
  createEffect(
    () => live.items.length + live.drafts.reduce((total, draft) => total + draft.text.length, 0) + live.requests.length,
    () => {
      // Follow new output while the reader is at the bottom.
      if (end && window.innerHeight + window.scrollY >= document.body.scrollHeight - 200)
        end.scrollIntoView({ block: "end" });
    },
  );

  return (
    <div class="mx-auto max-w-3xl">
      <Show
        when={live.session}
        fallback={<p class="text-muted">{live.error ?? "Connecting…"}</p>}
      >
        {(session) => (
          <header class="mb-4 space-y-1">
            <div class="flex flex-wrap items-center gap-3">
              <h1 class="text-xl font-semibold">{session().title}</h1>
              <StatusBadge status={session().status} />
              <Show when={!live.connected}>
                <span class="text-xs text-muted">reconnecting…</span>
              </Show>
            </div>
            <p class="text-sm text-muted">
              {session().machineName || "No machine"} · {session().harness}
              <Show when={session().model}>{(model) => <> · {model()}</>}</Show>
              <Show when={session().accountLabel}>{(account) => <> · {account()}</>}</Show>
              <Show when={session().profileName}>{(profile) => <> · {profile()}</>}</Show>
              <Show when={session().branch}>{(branch) => <> · <span class="font-mono">{branch()}</span></>}</Show> ·{" "}
              <Show when={session().workingDirectory}>{(directory) => <><span class="font-mono">{directory()}</span> · </>}</Show>
              {session().permissionMode}
            </p>
          </header>
        )}
      </Show>
      <div class="space-y-4 pb-4">
        <For each={live.items}>{(item) => <Item item={item} tools={tools} />}</For>
        <For each={loneDrafts()}>
          {(draft) => (
            <Switch
              fallback={
                <div class="whitespace-pre-wrap leading-relaxed text-ink/80">
                  {draft.text}
                  <span class="ml-0.5 inline-block h-4 w-1.5 animate-pulse bg-accent align-text-bottom" />
                </div>
              }
            >
              <Match when={isThinking(draft)}>
                <div class="max-h-40 overflow-auto whitespace-pre-wrap text-sm italic text-muted">{draft.text}</div>
              </Match>
              <Match when={isToolOutput(draft)}>
                <pre class="max-h-80 overflow-auto rounded-md border border-line bg-panel px-3 py-2 text-xs">{draft.text}</pre>
              </Match>
            </Switch>
          )}
        </For>
        <For each={live.requests} keyed={(request) => request.id}>
          {(request) => (
            <Approval
              request={request()}
              sessionId={sessionId}
              canControl={live.session?.canControl ?? false}
            />
          )}
        </For>
        <div ref={(element) => (end = element)} />
      </div>
      <Composer
        sessionId={sessionId}
        disabled={!(live.session?.canControl ?? false) || live.session?.status === "Stopped"}
        running={running()}
        session={live.session}
      />
    </div>
  );
}
