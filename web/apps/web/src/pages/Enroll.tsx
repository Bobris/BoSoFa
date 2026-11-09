import { api } from "@bosofa/api";
import { useNavigate, useSearchParams } from "@solidjs/router";
import { createEffect, createMemo, createSignal, Match, Show, Switch } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";
import { shortVersion } from "../version";
import { branding } from "../branding";
import MachineFacts from "../components/MachineFacts";

/** Approve a machine that runs `bosofad enroll`: the daemon shows a code and a link to this page. */
export default function Enroll() {
  const navigate = useNavigate();
  const [search] = useSearchParams<{ code: string }>();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null)
        navigate(`${paths.login}?returnUrl=${encodeURIComponent(`/enroll?code=${search.code ?? ""}`)}`, { replace: true });
    },
  );
  const [code, setCode] = createSignal<string>();
  const current = () => code() ?? search.code ?? "";
  const enrollment = createMemo(async () => {
    const value = current().trim();
    if (!me() || value.length < 8) return undefined;
    const { data } = await api.GET("/api/v1/enrollments/{userCode}", { params: { path: { userCode: value } } });
    return data ?? null;
  });
  const [error, setError] = createSignal<string>();
  const [name, setName] = createSignal<string>();

  async function approve(hostName: string) {
    const chosen = (name() ?? hostName).trim();
    const result = await api.POST("/api/v1/enrollments/{userCode}/approve", {
      params: { path: { userCode: current().trim() }, query: chosen && chosen !== hostName ? { name: chosen } : {} },
    });
    if (result.data) navigate(paths.machines(Number(result.data.id))(), { replace: true });
    else setError(String(result.error || "Approval failed."));
  }

  return (
    <div class="max-w-md space-y-5">
      <h1 class="text-xl font-semibold">Add a machine</h1>
      <label class="block space-y-1">
        <span class="text-sm text-muted">Code shown by {branding().daemon} enroll</span>
        <input
          class="w-full rounded-md border border-line bg-transparent px-3 py-2 font-mono uppercase tracking-widest"
          value={current()}
          onInput={(event) => setCode(event.currentTarget.value)}
          placeholder="XXXX-XXXX"
        />
      </label>
      <Switch>
        <Match when={enrollment() === null}>
          <p class="text-sm text-muted">No machine is waiting with this code.</p>
        </Match>
        <Match when={enrollment()}>
          {(found) => (
            <div class="rounded-lg border border-line bg-panel p-4 space-y-3">
              <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
                <dt class="text-muted">Host name</dt>
                <dd>{found().name}</dd>
                <dt class="text-muted">System</dt>
                <dd>
                  {found().os} {found().arch}
                </dd>
                <dt class="text-muted">Daemon</dt>
                <dd>
                  {branding().daemon} {shortVersion(found().daemonVersion)}
                </dd>
              </dl>
              <Show when={found().resources}>
                {(resources) => <MachineFacts resources={resources()} instanceId={found().instanceId} />}
              </Show>
              <Show
                when={found().status === "Pending"}
                fallback={<p class="text-sm text-muted">This code is {found().status.toLowerCase()}.</p>}
              >
                <label class="block space-y-1">
                  <span class="text-sm text-muted">Call it</span>
                  <input
                    class="w-full rounded-md border border-line bg-transparent px-3 py-2 text-sm"
                    maxlength={100}
                    value={name() ?? found().name}
                    onInput={(event) => setName(event.currentTarget.value)}
                  />
                </label>
                <button
                  class="rounded-md bg-accent text-white px-4 py-2 font-medium disabled:opacity-50"
                  disabled={!(name() ?? found().name).trim()}
                  onClick={() => approve(found().name)}
                >
                  Approve as my machine
                </button>
              </Show>
            </div>
          )}
        </Match>
      </Switch>
      <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>
    </div>
  );
}
