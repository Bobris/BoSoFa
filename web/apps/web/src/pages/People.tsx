import { api } from "@bosofa/api";
import { createMemo, For } from "solid-js";
import { useWorkspace } from "../workspace";

/** Everyone who has signed in, and who administers. */
export default function PeoplePage() {
  const workspace = useWorkspace();
  // Reads me() before the first await, so the list loads only for a signed-in user.
  const users = createMemo(async () => (workspace.me() ? ((await api.GET("/api/v1/users")).data ?? []) : []));
  return (
    <div class="mx-auto max-w-3xl space-y-4">
      <h1 class="text-xl font-semibold">People</h1>
      <section class="rounded-lg border border-line bg-panel">
        <ul class="divide-y divide-line">
          <For each={users()}>
            {(person) => (
              <li class="flex items-center justify-between gap-3 px-4 py-2 text-sm">
                <span>
                  {person.name} <span class="text-muted">{person.email}</span>
                </span>
                <span class="text-xs uppercase text-muted">{person.role}</span>
              </li>
            )}
          </For>
        </ul>
      </section>
    </div>
  );
}
