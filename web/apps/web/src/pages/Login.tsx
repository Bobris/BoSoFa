import { api, getPlatform } from "@bosofa/api";
import { useNavigate, useSearchParams } from "@solidjs/router";
import { createMemo, createSignal, Show } from "solid-js";
import { branding } from "../branding";
import { paths } from "../router";

/** The desktop app signs in through the system browser; afterwards the page reloads, because its CSP names the server.
 * While the browser is out, the address can still be corrected: continuing again replaces the sign-in that waits. */
function DesktopLogin() {
  const platform = getPlatform();
  const [server, setServer] = createSignal(platform.server ?? "");
  const [error, setError] = createSignal<string>();
  const [waitingFor, setWaitingFor] = createSignal<string>();
  let attempt = 0;

  async function signIn(event: SubmitEvent) {
    event.preventDefault();
    const address = server().trim();
    const mine = ++attempt;
    setError(undefined);
    setWaitingFor(address);
    const problem = await platform.signIn!(address);
    if (mine !== attempt) return; // a newer sign-in took over
    setWaitingFor(undefined);
    if (problem) setError(problem);
    else location.href = "/";
  }

  return (
    <form class="mx-auto mt-[18vh] max-w-sm space-y-4 rounded-2xl border border-line bg-panel p-6 shadow-sm" onSubmit={signIn}>
      <div>
        <img src="/icon.svg" alt="" class="mb-4 size-12 rounded-xl shadow-sm" />
        <h1 class="font-serif text-2xl tracking-tight">Sign in to {branding().name}</h1>
        <p class="mt-1 text-sm text-muted">The address of your team's server. You approve the sign-in in your browser.</p>
      </div>
      <input
        class="w-full rounded-lg border border-line bg-transparent px-3 py-2 outline-none focus:border-accent/50"
        placeholder="bosofa.example.com"
        required
        autofocus
        value={server()}
        onInput={(event) => setServer(event.currentTarget.value)}
      />
      <button type="submit" class="w-full rounded-lg bg-accent py-2 font-medium text-white disabled:opacity-50" disabled={!server().trim()}>
        {waitingFor() && waitingFor() === server().trim() ? "Open the browser again" : "Continue in the browser"}
      </button>
      <Show when={waitingFor()}>
        {(address) => (
          <p class="text-sm text-muted">
            Waiting for you to approve in the browser at <span class="font-medium text-ink">{address()}</span>. Wrong address? Change it and continue again.
          </p>
        )}
      </Show>
      <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
    </form>
  );
}

function WebLogin() {
  const navigate = useNavigate();
  const [search] = useSearchParams<{ returnUrl: string }>();
  // Only local paths, never another origin.
  const returnUrl = () => (search.returnUrl?.startsWith("/") && !search.returnUrl.startsWith("//") ? search.returnUrl : "/");
  const providers = createMemo(async () => (await api.GET("/auth/providers")).data ?? { oidc: null, devLogin: false });
  const [error, setError] = createSignal<string>();

  async function devLogin(event: SubmitEvent) {
    event.preventDefault();
    const form = new FormData(event.currentTarget as HTMLFormElement);
    const { response } = await api.POST("/auth/dev-login", {
      body: { email: String(form.get("email") ?? ""), name: String(form.get("name") ?? "") },
    });
    if (response.ok) navigate(returnUrl(), { replace: true });
    else setError(`Sign-in failed (${response.status}).`);
  }

  return (
    <div class="mx-auto mt-[18vh] max-w-sm space-y-5 rounded-2xl border border-line bg-panel p-6 shadow-sm">
      <div>
        <img src="/icon.svg" alt="" class="mb-4 size-12 rounded-xl shadow-sm" />
        <h1 class="font-serif text-2xl tracking-tight">Sign in to {branding().name}</h1>
      </div>
      <Show when={providers().oidc}>
        {(name) => (
          <a
            class="block w-full text-center rounded-md bg-accent text-white py-2 font-medium"
            href={`/auth/login?returnUrl=${encodeURIComponent(returnUrl())}`}
          >
            Continue with {name()}
          </a>
        )}
      </Show>
      <Show when={providers().devLogin}>
        <form class="space-y-3" onSubmit={devLogin}>
          <p class="text-xs text-muted">Development sign-in: no password, any email.</p>
          <input name="name" placeholder="Name" class="w-full rounded-md border border-line bg-transparent px-3 py-2" />
          <input
            name="email"
            type="email"
            required
            placeholder="Email"
            class="w-full rounded-md border border-line bg-transparent px-3 py-2"
          />
          <button type="submit" class="w-full rounded-md bg-ink text-canvas py-2 font-medium">
            Sign in
          </button>
        </form>
      </Show>
      <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
    </div>
  );
}

export default function Login() {
  return getPlatform().kind === "desktop" ? <DesktopLogin /> : <WebLogin />;
}
