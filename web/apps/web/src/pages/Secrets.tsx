import { api, type Secret, type SecretKind, type SecretScope } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, onSettled, refresh, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";
import { isAdmin } from "../workspace";

const field = "w-full rounded-md border border-line bg-panel px-3 py-2 text-sm";

function reference(kind: SecretKind, name: string) {
  return kind === "Secret" ? `\${secret:${name}}` : `\${var:${name}}`;
}

function problemOf(problem: unknown, status: number) {
  if (typeof problem === "string") return problem;
  return status === 403 ? "Only an admin changes shared values." : `That did not work (${status}).`;
}

/** The reference to write in a configuration, with a button that copies it. */
function Reference(props: { kind: SecretKind; name: string }) {
  const [copied, setCopied] = createSignal(false);
  return (
    <button
      type="button"
      class="font-mono text-xs text-muted hover:text-accent"
      title="Copy the reference"
      onClick={async () => {
        await navigator.clipboard.writeText(reference(props.kind, props.name));
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      }}
    >
      {copied() ? "Copied" : reference(props.kind, props.name)}
    </button>
  );
}

/** The new value of a secret or a variable, typed where it opens. */
function ValueForm(props: { secret: Secret; busy: boolean; onSave: (value: string) => void; onCancel: () => void }) {
  const [value, setValue] = createSignal("");
  let input: HTMLInputElement | undefined;
  onSettled(() => input?.focus());
  return (
    <form
      class="flex items-center gap-2"
      onSubmit={(event) => {
        event.preventDefault();
        props.onSave(value());
      }}
    >
      <input
        ref={input}
        class={[field, "py-1 font-mono"]}
        type={props.secret.kind === "Secret" ? "password" : "text"}
        autocomplete="off"
        placeholder={props.secret.kind === "Secret" ? "New value" : (props.secret.value ?? "")}
        required
        value={value()}
        onInput={(event) => setValue(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === "Escape") props.onCancel();
        }}
      />
      <button
        type="submit"
        class="shrink-0 rounded-md bg-accent px-3 py-1 font-medium text-white disabled:opacity-50"
        disabled={props.busy || !value().trim()}
      >
        Save
      </button>
      <button type="button" class="shrink-0 text-xs text-muted hover:text-ink" onClick={() => props.onCancel()}>
        Cancel
      </button>
    </form>
  );
}

/** One value: a secret shows its hint, never its value; rotating gives it a new one. */
function SecretRow(props: { secret: Secret; editable: boolean; onChanged: () => void }) {
  const [editing, setEditing] = createSignal(false);
  const [confirming, setConfirming] = createSignal(false);
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  const path = () => ({ path: { scope: props.secret.scope, name: props.secret.name } });

  async function save(value: string) {
    setBusy(true);
    const { response, error: problem } = await api.PUT("/api/v1/secrets/{scope}/{name}", {
      params: path(),
      body: { kind: props.secret.kind, value },
    });
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    setEditing(false);
    setError(undefined);
    props.onChanged();
  }

  async function remove() {
    setBusy(true);
    const { response, error: problem } = await api.DELETE("/api/v1/secrets/{scope}/{name}", { params: path() });
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    props.onChanged();
  }

  return (
    <li class="space-y-2 px-4 py-2 text-sm">
      <div class="flex items-start justify-between gap-3">
        <div class="min-w-0 space-y-0.5">
          <div class="flex flex-wrap items-baseline gap-x-2">
            <span class="font-mono font-medium">{props.secret.name}</span>
            <Show when={props.secret.kind === "Variable"}>
              <span class="rounded bg-hover px-1.5 text-xs text-muted">variable</span>
            </Show>
            <Reference kind={props.secret.kind} name={props.secret.name} />
          </div>
          <p class="break-all text-xs text-muted">
            <Show when={props.secret.kind === "Variable"} fallback={<span class="font-mono">{props.secret.hint}</span>}>
              <span class="font-mono text-ink">{props.secret.value}</span>
            </Show>{" "}
            · version {props.secret.version} · set by {props.secret.setBy}, {new Date(props.secret.setAt).toLocaleString()}
          </p>
        </div>
        <Show when={props.editable && !editing() && !confirming()}>
          <span class="flex shrink-0 items-center gap-3 text-xs">
            <button type="button" class="text-accent hover:underline" onClick={() => setEditing(true)}>
              {props.secret.kind === "Secret" ? "Rotate" : "Edit"}
            </button>
            <button type="button" class="text-muted hover:text-red-600" onClick={() => setConfirming(true)}>
              Delete
            </button>
          </span>
        </Show>
      </div>
      <Show when={editing()}>
        <ValueForm
          secret={props.secret}
          busy={busy()}
          onSave={save}
          onCancel={() => {
            setEditing(false);
            setError(undefined);
          }}
        />
      </Show>
      <Show when={confirming()}>
        <div class="flex flex-wrap items-center gap-3 text-xs">
          <span>Delete {props.secret.name}? What refers to it stops working until it is set again.</span>
          <button
            type="button"
            class="rounded-md bg-red-600 px-2 py-1 font-medium text-white disabled:opacity-50"
            disabled={busy()}
            onClick={remove}
          >
            Delete
          </button>
          <button type="button" class="text-muted hover:text-ink" onClick={() => setConfirming(false)}>
            Cancel
          </button>
        </div>
      </Show>
      <Show when={error()}>{(message) => <p class="text-xs text-red-600">{message()}</p>}</Show>
    </li>
  );
}

