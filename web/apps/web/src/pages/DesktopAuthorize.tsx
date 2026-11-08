import { api } from "@bosofa/api";
import { useNavigate, useSearchParams } from "@solidjs/router";
import { createEffect, createMemo, createSignal, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";

/** The system browser's half of the desktop sign-in: the person approves, and the code goes back to the app on this computer. */
export default function DesktopAuthorize() {
  const navigate = useNavigate();
  const [search] = useSearchParams<{ redirect_uri: string; code_challenge: string; state: string }>();
  const me = createMemo(() => fetchMe());
  const [error, setError] = createSignal<string>();
  const [done, setDone] = createSignal(false);
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent(location.pathname + location.search)}`, { replace: true });
    },
  );

  async function approve() {
    const { data, error: problem } = await api.POST("/auth/desktop/approve", {
      body: { redirectUri: search.redirect_uri ?? "", codeChallenge: search.code_challenge ?? "", state: search.state ?? "" },
    });
    if (!data) return setError(typeof problem === "string" ? problem : "This sign-in cannot be approved.");
    setDone(true);
    location.href = data.redirect;
  }

  return (
    <Show when={me()}>
      {(user) => (
        <section class="mx-auto max-w-md rounded-lg border border-line bg-panel p-6 space-y-4 text-sm">
          <h1 class="text-lg font-medium">Sign in to BoSoFa Desktop</h1>
          <Show
            when={!done()}
            fallback={<p class="text-muted">Done. You can close this tab and go back to the app.</p>}
          >
            <p>
              Let the BoSoFa Desktop app on this computer act as <strong>{user().name}</strong>?
            </p>
            <p class="text-muted">Only continue if you just asked the app to sign in.</p>
            <button class="rounded-md bg-accent px-3 py-1.5 font-medium text-white" onClick={approve}>
              Continue
            </button>
          </Show>
          <Show when={error()}>
            <p class="text-red-600">{error()}</p>
          </Show>
        </section>
      )}
    </Show>
  );
}
