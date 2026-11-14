import { api, type SkillShareMcp, type SkillShareStatus } from "@bosofa/api";
import { createMemo, createSignal, For, refresh, Show } from "solid-js";

/**
 * The person's side of SkillShare (docs/skillshare.md §5.1): whether their sign-in still works here, and which of their SkillShare
 * MCP servers their sessions get by default. Hidden when the server does not sign in through SkillShare.
 */
export default function SkillShareSection() {
  const status = createMemo(async (): Promise<SkillShareStatus | null> => (await api.GET("/api/v1/skillshare")).data ?? null);
  const servers = createMemo(async (): Promise<{ list: SkillShareMcp[]; problem?: string }> => {
    if (status()?.grant !== "Active") return { list: [] };
    const { data, error } = await api.GET("/api/v1/skillshare/mcps", { params: { query: { fresh: true } } });
    return data ? { list: data } : { list: [], problem: typeof error === "string" ? error : "SkillShare could not be reached." };
  });
  const [problem, setProblem] = createSignal<string>();
  const name = () => status()?.name ?? "SkillShare";

  async function toggle(server: SkillShareMcp, on: boolean) {
    const off = servers()
      .list.filter((other) => other.connected && (other.id === server.id ? !on : !other.on))
      .map((other) => other.id);
    const { response } = await api.PUT("/api/v1/skillshare/mcp-choice", { body: { off } });
    setProblem(response.ok ? undefined : `Saving failed (${response.status}).`);
    refresh(servers);
  }

  return (
    <Show when={status()?.enabled}>
      <section class="space-y-3 rounded-lg border border-line bg-panel p-4">
        <h2 class="text-sm font-medium">{name()}</h2>
        <Show
          when={status()?.grant === "Active"}
          fallback={
            <p class="text-sm text-muted">
              {status()?.grant === "Expired"
                ? `${name()} no longer accepts your sign-in here, so your sessions get none of its MCP servers. `
                : `Sign in through ${name()} once to use its MCP servers in your sessions. `}
              <a class="text-accent hover:underline" rel="external" href={`/auth/login?returnUrl=${encodeURIComponent("/accounts")}`}>
                Sign in again
              </a>
            </p>
          }
        >
          <p class="text-sm text-muted">
            Your sessions get the MCP servers you connected in {name()}, with your own access there. Turn off those you do not want by
            default; a new session can still change it.
          </p>
          <Show when={servers().problem}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
          <ul class="divide-y divide-line text-sm">
            <For each={servers().list} keyed={(server) => server.id} fallback={<li class="py-2 text-muted">You have no MCP servers there yet.</li>}>
              {(server) => (
                <li class="flex items-center gap-3 py-2">
                  <span class="min-w-0 flex-1">
                    <span class="block">{server().title}</span>
                    <span class="block text-xs text-muted">
                      {server().connected ? (server().accessMode ?? "Connected") : `Not connected in ${name()}`}
                    </span>
                  </span>
                  <Show
                    when={server().connected}
                    fallback={
                      <Show when={server().setupUrl}>
                        {(url) => (
                          <a class="text-xs text-accent hover:underline" href={url()} target="_blank" rel="noreferrer">
                            Connect
                          </a>
                        )}
                      </Show>
                    }
                  >
                    <label class="flex items-center gap-2 text-xs text-muted">
                      <input type="checkbox" checked={server().on} onChange={(event) => void toggle(server(), event.currentTarget.checked)} />
                      In my sessions
                    </label>
                  </Show>
                </li>
              )}
            </For>
          </ul>
          <Show when={problem()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
        </Show>
      </section>
    </Show>
  );
}
