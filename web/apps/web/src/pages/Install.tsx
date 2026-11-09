import { api, getPlatform, type ProductDownload } from "@bosofa/api";
import { createMemo, For, Show } from "solid-js";
import Command from "../components/Command";
import { paths } from "../router";
import { branding } from "../branding";

const platformNames: Record<string, string> = {
  "osx-arm64": "macOS, Apple silicon",
  "osx-x64": "macOS, Intel",
  "linux-x64": "Linux x64",
  "linux-arm64": "Linux Arm64",
  "win-x64": "Windows x64",
  "win-arm64": "Windows Arm64",
};

function Product(props: { title: string; script: string; release: ProductDownload | undefined; origin: string; children: unknown }) {
  return (
    <section class="rounded-lg border border-line bg-panel">
      <div class="px-4 py-3 border-b border-line flex items-center justify-between gap-3">
        <h2 class="text-sm font-medium">{props.title}</h2>
        <span class="text-xs text-muted">{props.release ? `version ${props.release.version}` : "not published yet"}</span>
      </div>
      <div class="px-4 py-3 space-y-3 text-sm">
        {props.children as never}
        <Show
          when={props.release}
          fallback={
            <p class="text-muted">
              An admin publishes it with <code class="font-mono text-xs">{branding().daemon} release-publish</code>.
            </p>
          }
        >
          {(release) => (
            <>
              <Command label="macOS and Linux, in a terminal" text={`curl -fsSL ${props.origin}/install/${props.script}.sh | sh`} />
              <Command label="Windows, in PowerShell" text={`irm ${props.origin}/install/${props.script}.ps1 | iex`} />
              <div class="space-y-1">
                <span class="text-xs text-muted">Or download it yourself</span>
                <ul class="flex flex-wrap gap-x-4 gap-y-1">
                  <For each={release().files} keyed={(file) => file.rid}>
                    {(file) => (
                      <li>
                        <a class="text-accent hover:underline" href={`${props.origin}${file().url}`} download={file().fileName}>
                          {platformNames[file().rid] ?? file().rid}
                        </a>
                        <span class="text-xs text-muted"> {Math.round(Number(file().size) / 1048576)} MB</span>
                      </li>
                    )}
                  </For>
                </ul>
              </div>
            </>
          )}
        </Show>
      </div>
    </section>
  );
}

/** Where a computer starts: the daemon and the desktop app come from this server, and so do their updates. */
export default function Install() {
  const origin = getPlatform().baseUrl || location.origin;
  const downloads = createMemo(async (): Promise<ProductDownload[]> => (await api.GET("/api/v1/downloads")).data ?? []);
  const release = (product: string) => downloads().find((candidate) => candidate.product === product);

  return (
    <div class="space-y-4 mx-auto max-w-3xl">
      <h1 class="text-2xl font-semibold">Install</h1>
      <Product title="Daemon: run sessions on a computer" script="daemon" release={release("daemon")} origin={origin}>
        <p class="text-muted">
          The script installs the daemon as a service of your user and prints a code. Approve it on the{" "}
          <a class="text-accent hover:underline" href={paths.enroll()}>
            enroll page
          </a>{" "}
          and the computer becomes one of your machines. New versions reach it through rollouts.
        </p>
      </Product>
      <Product title="Desktop app" script="desktop" release={release("desktop")} origin={origin}>
        <p class="text-muted">
          The same pages in a window of its own, which can also enable its computer as a machine. It updates itself from this
          server.
        </p>
      </Product>
    </div>
  );
}
