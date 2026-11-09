import { createSignal, Show } from "solid-js";

/** A command to run, with a button that copies it. */
export default function Command(props: { label?: string; text: string }) {
  const [copied, setCopied] = createSignal(false);
  return (
    <div class="space-y-1">
      <Show when={props.label}>
        <span class="text-xs text-muted">{props.label}</span>
      </Show>
      <div class="flex items-center gap-2">
        <code class="flex-1 min-w-0 truncate rounded-md border border-line bg-canvas px-2 py-1 font-mono text-xs">{props.text}</code>
        <button
          class="text-xs text-accent hover:underline shrink-0"
          onClick={async () => {
            await navigator.clipboard.writeText(props.text);
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
          }}
        >
          {copied() ? "Copied" : "Copy"}
        </button>
      </div>
    </div>
  );
}
