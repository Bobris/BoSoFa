import { api, getPlatform, type Machine } from "@bosofa/api";
import { createMemo, For, onSettled, refresh, Show } from "solid-js";
import ThisMachine from "../components/ThisMachine";
import { paths } from "../router";
import { shortVersion } from "../version";
import { branding } from "../branding";

/** Machines of the whole team, refreshed every few seconds until the live hub (M1) pushes presence. */
export default function Machines() {
  const machines = createMemo(async (): Promise<Machine[]> => (await api.GET("/api/v1/machines")).data ?? []);
  onSettled(() => {
    const id = setInterval(() => refresh(machines), 5000);
    return () => clearInterval(id);
  });

  return (
    <div class="space-y-4">
    <Show when={getPlatform().localDaemon}>
      <ThisMachine />
    </Show>
    <section class="rounded-lg border border-line bg-panel">
      <h2 class="px-4 py-3 border-b border-line text-sm font-medium">Machines</h2>
      <Show
        when={machines().length > 0}
        fallback={
          <p class="px-4 py-3 text-sm text-muted">
            No machines yet.{" "}
            <a class="text-accent hover:underline" href={paths.install()}>
              Install the daemon
            </a>{" "}
            on one.
          </p>
        }
      >
        <ul class="divide-y divide-line">
          <For each={machines()} keyed={(machine) => machine.id}>
            {(machine) => (
              <li class="px-4 py-2 flex items-center justify-between gap-3 text-sm">
                <span class="flex items-center gap-2">
                  <span
                    class={["inline-block size-2 rounded-full", machine().online ? "bg-green-500" : "bg-line"]}
                    title={machine().online ? "Online" : "Offline"}
                  />
                  <Show when={machine().canControl} fallback={machine().name}>
                    <a class="text-accent hover:underline" href={paths.machines(Number(machine().id))()}>
                      {machine().name}
                    </a>
                  </Show>
                  <span class="text-muted">
                    {machine().os} {machine().arch}
                  </span>
                </span>
                <span class="text-xs text-muted">
                  <For each={machine().harnesses} keyed={(harness) => harness.harness}>
                    {(harness) => (
                      <span class={["mr-2", { "text-red-600": !!harness().error }]} title={harness().error ?? undefined}>
                        {harness().harness} {harness().version}
                      </span>
                    )}
                  </For>
                  {machine().ownerName} · {branding().daemon} {shortVersion(machine().daemonVersion)}
                </span>
              </li>
            )}
          </For>
        </ul>
      </Show>
    </section>
    </div>
  );
}
