import { createMemo, createSignal, Errored, For, Loading, onSettled, Show } from "solid-js";
import { system } from "#neoastra";
import { runProbes, type ProbeResult } from "./probes";

export default function App() {
  // An async memo: the typed NeoRpc call into C# (SystemService.Info), read under <Loading>.
  const info = createMemo(() => system.info({}));
  const [results, setResults] = createSignal<ProbeResult[]>([]);
  const [running, setRunning] = createSignal(false);

  async function run() {
    setRunning(true);
    setResults([]);
    await runProbes((result) => setResults((list) => [...list, result]));
    setRunning(false);
  }

  onSettled(() => {
    void system.report({ name: "rendered", ok: true, detail: `performance.now()=${performance.now().toFixed(0)} ms` });
    void run();
  });

  return (
    <main>
      <h1>BoSoFa desktop spike</h1>
      <p class="muted">SolidJS 2 inside NeoAstra, served from {location.origin}</p>
      <Errored fallback={(error) => <p class="fail">RPC failed: {String(error)}</p>}>
        <Loading fallback={<p class="muted">Asking C#…</p>}>
          <section class="card">
            <h2>system.info (NeoRpc)</h2>
            <dl>
              <dt>Machine</dt>
              <dd>{info().machineName}</dd>
              <dt>OS</dt>
              <dd>
                {info().os} ({info().architecture})
              </dd>
              <dt>Runtime</dt>
              <dd>{info().framework}</dd>
              <dt>Uptime at call</dt>
              <dd>{info().uptimeMs.toFixed(0)} ms</dd>
            </dl>
          </section>
        </Loading>
      </Errored>
      <section class="card">
        <h2>
          Server probes{" "}
          <button type="button" disabled={running()} onClick={() => void run()}>
            Run again
          </button>
        </h2>
        <table>
          <tbody>
            <For each={results()}>
              {(result) => (
                <tr>
                  <td class={result.ok ? "ok" : "fail"}>{result.ok ? "OK" : "FAIL"}</td>
                  <td>{result.name}</td>
                  <td class="detail">{result.detail}</td>
                </tr>
              )}
            </For>
          </tbody>
        </table>
        <Show when={running()}>
          <p class="muted">Running…</p>
        </Show>
      </section>
    </main>
  );
}
