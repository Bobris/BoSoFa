import {
  api,
  type Environment,
  type EnvironmentApproval,
  type Machine,
  type Secret,
  type SecretKind,
  type User,
} from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, onSettled, refresh, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";
import { isAdmin, useWorkspace } from "../workspace";

const field = "w-full rounded-md border border-line bg-panel px-3 py-2 text-sm";
const badge = "rounded bg-hover px-1.5 text-xs text-muted";
const warning = "rounded bg-amber-500/15 px-1.5 text-xs text-amber-800 dark:text-amber-200";
const primary = "shrink-0 rounded-md bg-accent px-3 py-1 text-sm font-medium text-white disabled:opacity-50";
const quiet = "shrink-0 text-xs text-muted hover:text-ink";

type Id = number | string;
const same = (a: Id, b: Id) => String(a) === String(b);

function reference(kind: SecretKind, name: string) {
  return kind === "Secret" ? `\${secret:${name}}` : `\${var:${name}}`;
}

function problemOf(problem: unknown, status: number) {
  if (typeof problem === "string") return problem;
  return status === 403 ? "Only an admin changes shared values and environments." : `That did not work (${status}).`;
}

function day(at: string) {
  return new Date(at).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

/** Where a value lives, as the routes name it: an environment's values have routes of their own. */
type Place = { scope: Secret["scope"]; name: string; environment?: string | null };

function putValue(place: Place, body: { kind: SecretKind; value: string; hostOnly: boolean; machines?: Id[]; rotateEveryDays?: number }) {
  return place.scope === "Environment"
    ? api.PUT("/api/v1/environments/{environment}/secrets/{name}", { params: { path: { environment: place.environment!, name: place.name } }, body })
    : api.PUT("/api/v1/secrets/{scope}/{name}", { params: { path: { scope: place.scope, name: place.name } }, body });
}

function putSettings(place: Place, body: { hostOnly: boolean; machines: Id[]; rotateEveryDays: number }) {
  return place.scope === "Environment"
    ? api.PUT("/api/v1/environments/{environment}/secrets/{name}/settings", {
        params: { path: { environment: place.environment!, name: place.name } },
        body,
      })
    : api.PUT("/api/v1/secrets/{scope}/{name}/settings", { params: { path: { scope: place.scope, name: place.name } }, body });
}

function deleteValue(place: Place) {
  return place.scope === "Environment"
    ? api.DELETE("/api/v1/environments/{environment}/secrets/{name}", { params: { path: { environment: place.environment!, name: place.name } } })
    : api.DELETE("/api/v1/secrets/{scope}/{name}", { params: { path: { scope: place.scope, name: place.name } } });
}

/** Whether a value should be rotated now: something says so, or its interval has passed. */
function dueForRotation(secret: Secret) {
  return !!secret.rotationReason || (!!secret.rotateBy && Date.parse(secret.rotateBy) <= Date.now());
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

/** Machines to tick; none ticked means any. */
function MachinePicker(props: { machines: Machine[]; chosen: Id[]; showOwner: boolean; onChange: (chosen: Id[]) => void }) {
  return (
    <div class="flex flex-wrap gap-x-4 gap-y-1">
      <For each={props.machines} keyed={(machine) => String(machine.id)} fallback={<span class="text-xs text-muted">No machines yet.</span>}>
        {(machine) => (
          <label class="flex items-center gap-1.5 text-sm">
            <input
              type="checkbox"
              checked={props.chosen.some((id) => same(id, machine().id))}
              onChange={(event) =>
                props.onChange(
                  event.currentTarget.checked ? [...props.chosen, machine().id] : props.chosen.filter((id) => !same(id, machine().id)),
                )
              }
            />
            {machine().name}
            <Show when={props.showOwner}>
              <span class="text-xs text-muted">({machine().ownerName})</span>
            </Show>
          </label>
        )}
      </For>
    </div>
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
      <button type="submit" class={primary} disabled={props.busy || !value().trim()}>
        Save
      </button>
      <button type="button" class={quiet} onClick={() => props.onCancel()}>
        Cancel
      </button>
    </form>
  );
}

/** How a value is used: for MCP servers only, on which machines, and how often it is rotated. Its value stays. */
function SettingsForm(props: {
  secret: Secret;
  choices: Machine[];
  showOwner: boolean;
  busy: boolean;
  onSave: (settings: { hostOnly: boolean; machines: Id[]; rotateEveryDays: number }) => void;
  onCancel: () => void;
}) {
  const [hostOnly, setHostOnly] = createSignal(props.secret.hostOnly ?? false);
  const [machines, setMachines] = createSignal<Id[]>(props.secret.machines ?? []);
  const [days, setDays] = createSignal(Number(props.secret.rotateEveryDays ?? 0) > 0 ? String(props.secret.rotateEveryDays) : "");
  return (
    <form
      class="space-y-3 rounded-md border border-line p-3"
      onSubmit={(event) => {
        event.preventDefault();
        props.onSave({ hostOnly: hostOnly(), machines: machines(), rotateEveryDays: Number(days() || 0) });
      }}
    >
      <Show when={props.secret.kind === "Secret"}>
        <label class="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={hostOnly()} onChange={(event) => setHostOnly(event.currentTarget.checked)} />
          For MCP servers only
        </label>
      </Show>
      <div class="space-y-1">
        <p class="text-xs text-muted">Only for sessions on these machines (none ticked: any machine)</p>
        <MachinePicker machines={props.choices} chosen={machines()} showOwner={props.showOwner} onChange={setMachines} />
      </div>
      <label class="flex items-center gap-2 text-sm">
        Rotate every
        <input
          class="w-20 rounded-md border border-line bg-panel px-2 py-1 text-sm"
          type="number"
          min="0"
          max="3650"
          placeholder="—"
          value={days()}
          onInput={(event) => setDays(event.currentTarget.value)}
        />
        days
      </label>
      <div class="flex items-center gap-3">
        <button type="submit" class={primary} disabled={props.busy}>
          Save
        </button>
        <button type="button" class={quiet} onClick={() => props.onCancel()}>
          Cancel
        </button>
      </div>
    </form>
  );
}

/** One value: a secret shows its hint, never its value; rotating gives it a new one. */
function SecretRow(props: { secret: Secret; editable: boolean; machines: Machine[]; choices: Machine[]; onChanged: () => void }) {
  const [mode, setMode] = createSignal<"value" | "settings" | "delete">();
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  const machineName = (id: Id) => props.machines.find((machine) => same(machine.id, id))?.name ?? "a removed machine";
  const close = () => {
    setMode(undefined);
    setError(undefined);
  };

  async function run(request: Promise<{ response: Response; error?: unknown }>) {
    setBusy(true);
    const { response, error: problem } = await request;
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    close();
    props.onChanged();
  }

  return (
    <li class="space-y-2 px-4 py-2 text-sm">
      <div class="flex items-start justify-between gap-3">
        <div class="min-w-0 space-y-0.5">
          <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
            <span class="font-mono font-medium">{props.secret.name}</span>
            <Show when={props.secret.kind === "Variable"}>
              <span class={badge}>variable</span>
            </Show>
            <Show when={props.secret.hostOnly}>
              <span class={badge} title="Goes to MCP servers, never to an agent's shell or a harness's environment">
                MCP servers only
              </span>
            </Show>
            <Show when={props.secret.machines?.length}>
              <span class={badge} title="Sessions on other machines do not get it">
                only on {props.secret.machines!.map(machineName).join(", ")}
              </span>
            </Show>
            <Show when={dueForRotation(props.secret)}>
              <span class={warning}>rotation due</span>
            </Show>
            <Reference kind={props.secret.kind} name={props.secret.name} />
          </div>
          <p class="break-all text-xs text-muted">
            <Show when={props.secret.kind === "Variable"} fallback={<span class="font-mono">{props.secret.hint}</span>}>
              <span class="font-mono text-ink">{props.secret.value}</span>
            </Show>{" "}
            · version {props.secret.version} · set by {props.secret.setBy}, {new Date(props.secret.setAt).toLocaleString()}
            <Show when={props.secret.rotateBy && !props.secret.rotationReason}>
              {" "}
              · rotate by {day(props.secret.rotateBy!)}
            </Show>
          </p>
          <Show when={props.secret.rotationReason}>
            {(reason) => <p class="text-xs text-amber-800 dark:text-amber-200">{reason()} Rotate it.</p>}
          </Show>
          <Show when={props.secret.usedOn?.length}>
            <p class="text-xs text-muted">
              Used on{" "}
              {props.secret
                .usedOn!.map((use) => `${use.machine} (version ${use.version}, ${new Date(use.at).toLocaleString()})`)
                .join(", ")}
            </p>
          </Show>
        </div>
        <Show when={props.editable && !mode()}>
          <span class="flex shrink-0 items-center gap-3 text-xs">
            <button type="button" class="text-accent hover:underline" onClick={() => setMode("value")}>
              {props.secret.kind === "Secret" ? "Rotate" : "Edit"}
            </button>
            <button type="button" class="text-muted hover:text-ink" onClick={() => setMode("settings")}>
              Settings
            </button>
            <button type="button" class="text-muted hover:text-red-600" onClick={() => setMode("delete")}>
              Delete
            </button>
          </span>
        </Show>
      </div>
      <Show when={mode() === "value"}>
        <ValueForm
          secret={props.secret}
          busy={busy()}
          onSave={(value) => run(putValue(props.secret, { kind: props.secret.kind, value, hostOnly: props.secret.hostOnly ?? false }))}
          onCancel={close}
        />
      </Show>
      <Show when={mode() === "settings"}>
        <SettingsForm
          secret={props.secret}
          choices={props.choices}
          showOwner={props.secret.scope !== "Personal"}
          busy={busy()}
          onSave={(settings) => run(putSettings(props.secret, settings))}
          onCancel={close}
        />
      </Show>
      <Show when={mode() === "delete"}>
        <div class="flex flex-wrap items-center gap-3 text-xs">
          <span>Delete {props.secret.name}? What refers to it stops working until it is set again.</span>
          <button
            type="button"
            class="rounded-md bg-red-600 px-2 py-1 font-medium text-white disabled:opacity-50"
            disabled={busy()}
            onClick={() => run(deleteValue(props.secret))}
          >
            Delete
          </button>
          <button type="button" class={quiet} onClick={close}>
            Cancel
          </button>
        </div>
      </Show>
      <Show when={error()}>{(message) => <p class="text-xs text-red-600">{message()}</p>}</Show>
    </li>
  );
}

function SecretItems(props: { secrets: Secret[]; empty: string; editable: boolean; machines: Machine[]; choices: Machine[]; onChanged: () => void }) {
  return (
    <Show when={props.secrets.length > 0} fallback={<p class="px-4 py-3 text-sm text-muted">{props.empty}</p>}>
      <ul class="divide-y divide-line">
        <For each={props.secrets} keyed={(secret) => `${secret.scope}/${secret.environment ?? ""}/${secret.name}`}>
          {(secret) => (
            <SecretRow
              secret={secret()}
              editable={props.editable}
              machines={props.machines}
              choices={props.choices}
              onChanged={props.onChanged}
            />
          )}
        </For>
      </ul>
    </Show>
  );
}

function SecretList(props: {
  title: string;
  empty: string;
  secrets: Secret[];
  editable: boolean;
  machines: Machine[];
  choices: Machine[];
  onChanged: () => void;
}) {
  return (
    <section class="rounded-lg border border-line bg-panel">
      <h2 class="border-b border-line px-4 py-3 text-sm font-medium">{props.title}</h2>
      <SecretItems
        secrets={props.secrets}
        empty={props.empty}
        editable={props.editable}
        machines={props.machines}
        choices={props.choices}
        onChanged={props.onChanged}
      />
    </section>
  );
}

/** An environment's rules: who approves its sessions and where they run. With a name field when it is new. */
function RulesForm(props: {
  environment?: Environment;
  users: User[];
  machines: Machine[];
  onSaved: () => void;
  onCancel?: () => void;
}) {
  const [name, setName] = createSignal("");
  const [description, setDescription] = createSignal(props.environment?.description ?? "");
  const [reviewers, setReviewers] = createSignal<Id[]>(props.environment?.reviewers.map((reviewer) => reviewer.id) ?? []);
  const [machines, setMachines] = createSignal<Id[]>(props.environment?.machines.map((machine) => machine.id) ?? []);
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    setBusy(true);
    const { response, error: problem } = await api.PUT("/api/v1/environments/{name}", {
      params: { path: { name: props.environment?.name ?? name().trim() } },
      body: { description: description().trim(), reviewers: reviewers(), machines: machines() },
    });
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    setError(undefined);
    setName("");
    setDescription("");
    setReviewers([]);
    setMachines([]);
    props.onSaved();
  }

  return (
    <form class="space-y-3" onSubmit={save}>
      <Show when={!props.environment}>
        <input
          class={[field, "font-mono"]}
          placeholder="Name, e.g. production"
          required
          pattern="[a-z0-9][a-z0-9_\-]{0,63}"
          title="Lowercase letters, digits, - and _"
          value={name()}
          onInput={(event) => setName(event.currentTarget.value)}
        />
      </Show>
      <input
        class={field}
        placeholder="What it is for (optional)"
        maxlength={500}
        value={description()}
        onInput={(event) => setDescription(event.currentTarget.value)}
      />
      <div class="space-y-1">
        <p class="text-xs text-muted">Reviewers: one of them, never the session's own creator, approves each session before it starts (none: no approval)</p>
        <div class="flex flex-wrap gap-x-4 gap-y-1">
          <For each={props.users} keyed={(user) => String(user.id)}>
            {(user) => (
              <label class="flex items-center gap-1.5 text-sm">
                <input
                  type="checkbox"
                  checked={reviewers().some((id) => same(id, user().id))}
                  onChange={(event) =>
                    setReviewers(
                      event.currentTarget.checked ? [...reviewers(), user().id] : reviewers().filter((id) => !same(id, user().id)),
                    )
                  }
                />
                {user().name}
              </label>
            )}
          </For>
        </div>
      </div>
      <div class="space-y-1">
        <p class="text-xs text-muted">Its sessions run only on these machines (none ticked: any)</p>
        <MachinePicker machines={props.machines} chosen={machines()} showOwner={true} onChange={setMachines} />
      </div>
      <div class="flex items-center gap-3">
        <button type="submit" class={primary} disabled={busy() || (!props.environment && !name().trim())}>
          {props.environment ? "Save rules" : "Create environment"}
        </button>
        <Show when={props.onCancel}>
          <button type="button" class={quiet} onClick={() => props.onCancel?.()}>
            Cancel
          </button>
        </Show>
        <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
      </div>
    </form>
  );
}

function EnvironmentCard(props: { environment: Environment; admin: boolean; users: User[]; machines: Machine[]; onChanged: () => void }) {
  const [mode, setMode] = createSignal<"rules" | "delete">();
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);

  async function remove() {
    setBusy(true);
    const { response, error: problem } = await api.DELETE("/api/v1/environments/{name}", { params: { path: { name: props.environment.name } } });
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    setMode(undefined);
    props.onChanged();
  }

  return (
    <section class="rounded-lg border border-line bg-panel">
      <div class="space-y-1 border-b border-line px-4 py-3">
        <div class="flex items-start justify-between gap-3">
          <h2 class="text-sm font-medium">
            Environment <span class="font-mono">{props.environment.name}</span>
          </h2>
          <Show when={props.admin && !mode()}>
            <span class="flex shrink-0 items-center gap-3 text-xs">
              <button type="button" class="text-accent hover:underline" onClick={() => setMode("rules")}>
                Rules
              </button>
              <button type="button" class="text-muted hover:text-red-600" onClick={() => setMode("delete")}>
                Delete
              </button>
            </span>
          </Show>
        </div>
        <Show when={props.environment.description}>
          <p class="text-sm">{props.environment.description}</p>
        </Show>
        <p class="text-xs text-muted">
          {props.environment.reviewers.length
            ? `Each session waits for ${props.environment.reviewers.map((reviewer) => reviewer.name).join(" or ")} to approve it.`
            : "Sessions start without approval."}{" "}
          {props.environment.machines.length
            ? `Only on ${props.environment.machines.map((machine) => machine.name).join(", ")}.`
            : "On any machine."}
        </p>
        <Show when={mode() === "rules"}>
          <div class="pt-2">
            <RulesForm
              environment={props.environment}
              users={props.users}
              machines={props.machines}
              onSaved={() => {
                setMode(undefined);
                props.onChanged();
              }}
              onCancel={() => setMode(undefined)}
            />
          </div>
        </Show>
        <Show when={mode() === "delete"}>
          <div class="flex flex-wrap items-center gap-3 pt-2 text-xs">
            <span>Delete {props.environment.name} and its values?</span>
            <button
              type="button"
              class="rounded-md bg-red-600 px-2 py-1 font-medium text-white disabled:opacity-50"
              disabled={busy()}
              onClick={remove}
            >
              Delete
            </button>
            <button
              type="button"
              class={quiet}
              onClick={() => {
                setMode(undefined);
                setError(undefined);
              }}
            >
              Cancel
            </button>
          </div>
        </Show>
        <Show when={error()}>{(message) => <p class="text-xs text-red-600">{message()}</p>}</Show>
      </div>
      <SecretItems
        secrets={props.environment.values}
        empty={props.admin ? "No values yet: add them below." : "No values yet."}
        editable={props.admin}
        machines={props.machines}
        choices={props.machines}
        onChanged={props.onChanged}
      />
    </section>
  );
}

