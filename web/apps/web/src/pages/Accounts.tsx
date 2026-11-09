import { api, getPlatform, type AccountKind, type AccountUsage, type HarnessAccount } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, refresh, Show } from "solid-js";
import Command from "../components/Command";
import Limit from "../components/Limit";
import { paths } from "../router";
import { fetchMe } from "../session";

const kinds: { value: AccountKind; label: string; help: string }[] = [
  {
    value: "ClaudeOAuthToken",
    label: "Claude subscription token",
    help: "Run `claude setup-token` on any computer, sign in, and paste the token it prints (valid for one year).",
  },
  {
    value: "AnthropicApiKey",
    label: "Anthropic API key",
    help: "For Claude Code and the server agent, billed to the key's Console organization. Max, Ultra and Team plans include monthly API credits: link the organization under Settings > Billing on claude.ai.",
  },
  { value: "OpenAIApiKey", label: "OpenAI API key", help: "For Codex and the server agent, billed to the key's OpenAI organization." },
  {
    value: "OpenCodeGoApiKey",
    label: "OpenCode Go API key",
    help: "From the OpenCode Console. Only for your own server agent sessions: a Go key belongs to one person.",
  },
];

function expiresSoon(account: HarnessAccount): boolean {
  return account.expiresAt != null && Date.parse(account.expiresAt) - Date.now() < 30 * 86_400_000;
}

/** Limits as of the account's latest turn: nothing reports them between turns, so the age is part of the answer. */
function Limits(props: { usage: AccountUsage }) {
  return (
    <div class="mt-2 text-xs">
      <div class="flex gap-4">
        <Show when={props.usage.fiveHour}>{(window) => <Limit name="5 hours" window={window()} />}</Show>
        <Show when={props.usage.sevenDay}>{(window) => <Limit name="Week" window={window()} />}</Show>
      </div>
      <p class="mt-1 text-muted">As of {new Date(props.usage.updatedAt).toLocaleString()}</p>
    </div>
  );
}

