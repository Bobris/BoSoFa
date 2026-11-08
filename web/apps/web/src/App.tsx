import { Errored, Loading } from "solid-js";
import { paths, Router } from "./router";

export default function App() {
  return (
    <Router>
      {(props) => (
        <div class="min-h-screen flex flex-col">
          <header class="border-b border-line bg-panel px-4 h-12 flex items-center gap-3">
            <a href={paths()} class="font-semibold tracking-tight">
              BoSoFa
            </a>
            <span class="text-muted text-sm">Bobris Software Factory</span>
          </header>
          <Errored
            fallback={(error, reset) => (
              <main class="p-6">
                <p class="text-red-600">{String(error)}</p>
                <button class="mt-3 underline" onClick={reset}>
                  Try again
                </button>
              </main>
            )}
          >
            <Loading fallback={<main class="p-6 text-muted">Loading…</main>}>
              <main class="flex-1 p-4 sm:p-6">{props.children}</main>
            </Loading>
          </Errored>
        </div>
      )}
    </Router>
  );
}