/** Sessions of others that wait for this person to approve their environment. */
function Approvals(props: { approvals: EnvironmentApproval[]; onAnswered: () => void }) {
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);

  async function answer(approval: EnvironmentApproval, approve: boolean) {
    setBusy(true);
    const { response, error: problem } = await api.POST("/api/v1/environment-approvals/{sessionId}", {
      params: { path: { sessionId: approval.sessionId } },
      body: { approve },
    });
    setBusy(false);
    setError(response.ok ? undefined : problemOf(problem, response.status));
    props.onAnswered();
  }

  return (
    <section id="approvals" class="rounded-lg border border-amber-500/40 bg-panel">
      <h2 class="border-b border-line px-4 py-3 text-sm font-medium">Waiting for your approval</h2>
      <ul class="divide-y divide-line">
        <For each={props.approvals} keyed={(approval) => String(approval.sessionId)}>
          {(approval) => (
            <li class="flex flex-wrap items-center justify-between gap-3 px-4 py-2 text-sm">
              <div class="min-w-0">
                <p>
                  <span class="font-medium">{approval().requestedBy}</span> wants to start a session in{" "}
                  <span class="font-mono">{approval().environment}</span>
                </p>
                <p class="break-all text-xs text-muted">
                  {approval().harness}
                  {approval().profile ? ` with the profile ${approval().profile}` : ""} on {approval().machine}
                  {approval().workingDirectory ? ` in ${approval().workingDirectory}` : ""} · {new Date(approval().requestedAt).toLocaleString()}
                </p>
              </div>
              <span class="flex shrink-0 items-center gap-3">
                <button type="button" class={primary} disabled={busy()} onClick={() => answer(approval(), true)}>
                  Approve
                </button>
                <button type="button" class="text-sm text-muted hover:text-red-600" disabled={busy()} onClick={() => answer(approval(), false)}>
                  Reject
                </button>
              </span>
            </li>
          )}
        </For>
      </ul>
      <Show when={error()}>{(message) => <p class="px-4 pb-3 text-xs text-red-600">{message()}</p>}</Show>
    </section>
  );
}

