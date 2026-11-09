import { getPlatform } from "@bosofa/api";
import { createMemo, createSignal, onSettled, refresh, Show } from "solid-js";

/** Desktop only: offers a staged update of this app. */
export default function UpdateBanner() {
  const updates = getPlatform().updates!;
  const state = createMemo(() => updates.check());
  const [busy, setBusy] = createSignal(false);
  const [problem, setProblem] = createSignal<string>();
  onSettled(() => {
    const id = setInterval(() => refresh(state), 30 * 60_000);
    return () => clearInterval(id);
  });

  return (
    <Show when={state().ready}>
      <span class="ml-auto flex items-center gap-2 text-sm">
        <span class="text-muted">{problem() ?? `Version ${state().available} is ready`}</span>
        <button
          class="rounded-md bg-accent px-3 py-0.5 font-medium text-white"
          disabled={busy()}
          onClick={async () => {
            setBusy(true);
            setProblem(await updates.install());
            setBusy(false);
          }}
        >
          {busy() ? "Restarting…" : "Restart to update"}
        </button>
      </span>
    </Show>
  );
}
