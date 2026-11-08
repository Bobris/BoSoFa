import { api, type ApiToken } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, refresh, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";

/** My API tokens for scripts. A token is shown once; the server keeps only its hash. */
export default function Tokens() {
  const navigate = useNavigate();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent("/tokens")}`, { replace: true });
    },
  );
  const tokens = createMemo(async (): Promise<ApiToken[]> => (me() ? ((await api.GET("/api/v1/tokens")).data ?? []) : []));
  const [name, setName] = createSignal("");
  const [scope, setScope] = createSignal<ApiToken["scope"]>("Read");
  const [days, setDays] = createSignal("90");
  const [created, setCreated] = createSignal<string>();
  const [error, setError] = createSignal<string>();

  async function create(event: SubmitEvent) {
    event.preventDefault();
    setError(undefined);
    const { data, error: problem } = await api.POST("/api/v1/tokens", {
      body: { name: name().trim(), scope: scope(), expiresInDays: days() ? Number(days()) : null },
    });
    if (!data) return setError(typeof problem === "string" ? problem : "The token could not be created.");
    setCreated(data.token);
    setName("");
    refresh(tokens);
  }

  async function revoke(token: ApiToken) {
    if (!confirm(`Revoke ${token.name}? Scripts using it stop working at once.`)) return;
    await api.DELETE("/api/v1/tokens/{id}", { params: { path: { id: Number(token.id) } } });
    refresh(tokens);
  }

  return (
    <div class="space-y-4 max-w-2xl">
      <section class="rounded-lg border border-line bg-panel">
        <h2 class="px-4 py-3 border-b border-line text-sm font-medium">API tokens</h2>
        <ul class="divide-y divide-line text-sm">
          <For each={tokens()} keyed={(token) => token.id}>
            {(token) => (
              <li class="px-4 py-2 flex items-center justify-between gap-3">
                <span>
                  {token().name}
                  <span class="text-muted">
                    {" "}
                    · {token().scope === "Read" ? "read only" : "full control"}
                    {token().expiresAt ? ` · until ${new Date(token().expiresAt!).toLocaleDateString()}` : " · never expires"}
                  </span>
                </span>
                <button class="text-xs text-accent hover:underline" onClick={() => revoke(token())}>
                  Revoke
                </button>
              </li>
            )}
          </For>
          <Show when={tokens().length === 0}>
            <li class="px-4 py-2 text-muted">No tokens yet.</li>
          </Show>
        </ul>
      </section>
      <Show when={created()}>
        <section class="rounded-lg border border-line bg-panel p-4 space-y-2 text-sm">
          <p class="font-medium">Copy this token now. It will not be shown again.</p>
          <code class="block font-mono text-xs break-all">{created()}</code>
        </section>
      </Show>
      <form class="rounded-lg border border-line bg-panel p-4 space-y-3 text-sm" onSubmit={create}>
        <h3 class="font-medium">New token</h3>
        <input
          class="w-full rounded-md border border-line bg-transparent px-2 py-1"
          placeholder="What it is for"
          required
          value={name()}
          onInput={(event) => setName(event.currentTarget.value)}
        />
        <div class="flex gap-3">
          <select
            class="rounded-md border border-line bg-transparent px-2 py-1"
            value={scope()}
            onChange={(event) => setScope(event.currentTarget.value as ApiToken["scope"])}
          >
            <option value="Read">Read only</option>
            <option value="Control">Full control</option>
          </select>
          <input
            class="w-28 rounded-md border border-line bg-transparent px-2 py-1"
            type="number"
            min="1"
            max="3650"
            placeholder="Days"
            value={days()}
            onInput={(event) => setDays(event.currentTarget.value)}
          />
          <button class="rounded-md bg-accent px-3 py-1 font-medium text-white" type="submit">
            Create
          </button>
        </div>
        <Show when={error()}>
          <p class="text-red-600">{error()}</p>
        </Show>
      </form>
    </div>
  );
}