/** My secrets and variables, the shared ones and the environments: configuration names them instead of holding their values. */
export default function Secrets() {
  const navigate = useNavigate();
  const workspace = useWorkspace();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent("/secrets")}`, { replace: true });
    },
  );
  const secrets = createMemo(async (): Promise<Secret[]> => (me() ? ((await api.GET("/api/v1/secrets")).data ?? []) : []));
  const environments = createMemo(async (): Promise<Environment[]> => (me() ? ((await api.GET("/api/v1/environments")).data ?? []) : []));
  const machines = createMemo(async (): Promise<Machine[]> => (me() ? ((await api.GET("/api/v1/machines")).data ?? []) : []));
  const users = createMemo(async (): Promise<User[]> => (me() ? ((await api.GET("/api/v1/users")).data ?? []) : []));
  const mine = createMemo(() => machines().filter((machine) => machine.canControl));
  const personal = createMemo(() => secrets().filter((secret) => secret.scope === "Personal"));
  const shared = createMemo(() => secrets().filter((secret) => secret.scope === "Shared"));
  const [kind, setKind] = createSignal<SecretKind>("Secret");
  // "Personal", "Shared", or "env:<name>"
  const [place, setPlace] = createSignal("Personal");
  const [name, setName] = createSignal("");
  const [value, setValue] = createSignal("");
  const [hostOnly, setHostOnly] = createSignal(false);
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  const [creating, setCreating] = createSignal(false);
  const reload = () => {
    refresh(secrets);
    refresh(environments);
  };
  // What its manager should rotate: their own values, and for admins the shared ones and those of environments.
  const due = createMemo(() => {
    const admin = isAdmin(me());
    return [...personal(), ...(admin ? shared() : []), ...(admin ? environments().flatMap((environment) => environment.values) : [])].filter(
      dueForRotation,
    );
  });

  function placeOf(choice: string): Place {
    return choice.startsWith("env:")
      ? { scope: "Environment", name: name().trim(), environment: choice.slice(4) }
      : { scope: choice as Secret["scope"], name: name().trim() };
  }

  async function add(event: SubmitEvent) {
    event.preventDefault();
    const target = placeOf(place());
    const existing =
      target.scope === "Environment"
        ? (environments().find((environment) => environment.name === target.environment)?.values ?? [])
        : secrets().filter((secret) => secret.scope === target.scope);
    if (existing.some((secret) => secret.name === target.name)) return setError(`${target.name} is already set: rotate or edit it in the list.`);
    setBusy(true);
    const { response, error: problem } = await putValue(target, { kind: kind(), value: value(), hostOnly: kind() === "Secret" && hostOnly() });
    setBusy(false);
    if (!response.ok) return setError(problemOf(problem, response.status));
    setName("");
    setValue("");
    setHostOnly(false);
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
              <code class="font-mono">{"${var:NAME}"}</code>. A secret is encrypted on the server and never shown again. A profile's
              environment comes first, then your own value of a name, then the shared one.
            </p>
          </div>
          <Show when={workspace.approvals().length > 0}>
            <Approvals approvals={workspace.approvals()} onAnswered={() => workspace.refreshApprovals()} />
          </Show>
          <Show when={due().length > 0}>
            <p class="rounded-md bg-amber-500/15 px-3 py-2 text-sm text-amber-800 dark:text-amber-200">
              {due().length === 1 ? `${due()[0].name} is due for rotation.` : `${due().length} values are due for rotation.`} Rotate them in
              the lists below.
            </p>
          </Show>
          <SecretList
            title="Yours"
            empty="Nothing yet."
            secrets={personal()}
            editable={true}
            machines={machines()}
            choices={mine()}
            onChanged={reload}
          />
          <SecretList
            title="Shared with everyone"
            empty={isAdmin(user()) ? "Nothing shared yet." : "Nothing shared yet. Admins set shared values."}
            secrets={shared()}
            editable={isAdmin(user())}
            machines={machines()}
            choices={machines()}
            onChanged={reload}
          />
          <For each={environments()} keyed={(environment) => environment.name}>
            {(environment) => (
              <EnvironmentCard
                environment={environment()}
                admin={isAdmin(user())}
                users={users()}
                machines={machines()}
                onChanged={reload}
              />
            )}
          </For>
          <form class="space-y-3 rounded-lg border border-line bg-panel p-4" onSubmit={add}>
            <h2 class="text-sm font-medium">Add</h2>
            <div class="flex gap-3">
              <select class={field} value={kind()} onChange={(event) => setKind(event.currentTarget.value as SecretKind)}>
                <option value="Secret">Secret</option>
                <option value="Variable">Variable (not secret)</option>
              </select>
              <Show when={isAdmin(user())}>
                <select class={field} value={place()} onChange={(event) => setPlace(event.currentTarget.value)}>
                  <option value="Personal">Yours</option>
                  <option value="Shared">Shared with everyone</option>
                  <For each={environments()} keyed={(environment) => environment.name}>
                    {(environment) => <option value={`env:${environment().name}`}>Environment {environment().name}</option>}
                  </For>
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
            <Show when={kind() === "Secret"}>
              <label class="flex items-start gap-2 text-sm">
                <input type="checkbox" class="mt-1" checked={hostOnly()} onChange={(event) => setHostOnly(event.currentTarget.checked)} />
                <span>
                  For MCP servers only
                  <span class="block text-xs text-muted">
                    Never in a session's environment, so the agent's commands cannot read it. Best with a remote MCP server: a
                    local one runs as your user, who can read its environment.
                  </span>
                </span>
              </label>
            </Show>
            <p class="text-xs text-muted">Its machines and how often to rotate it are under Settings, once it is added.</p>
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
          <Show when={isAdmin(user())}>
            <section class="space-y-3 rounded-lg border border-line bg-panel p-4">
              <div class="space-y-1">
                <h2 class="text-sm font-medium">Environments</h2>
                <p class="text-xs text-muted">
                  A named set of values with rules of its own, such as production: a profile names it, its values come first, and its
                  sessions run only on its machines and once a reviewer approves them.
                </p>
              </div>
              <Show
                when={creating()}
                fallback={
                  <button type="button" class="text-sm text-accent hover:underline" onClick={() => setCreating(true)}>
                    New environment
                  </button>
                }
              >
                <RulesForm
                  users={users()}
                  machines={machines()}
                  onSaved={() => {
                    setCreating(false);
                    reload();
                  }}
                  onCancel={() => setCreating(false)}
                />
              </Show>
            </section>
          </Show>
        </div>
      )}
    </Show>
  );
}
