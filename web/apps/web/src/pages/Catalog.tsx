import { api, uploadBlob, type McpServer, type Profile, type SkillBundle } from "@bosofa/api";
import { useNavigate } from "@solidjs/router";
import { createEffect, createMemo, createSignal, For, refresh, Show } from "solid-js";
import { paths } from "../router";
import { fetchMe } from "../session";

const field = "w-full rounded-md border border-line bg-panel px-3 py-2 text-sm";
const button = "rounded-md bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50";
const section = "space-y-3 rounded-lg border border-line bg-panel p-4";

/** "KEY=value" lines as a map. */
function parseSecrets(text: string): Record<string, string> {
  const secrets: Record<string, string> = {};
  for (const line of text.split("\n")) {
    const at = line.indexOf("=");
    if (at > 0) secrets[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }
  return secrets;
}

/** MCP servers, skill bundles and profiles. Everyone sees them; admins change them. */
export default function Catalog() {
  const navigate = useNavigate();
  const me = createMemo(() => fetchMe());
  createEffect(
    () => me(),
    (user) => {
      if (user === null) navigate(`${paths.login}?returnUrl=${encodeURIComponent("/catalog")}`, { replace: true });
    },
  );
  const servers = createMemo(async (): Promise<McpServer[]> => (me() ? ((await api.GET("/api/v1/mcp-servers")).data ?? []) : []));
  const skills = createMemo(async (): Promise<SkillBundle[]> => (me() ? ((await api.GET("/api/v1/skills")).data ?? []) : []));
  const profiles = createMemo(async (): Promise<Profile[]> => (me() ? ((await api.GET("/api/v1/profiles")).data ?? []) : []));
  const isAdmin = () => me()?.role === "Admin";
  const [error, setError] = createSignal<string>();

  function report(what: string, response: Response) {
    setError(response.ok ? undefined : `${what} failed (${response.status}).`);
    return response.ok;
  }

  const [serverName, setServerName] = createSignal("");
  const [serverTarget, setServerTarget] = createSignal("");
  const [serverSecrets, setServerSecrets] = createSignal("");
  async function addServer(event: SubmitEvent) {
    event.preventDefault();
    const target = serverTarget().trim();
    const remote = /^https?:\/\//.test(target);
    const [command, ...args] = target.split(/\s+/);
    const { response } = await api.POST("/api/v1/mcp-servers", {
      body: {
        name: serverName().trim(),
        transport: remote ? "Http" : "Stdio",
        command: remote ? null : command,
        args: remote ? [] : args,
        url: remote ? target : null,
        secrets: parseSecrets(serverSecrets()),
      },
    });
    if (report("Saving the MCP server", response)) {
      setServerName("");
      setServerTarget("");
      setServerSecrets("");
      refresh(servers);
    }
  }

  const [skillName, setSkillName] = createSignal("");
  const [skillVersion, setSkillVersion] = createSignal("1");
  let skillFile: HTMLInputElement | undefined;
  async function addSkill(event: SubmitEvent) {
    event.preventDefault();
    const file = skillFile?.files?.[0];
    if (!file) return;
    const uploaded = await uploadBlob(file);
    if (!uploaded.sha256) return setError(`Uploading the bundle failed: ${uploaded.error}`);
    const { sha256 } = uploaded;
    const { response } = await api.POST("/api/v1/skills", {
      body: { name: skillName().trim(), version: skillVersion().trim(), sha256, description: null },
    });
    if (report("Publishing the skill", response)) {
      setSkillName("");
      refresh(skills);
    }
  }

  const [profileName, setProfileName] = createSignal("");
  const [profileModel, setProfileModel] = createSignal("");
  const [chosenServers, setChosenServers] = createSignal<string[]>([]);
  const [chosenSkills, setChosenSkills] = createSignal<string[]>([]);
  const toggle = (list: string[], value: string, on: boolean) => (on ? [...list, value] : list.filter((item) => item !== value));
  async function addProfile(event: SubmitEvent) {
    event.preventDefault();
    const { response } = await api.POST("/api/v1/profiles", {
      body: {
        name: profileName().trim(),
        model: profileModel().trim() || null,
        permissionMode: null,
        mcpServers: chosenServers(),
        skills: chosenSkills().map((key) => {
          const [name, version] = key.split("@");
          return { name, version };
        }),
      },
    });
    if (report("Saving the profile", response)) {
      setProfileName("");
      setChosenServers([]);
      setChosenSkills([]);
      refresh(profiles);
    }
  }

  return (
    <Show when={me()}>
      <div class="mx-auto max-w-2xl space-y-6">
        <h1 class="text-xl font-semibold">Catalog</h1>
        <Show when={error()}>{(message) => <p class="text-sm text-red-600">{message()}</p>}</Show>

        <section class={section}>
          <h2 class="text-sm font-medium">Profiles</h2>
          <ul class="text-sm">
            <For each={profiles()} keyed={(profile) => profile.name} fallback={<li class="text-muted">None yet.</li>}>
              {(profile) => (
                <li>
                  {profile().name}{" "}
                  <span class="text-muted">
                    {[...profile().mcpServers, ...profile().skills.map((skill) => `${skill.name}@${skill.version}`)].join(", ")}
                  </span>
                </li>
              )}
            </For>
          </ul>
          <Show when={isAdmin()}>
            <form class="space-y-2 border-t border-line pt-3" onSubmit={addProfile}>
              <input class={field} required placeholder="Name" value={profileName()} onInput={(e) => setProfileName(e.currentTarget.value)} />
              <input class={field} placeholder="Model (optional)" value={profileModel()} onInput={(e) => setProfileModel(e.currentTarget.value)} />
              <For each={servers()} keyed={(server) => server.name}>
                {(server) => (
                  <label class="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      checked={chosenServers().includes(server().name)}
                      onChange={(e) => setChosenServers(toggle(chosenServers(), server().name, e.currentTarget.checked))}
                    />
                    MCP: {server().name}
                  </label>
                )}
              </For>
              <For each={skills()} keyed={(skill) => `${skill.name}@${skill.version}`}>
                {(skill) => (
                  <label class="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      checked={chosenSkills().includes(`${skill().name}@${skill().version}`)}
                      onChange={(e) => setChosenSkills(toggle(chosenSkills(), `${skill().name}@${skill().version}`, e.currentTarget.checked))}
                    />
                    Skill: {skill().name}@{skill().version}
                  </label>
                )}
              </For>
              <button type="submit" class={button} disabled={!profileName().trim()}>
                Save profile
              </button>
            </form>
          </Show>
        </section>

        <section class={section}>
          <h2 class="text-sm font-medium">MCP servers</h2>
          <ul class="text-sm">
            <For each={servers()} keyed={(server) => server.name} fallback={<li class="text-muted">None yet.</li>}>
              {(server) => (
                <li>
                  {server().name}{" "}
                  <span class="font-mono text-xs text-muted">
                    {server().url ?? [server().command, ...server().args].join(" ")} {server().secretNames.join(" ")}
                  </span>
                </li>
              )}
            </For>
          </ul>
          <Show when={isAdmin()}>
            <form class="space-y-2 border-t border-line pt-3" onSubmit={addServer}>
              <input class={field} required placeholder="Name" value={serverName()} onInput={(e) => setServerName(e.currentTarget.value)} />
              <input class={[field, "font-mono"]} required placeholder="Command and arguments, or https:// URL" value={serverTarget()} onInput={(e) => setServerTarget(e.currentTarget.value)} />
              <textarea class={[field, "font-mono"]} rows={2} placeholder="Secrets, one KEY=value per line (environment, or headers for a URL)" value={serverSecrets()} onInput={(e) => setServerSecrets(e.currentTarget.value)} />
              <button type="submit" class={button} disabled={!serverName().trim() || !serverTarget().trim()}>
                Save server
              </button>
            </form>
          </Show>
        </section>

        <section class={section}>
          <h2 class="text-sm font-medium">Skills</h2>
          <ul class="text-sm">
            <For each={skills()} keyed={(skill) => `${skill.name}@${skill.version}`} fallback={<li class="text-muted">None yet.</li>}>
              {(skill) => (
                <li>
                  {skill().name}@{skill().version} <span class="text-muted">{skill().description}</span>
                </li>
              )}
            </For>
          </ul>
          <Show when={isAdmin()}>
            <form class="space-y-2 border-t border-line pt-3" onSubmit={addSkill}>
              <div class="grid grid-cols-2 gap-2">
                <input class={field} required placeholder="Name" value={skillName()} onInput={(e) => setSkillName(e.currentTarget.value)} />
                <input class={field} required placeholder="Version" value={skillVersion()} onInput={(e) => setSkillVersion(e.currentTarget.value)} />
              </div>
              <input class="text-sm" type="file" accept=".zip" required ref={(element) => (skillFile = element)} />
              <p class="text-xs text-muted">A .zip with SKILL.md at its root.</p>
              <button type="submit" class={button} disabled={!skillName().trim()}>
                Publish skill
              </button>
            </form>
          </Show>
        </section>
      </div>
    </Show>
  );
}