function SecretList(props: { title: string; empty: string; secrets: Secret[]; editable: boolean; onChanged: () => void }) {
  return (
    <section class="rounded-lg border border-line bg-panel">
      <h2 class="border-b border-line px-4 py-3 text-sm font-medium">{props.title}</h2>
      <Show when={props.secrets.length > 0} fallback={<p class="px-4 py-3 text-sm text-muted">{props.empty}</p>}>
        <ul class="divide-y divide-line">
          <For each={props.secrets} keyed={(secret) => `${secret.scope}/${secret.name}`}>
            {(secret) => <SecretRow secret={secret()} editable={props.editable} onChanged={props.onChanged} />}
          </For>
        </ul>
      </Show>
    </section>
  );
}

/** My secrets and variables, and the shared ones: configuration names them instead of holding their values. */
export default function Secrets() {
  const navigate = useNavigate();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent("/secrets")}`, { replace: true });
    },
  );
  const secrets = createMemo(async (): Promise<Secret[]> => (me() ? ((await api.GET("/api/v1/secrets")).data ?? []) : []));
  const personal = createMemo(() => secrets().filter((secret) => secret.scope === "Personal"));
  const shared = createMemo(() => secrets().filter((secret) => secret.scope === "Shared"));
  const [kind, setKind] = createSignal<SecretKind>("Secret");
  const [scope, setScope] = createSignal<SecretScope>("Personal");
  const [name, setName] = createSignal("");
  const [value, setValue] = createSignal("");
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  const reload = () => refresh(secrets);

  async function add(event: SubmitEvent) {
    event.preventDefault();
    const taken = secrets().some((secret) => secret.scope === scope() && secret.name === name().trim());
    if (taken) return setError(`${name().trim()} is already set: rotate or edit it in the list.`);
    setBusy(true);
    const { response, error: problem } = await api.PUT("/api/v1/secrets/{scope}/{name}", {
      params: { path: { scope: scope(), name: name().trim() } },
      body: { kind: kind(), value: value() },
    });
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    setName("");
    setValue("");
    setError(undefined);
    reload();
  }

  return (
    <Show when={me()}>
      {(user) => (
        <div class="mx-auto max-w-2xl space-y-6">
          <div class="space-y-1">
            <h1 class="text-xl font-semibold">Secrets and variables</h1>
            <p class="text-sm text-muted">
              Configuration refers to them by name, with <code class="font-mono">{"${secret:NAME}"}</code> and{" "}
              <code class="font-mono">{"${var:NAME}"}</code>. A secret is encrypted on the server and never shown again; your own
              value of a name comes before a shared one.
            </p>
          </div>
          <SecretList
            title="Yours"
            empty="Nothing yet."
            secrets={personal()}
            editable={true}
            onChanged={reload}
          />
          <SecretList
            title="Shared with everyone"
            empty={isAdmin(user()) ? "Nothing shared yet." : "Nothing shared yet. Admins set shared values."}
            secrets={shared()}
            editable={isAdmin(user())}
            onChanged={reload}
          />
          <form class="space-y-3 rounded-lg border border-line bg-panel p-4" onSubmit={add}>
            <h2 class="text-sm font-medium">Add</h2>
            <div class="flex gap-3">
              <select class={field} value={kind()} onChange={(event) => setKind(event.currentTarget.value as SecretKind)}>
                <option value="Secret">Secret</option>
                <option value="Variable">Variable (not secret)</option>
              </select>
              <Show when={isAdmin(user())}>
                <select class={field} value={scope()} onChange={(event) => setScope(event.currentTarget.value as SecretScope)}>
                  <option value="Personal">Yours</option>
                  <option value="Shared">Shared with everyone</option>
                </select>
              </Show>
            </div>
            <input
              class={[field, "font-mono"]}
              placeholder="Name, e.g. GRAFANA_TOKEN"
              required
              pattern="[A-Za-z_][A-Za-z0-9_]{0,63}"
              title="Letters, digits and _, not starting with a digit"
              value={name()}
              onInput={(event) => setName(event.currentTarget.value)}
            />
            <input
              class={[field, "font-mono"]}
              type={kind() === "Secret" ? "password" : "text"}
              autocomplete="off"
              placeholder="Value"
              required
              value={value()}
              onInput={(event) => setValue(event.currentTarget.value)}
            />
            <div class="flex items-center gap-3">
              <button
                type="submit"
                class="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
                disabled={busy() || !name().trim() || !value().trim()}
              >
                Add
              </button>
              <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
            </div>
          </form>
        </div>
      )}
    </Show>
  );
}
