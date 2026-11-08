import { api } from "@bosofa/api";
import { useNavigate, useSearchParams } from "@solidjs/router";
import { createMemo, createSignal, Show } from "solid-js";
import { paths } from "../router";

export default function Login() {
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
    <div class="max-w-sm mx-auto mt-16 rounded-lg border border-line bg-panel p-6 space-y-5">
      <h1 class="text-xl font-semibold">Sign in</h1>
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
