import { api, type AuditEntry } from "@bosofa/api";
import { createMemo, createSignal, For, Show } from "solid-js";

/** Who did what, newest first. */
export default function Audit() {
  const [older, setOlder] = createSignal<AuditEntry[]>([]);
  const [finished, setFinished] = createSignal(false);
  const first = createMemo(async () => {
    const { data, response } = await api.GET("/api/v1/audit", {});
    return { entries: data ?? [], forbidden: response.status === 403 };
  });
  const entries = () => [...first().entries, ...older()];

  async function more() {
    const last = entries().at(-1);
    if (!last) return;
    const { data } = await api.GET("/api/v1/audit", { params: { query: { before: Number(last.id) } } });
    if (!data || data.length === 0) return setFinished(true);
    setOlder((current) => [...current, ...data]);
  }

  return (
    <section class="rounded-lg border border-line bg-panel mx-auto max-w-4xl">
      <h2 class="px-4 py-3 border-b border-line text-sm font-medium">Audit log</h2>
      <Show when={!first().forbidden} fallback={<p class="px-4 py-3 text-sm text-muted">Only admins can see the audit log.</p>}>
        <ul class="divide-y divide-line text-sm">
          <For each={entries()} keyed={(entry) => entry.id}>
            {(entry) => (
              <li class="px-4 py-2">
                <span class="text-muted">{new Date(entry().at).toLocaleString()}</span> {entry().actorName || "system"}{" "}
                <code class="font-mono text-xs">{entry().action}</code> {entry().target}
                <Show when={entry().detail}>
                  <span class="block text-xs text-muted break-all">{entry().detail}</span>
                </Show>
              </li>
            )}
          </For>
          <Show when={entries().length === 0}>
            <li class="px-4 py-2 text-muted">Nothing yet.</li>
          </Show>
        </ul>
        <Show when={entries().length > 0 && !finished()}>
          <button class="px-4 py-2 text-xs text-accent hover:underline" onClick={more}>
            Older
          </button>
        </Show>
      </Show>
    </section>
  );
}
