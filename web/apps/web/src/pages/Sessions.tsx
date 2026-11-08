import { api, type Session } from "@bosofa/api";
import { createMemo, For, onSettled, refresh, Show } from "solid-js";
import { paths } from "../router";
import { StatusBadge } from "../components/StatusBadge";

function ago(iso: string): string {
  const seconds = Math.max(0, (Date.now() - Date.parse(iso)) / 1000);
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  return new Date(iso).toLocaleDateString();
}

/** Recent sessions of the whole team, newest activity first. */
export default function Sessions() {
  const sessions = createMemo(async (): Promise<Session[]> => (await api.GET("/api/v1/sessions")).data ?? []);
  onSettled(() => {
    const id = setInterval(() => refresh(sessions), 5000);
    return () => clearInterval(id);
  });

  return (
    <section class="rounded-lg border border-line bg-panel">
      <div class="flex items-center justify-between border-b border-line px-4 py-2">
        <h2 class="text-sm font-medium">Sessions</h2>
        <a href={paths.sessions.new()} class="rounded-md bg-accent px-3 py-1 text-sm font-medium text-white">
          New session
        </a>
      </div>
      <Show when={sessions().length > 0} fallback={<p class="px-4 py-3 text-sm text-muted">No sessions yet.</p>}>
        <ul class="divide-y divide-line">
          <For each={sessions()} keyed={(session) => session.id}>
            {(session) => (
              <li>
                <a
                  href={paths.sessions(Number(session().id))()}
                  class="flex items-center justify-between gap-3 px-4 py-2 text-sm hover:bg-canvas"
                >
                  <span class="min-w-0 truncate">{session().title}</span>
                  <span class="flex shrink-0 items-center gap-3 text-xs text-muted">
                    {session().machineName} · {session().createdByName} · {ago(session().updatedAt)}
                    <StatusBadge status={session().status} />
                  </span>
                </a>
              </li>
            )}
          </For>
        </ul>
      </Show>
    </section>
  );
}
