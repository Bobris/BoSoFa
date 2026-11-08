import { getPlatform } from "@bosofa/api";
import { createMemo, createSignal, refresh, Show } from "solid-js";

/** Desktop only: makes this computer one of the signed-in person's machines. */
export default function ThisMachine() {
  const local = getPlatform().localDaemon!;
  const status = createMemo(() => local.status());
  const [busy, setBusy] = createSignal(false);
  const [problem, setProblem] = createSignal<string>();

  async function run(action: () => Promise<string | undefined>) {
    setBusy(true);
    setProblem(undefined);
    setProblem(await action());
    setBusy(false);
    refresh(status);
  }

  return (
    <section class="rounded-lg border border-line bg-panel px-4 py-3 text-sm space-y-2">
      <div class="flex items-center justify-between gap-3">
        <span>
          <span class="font-medium">This computer</span>
          <span class="text-muted">
            {" "}
            · {status().installed ? "runs sessions for you" : status().enrolled ? "enrolled, not running" : "not enabled"}
          </span>
        </span>
        <Show
          when={status().installed}
          fallback={
            <button class="rounded-md bg-accent px-3 py-1 font-medium text-white" disabled={busy()} onClick={() => run(local.enable)}>
              {busy() ? "Enabling…" : "Enable this machine"}
            </button>
          }
        >
          <button class="text-xs text-accent hover:underline" disabled={busy()} onClick={() => run(local.disable)}>
            Disable
          </button>
        </Show>
      </div>
      <Show when={problem()}>
        <p class="text-red-600">{problem()}</p>
      </Show>
    </section>
  );
}