/** My harness accounts: secrets go to the server once and never come back; daemons lease them per session. */
export default function Accounts() {
  const navigate = useNavigate();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent("/accounts")}`, { replace: true });
    },
  );
  const accounts = createMemo(async (): Promise<HarnessAccount[]> =>
    me() ? ((await api.GET("/api/v1/accounts")).data ?? []) : [],
  );
  const [kind, setKind] = createSignal<AccountKind>("ClaudeOAuthToken");
  const [label, setLabel] = createSignal("");
  const [secret, setSecret] = createSignal("");
  const [error, setError] = createSignal<string>();
  const [busy, setBusy] = createSignal(false);
  let secretInput: HTMLInputElement | undefined;

  async function add(event: SubmitEvent) {
    event.preventDefault();
    setBusy(true);
    const { response, error: problem } = await api.POST("/api/v1/accounts", {
      body: { kind: kind(), label: label().trim(), secret: secret().trim() },
    });
    setBusy(false);
    if (!response.ok) {
      setError(typeof problem === "string" ? problem : `Adding the account failed (${response.status}).`);
      return;
    }
    setSecret("");
    setLabel("");
    setError(undefined);
    refresh(accounts);
  }

  async function remove(account: HarnessAccount) {
    await api.DELETE("/api/v1/accounts/{id}", { params: { path: { id: Number(account.id) } } });
    refresh(accounts);
  }

  const field = "w-full rounded-md border border-line bg-panel px-3 py-2 text-sm";

  return (
    <Show when={me()}>
      {(user) => (
      <div class="mx-auto max-w-2xl space-y-6">
        <h1 class="text-xl font-semibold">Harness accounts</h1>
        <section class="rounded-lg border border-line bg-panel">
          <Show
            when={accounts().length > 0}
            fallback={
              <p class="px-4 py-3 text-sm text-muted">
                No accounts yet. Sessions then use the sign-in of the machine they run on.
              </p>
            }
          >
            <ul class="divide-y divide-line">
              <For each={accounts()} keyed={(account) => account.id}>
                {(account) => (
                  <li class="px-4 py-2 text-sm">
                    <div class="flex items-center justify-between gap-3">
                      <span>
                        {account().label}{" "}
                        <span class="text-muted">
                          {kinds.find((k) => k.value === account().kind)?.label} · {account().secretHint}
                        </span>
                      </span>
                      <span class="flex items-center gap-3 text-xs">
                        <Show when={account().expiresAt}>
                          {(expires) => (
                            <span class={expiresSoon(account()) ? "text-red-600" : "text-muted"}>
                              expires {new Date(expires()).toLocaleDateString()}
                            </span>
                          )}
                        </Show>
                        <button class="text-muted hover:text-red-600" onClick={() => remove(account())}>
                          Remove
                        </button>
                      </span>
                    </div>
                    <Show when={account().usage}>{(usage) => <Limits usage={usage()} />}</Show>
                  </li>
                )}
              </For>
            </ul>
          </Show>
        </section>
        <Show when={getPlatform().localDaemon}>
          {(local) => (
            <section class="space-y-2 rounded-lg border border-line bg-panel p-4 text-sm">
              <h2 class="font-medium">Add your Claude subscription</h2>
              <p class="text-xs text-muted">Signs in with Claude Code in your browser. The token goes to the server and is never shown.</p>
              <button
                class="rounded-md bg-accent px-3 py-1.5 font-medium text-white"
                disabled={busy()}
                onClick={async () => {
                  setBusy(true);
                  setError(await local().addClaudeAccount(`Claude (${user().name})`));
                  setBusy(false);
                  refresh(accounts);
                }}
              >
                {busy() ? "Waiting for the browser…" : "Sign in to Claude"}
              </button>
            </section>
          )}
        </Show>
        <Show when={!getPlatform().localDaemon}>
          <section class="space-y-3 rounded-lg border border-line bg-panel p-4 text-sm">
            <h2 class="font-medium">Add your Claude subscription</h2>
            <ol class="list-decimal space-y-3 pl-5 text-xs text-muted">
              <li class="space-y-1">
                <p>On any computer with Claude Code installed, run this in a terminal:</p>
                <Command text="claude setup-token" />
              </li>
              <li>Sign in to Claude in the browser it opens. The terminal then prints a token that starts with sk-ant-oat01-.</li>
              <li>
                Copy the whole token and{" "}
                <button
                  class="text-accent hover:underline"
                  onClick={() => {
                    setKind("ClaudeOAuthToken");
                    if (!label().trim()) setLabel(`Claude (${user().name})`);
                    secretInput?.focus();
                  }}
                >
                  paste it below
                </button>
                . It is valid for a year and is shown only once, so you need not keep it.
              </li>
            </ol>
          </section>
        </Show>
        <form class="space-y-3 rounded-lg border border-line bg-panel p-4" onSubmit={add}>
          <h2 class="text-sm font-medium">Add an account</h2>
          <select class={field} value={kind()} onChange={(event) => setKind(event.currentTarget.value as AccountKind)}>
            <For each={kinds}>{(option) => <option value={option.value}>{option.label}</option>}</For>
          </select>
          <p class="text-xs text-muted">{kinds.find((k) => k.value === kind())?.help}</p>
          <input
            class={field}
            placeholder="Label, e.g. My Max plan"
            required
            value={label()}
            onInput={(event) => setLabel(event.currentTarget.value)}
          />
          <input
            ref={secretInput}
            class={[field, "font-mono"]}
            type="password"
            autocomplete="off"
            placeholder="Token or key"
            required
            value={secret()}
            onInput={(event) => setSecret(event.currentTarget.value)}
          />
          <div class="flex items-center gap-3">
            <button
              type="submit"
              class="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
              disabled={busy() || !label().trim() || !secret().trim()}
            >
              Add
            </button>
            <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
          </div>
          <p class="text-xs text-muted">
            The secret is encrypted on the server and only handed to your own machines for your own sessions.
          </p>
        </form>
      </div>
      )}
    </Show>
  );
}
