import { api, type RuntimeRequest, type SessionItem } from "@bosofa/api";
import { useNavigate, useParams } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, Match, Show, Switch, untrack } from "solid-js";
import { StatusBadge } from "../components/StatusBadge";
import { createLiveSession } from "../live";
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

function Item(props: { item: SessionItem }) {
  return (
    <Switch>
      <Match when={props.item.kind === "User"}>
        <div class="flex justify-end">
          <div class="max-w-[85%] rounded-lg bg-accent/10 px-3 py-2 whitespace-pre-wrap">{props.item.text}</div>
        </div>
      </Match>
      <Match when={props.item.kind === "Assistant"}>
        <div class="whitespace-pre-wrap leading-relaxed">{props.item.text}</div>
      </Match>
      <Match when={props.item.kind === "Thinking"}>
        <details class="text-sm text-muted">
          <summary class="cursor-pointer select-none">Thinking</summary>
          <div class="mt-1 whitespace-pre-wrap italic">{props.item.text}</div>
        </details>
      </Match>
      <Match when={props.item.kind === "ToolUse"}>
        <details class="rounded-md border border-line bg-panel text-sm">
          <summary class="cursor-pointer select-none px-3 py-1.5 font-mono">
            <span class="font-semibold">{props.item.toolName}</span>{" "}
            <span class="text-muted">{props.item.text}</span>
          </summary>
          <pre class="overflow-x-auto border-t border-line px-3 py-2 text-xs">{pretty(props.item.data)}</pre>
        </details>
      </Match>
      <Match when={props.item.kind === "ToolResult"}>
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
          </div>
        )}
      </Match>
      <Match when={props.item.kind === "Notice" && uploaded(props.item)}>
        {(file) => (
          <p class="text-sm text-muted">
            {props.item.text}{" "}
            <a class="text-accent underline" href={`/api/v1/blobs/${file().blob}`} download={file().fileName}>
              Download
            </a>
          </p>
        )}
      </Match>
      <Match when={props.item.kind === "Notice"}>
        <p class={["text-sm", props.item.isError ? "text-red-600" : "text-muted"]}>{props.item.text}</p>
      </Match>
    </Switch>
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

function Composer(props: { sessionId: number; disabled: boolean; running: boolean }) {
  const [text, setText] = createSignal("");
  const [error, setError] = createSignal<string>();

  /** Queue waits for the running turn; Steer redirects it now. */
  async function send(mode: "Queue" | "Steer" = "Queue") {
    const value = text().trim();
    if (!value) return;
    const { response } = await api.POST("/api/v1/sessions/{id}/messages", {
      params: { path: { id: props.sessionId } },
      body: { text: value, mode },
    });
    if (response.ok) {
      setText("");
      setError(undefined);
    } else setError(`Sending failed (${response.status}).`);
  }

  async function interrupt() {
    await api.POST("/api/v1/sessions/{id}/interrupt", { params: { path: { id: props.sessionId } } });
  }

  return (
    <div class="sticky bottom-0 -mx-4 sm:-mx-6 border-t border-line bg-canvas/95 px-4 sm:px-6 py-3 backdrop-blur">
      <div class="flex items-end gap-2">
        <textarea
          class="min-h-11 flex-1 resize-y rounded-md border border-line bg-panel px-3 py-2 text-sm"
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
            disabled={text().trim() === ""}
            onClick={() => send("Steer")}
          >
            Steer
          </button>
        </Show>
        <button
          class="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
          title={props.running ? "Send after the running turn ends" : undefined}
          disabled={props.disabled || text().trim() === ""}
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
              {session().machineName} · {session().harness}
              <Show when={session().model}>{(model) => <> · {model()}</>}</Show>
              <Show when={session().accountLabel}>{(account) => <> · {account()}</>}</Show>
              <Show when={session().profileName}>{(profile) => <> · {profile()}</>}</Show>
              <Show when={session().branch}>{(branch) => <> · <span class="font-mono">{branch()}</span></>}</Show> ·{" "}
              <span class="font-mono">{session().workingDirectory}</span> · {session().permissionMode}
              <Show when={Number(session().costUsd) > 0}> · ${Number(session().costUsd).toFixed(4)}</Show>
            </p>
          </header>
        )}
      </Show>
      <div class="space-y-4 pb-4">
        <For each={live.items}>{(item) => <Item item={item} />}</For>
        <For each={live.drafts}>
          {(draft) => (
            <div class="whitespace-pre-wrap leading-relaxed text-ink/80">
              {draft.text}
              <span class="ml-0.5 inline-block h-4 w-1.5 animate-pulse bg-accent align-text-bottom" />
            </div>
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
      />
    </div>
  );
}
