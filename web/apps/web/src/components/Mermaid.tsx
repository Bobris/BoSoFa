import { createEffect, createMemo, createSignal, For, Match, Show, Switch } from "solid-js";

type Part = { kind: "text" | "mermaid"; text: string };

// A closed ```mermaid fence on lines of its own; an unclosed one (still streaming) stays text until it closes.
const FENCE = /^[ \t]*```mermaid[ \t]*\r?\n([\s\S]*?)\r?\n[ \t]*```[ \t]*$/gm;

/** Splits text into plain runs and the sources of the Mermaid diagrams fenced in it. */
export function splitMermaid(text: string): Part[] {
  const parts: Part[] = [];
  let last = 0;
  for (const match of text.matchAll(FENCE)) {
    const before = text.slice(last, match.index).replace(/\r?\n$/, "");
    if (before) parts.push({ kind: "text", text: before });
    parts.push({ kind: "mermaid", text: match[1] });
    last = match.index + match[0].length;
  }
  const rest = text.slice(last).replace(/^\r?\n/, "");
  if (rest || parts.length === 0) parts.push({ kind: "text", text: rest });
  return parts;
}

/** A Mermaid diagram drawn by Pele, loaded on first use; the source shows instead when it does not render. */
export function MermaidDiagram(props: { source: string }) {
  let host: HTMLDivElement | undefined;
  const [failed, setFailed] = createSignal<string>();
  createEffect(
    () => props.source,
    (source) => {
      setFailed(undefined);
      let disposed = false;
      let destroy: (() => void) | undefined;
      const fail = (error: unknown) => !disposed && setFailed(error instanceof Error ? error.message : String(error));
      void import("pele/lazy")
        .then(async ({ mountAsync }) => {
          if (disposed || !host) return;
          const mounted = await mountAsync(host, source, { onError: fail });
          if (disposed) mounted.destroy();
          else destroy = () => mounted.destroy();
        })
        .catch(fail);
      return () => {
        disposed = true;
        destroy?.();
      };
    },
  );
  return (
    <div class="my-2 whitespace-normal">
      <div ref={(el) => (host = el)} class={["pele-host overflow-x-auto", { hidden: failed() !== undefined }]} />
      <Show when={failed()}>
        {(message) => (
          <div class="rounded-md border border-line bg-panel text-sm">
            <p class="select-none border-b border-line px-3 py-1.5 text-xs text-muted">Mermaid diagram not rendered: {message()}</p>
            <pre class="overflow-x-auto px-3 py-2 text-xs">{props.source}</pre>
          </div>
        )}
      </Show>
    </div>
  );
}

/** Plain text with its fenced Mermaid blocks drawn as diagrams. */
export function TextWithDiagrams(props: { text: string }) {
  const parts = createMemo(() => splitMermaid(props.text));
  return (
    // By position, so a diagram stays mounted while text streams in after it.
    <For each={parts()} keyed={false}>
      {(part) => (
        <Switch>
          <Match when={part().kind === "mermaid"}>
            <MermaidDiagram source={part().text} />
          </Match>
          <Match when={part().kind === "text"}>
            <span>{part().text}</span>
          </Match>
        </Switch>
      )}
    </For>
  );
}
