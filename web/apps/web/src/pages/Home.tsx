import { api } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createEffect, createMemo, For, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe, logout } from "../session";
import Machines from "./Machines";
import Sessions from "./Sessions";

export default function Home() {
  const navigate = useNavigate();
  const me = createMemo(() => fetchMe());
  // Reads me() before the first await, so the list loads only for a signed-in user and follows sign-in changes.
  const users = createMemo(async () => (me() ? ((await api.GET("/api/v1/users")).data ?? []) : []));
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(paths.login, { replace: true });
    },
  );

  return (
    <Show when={me()}>
      {(user) => (
        <div class="mx-auto max-w-3xl space-y-6">
          <div class="flex items-center justify-between gap-4">
            <h1 class="text-2xl font-semibold">Hello {user().name}</h1>
            <span class="flex items-center gap-4">
            <a href={paths.install()} class="text-sm text-muted hover:text-ink">
              Install
            </a>
            <a href={paths.catalog()} class="text-sm text-muted hover:text-ink">
              Catalog
            </a>
            <Show when={user().role === "Admin"}>
              <a href={paths.releases()} class="text-sm text-muted hover:text-ink">
                Releases
              </a>
              <a href={paths.audit()} class="text-sm text-muted hover:text-ink">
                Audit
              </a>
            </Show>
            <a href={paths.tokens()} class="text-sm text-muted hover:text-ink">
              API tokens
            </a>
            <a href={paths.accounts()} class="text-sm text-muted hover:text-ink">
              Accounts
            </a>
            <button
              class="text-sm text-muted hover:text-ink"
              onClick={async () => {
                await logout();
                navigate(paths.login, { replace: true });
              }}
            >
              Sign out
            </button>
            </span>
          </div>
          <Sessions />
          <Machines />
          <section class="rounded-lg border border-line bg-panel">
            <h2 class="px-4 py-3 border-b border-line text-sm font-medium">People</h2>
            <ul class="divide-y divide-line">
              <For each={users()}>
                {(person) => (
                  <li class="px-4 py-2 flex items-center justify-between gap-3 text-sm">
                    <span>
                      {person.name} <span class="text-muted">{person.email}</span>
                    </span>
                    <span class="text-xs text-muted uppercase">{person.role}</span>
                  </li>
                )}
              </For>
            </ul>
          </section>
        </div>
      )}
    </Show>
  );
}
