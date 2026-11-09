import { api, type DaemonRollout, type Release } from "@bosofa/api";
import { createMemo, createSignal, For, onSettled, refresh, Show } from "solid-js";

/** Daemon releases and rolling them out, a few machines at a time; desktop apps fetch new releases themselves. */
export default function Releases() {
  const releases = createMemo(async (): Promise<Release[]> => (await api.GET("/api/v1/daemon-releases")).data ?? []);
  const desktopReleases = createMemo(async (): Promise<Release[]> => (await api.GET("/api/v1/desktop-releases")).data ?? []);
  const rollouts = createMemo(async (): Promise<DaemonRollout[]> => (await api.GET("/api/v1/daemon-rollouts")).data ?? []);
  const [waveSize, setWaveSize] = createSignal("1");
  const [error, setError] = createSignal<string>();
  onSettled(() => {
    const id = setInterval(() => refresh(rollouts), 5000);
    return () => clearInterval(id);
  });

  async function roll(version: string) {
    setError(undefined);
    const { response, error: problem } = await api.POST("/api/v1/daemon-rollouts", {
      body: { version, waveSize: Number(waveSize()), failAfterSeconds: null },
    });
    if (!response.ok) return setError(typeof problem === "string" ? problem : `That did not work (${response.status}).`);
    refresh(rollouts);
  }

  async function halt(rollout: DaemonRollout) {
    await api.POST("/api/v1/daemon-rollouts/{id}/halt", { params: { path: { id: Number(rollout.id) } } });
    refresh(rollouts);
  }

  return (
    <div class="space-y-4 mx-auto max-w-3xl">
      <section class="rounded-lg border border-line bg-panel">
        <div class="px-4 py-3 border-b border-line flex items-center justify-between gap-3">
          <h2 class="text-sm font-medium">Daemon releases</h2>
          <label class="text-xs text-muted flex items-center gap-2">
            Machines at a time
            <input
              class="w-16 rounded-md border border-line bg-transparent px-2 py-0.5"
              type="number"
              min="1"
              value={waveSize()}
              onInput={(event) => setWaveSize(event.currentTarget.value)}
            />
          </label>
        </div>
        <Show when={error()}>
          <p class="px-4 py-2 border-b border-line text-sm text-red-600">{error()}</p>
        </Show>
        <ul class="divide-y divide-line text-sm">
          <For each={releases()} keyed={(release) => release.version}>
            {(release) => (
              <li class="px-4 py-2 flex items-center justify-between gap-3">
                <span class="truncate">
                  {release().version}
                  <span class="text-muted">
                    {" "}
                    · {release().channel} · {release().rids.join(", ")}
                  </span>
                </span>
                <button class="text-xs text-accent hover:underline shrink-0" onClick={() => roll(release().version)}>
                  Roll out
                </button>
              </li>
            )}
          </For>
          <Show when={releases().length === 0}>
            <li class="px-4 py-2 text-muted">
              None yet. Publish one with <code class="font-mono text-xs">bosofad release-publish</code>.
            </li>
          </Show>
        </ul>
      </section>
      <section class="rounded-lg border border-line bg-panel">
        <h2 class="px-4 py-3 border-b border-line text-sm font-medium">Desktop releases</h2>
        <ul class="divide-y divide-line text-sm">
          <For each={desktopReleases()} keyed={(release) => release.version}>
            {(release) => (
              <li class="px-4 py-2 truncate">
                {release().version}
                <span class="text-muted">
                  {" "}
                  · {release().channel} · {release().rids.join(", ")}
                </span>
              </li>
            )}
          </For>
          <Show when={desktopReleases().length === 0}>
            <li class="px-4 py-2 text-muted">
              None yet. Publish one with <code class="font-mono text-xs">bosofad release-publish --product desktop</code>; apps
              pick up the newest stable one by themselves.
            </li>
          </Show>
        </ul>
      </section>
      <section class="rounded-lg border border-line bg-panel">
        <h2 class="px-4 py-3 border-b border-line text-sm font-medium">Rollouts</h2>
        <ul class="divide-y divide-line text-sm">
          <For each={rollouts()} keyed={(rollout) => rollout.id}>
            {(rollout) => (
              <li class="px-4 py-2 flex items-center justify-between gap-3">
                <span class="truncate">
                  {rollout().version}
                  <span class="text-muted">
                    {" "}
                    · {rollout().status} · {rollout().updated} updated, {rollout().inFlight} updating
                    {rollout().detail ? ` · ${rollout().detail}` : ""}
                  </span>
                </span>
                <Show when={rollout().status === "Running"}>
                  <button class="text-xs text-accent hover:underline shrink-0" onClick={() => halt(rollout())}>
                    Halt
                  </button>
                </Show>
              </li>
            )}
          </For>
          <Show when={rollouts().length === 0}>
            <li class="px-4 py-2 text-muted">None yet.</li>
          </Show>
        </ul>
      </section>
    </div>
  );
}
