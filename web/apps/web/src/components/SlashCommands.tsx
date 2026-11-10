import { api, type Session } from "@bosofa/api";
import { For } from "solid-js";

/** A command typed in a session's composer as `/name`; it runs on the server instead of going to the model as a message. */
export type SlashCommand = {
  name: string;
  description: string;
  /** Whether this session can take it. */
  available: (session: Session) => boolean;
  /** What went wrong, if anything. */
  run: (session: Session) => Promise<string | undefined>;
};

export const slashCommands: SlashCommand[] = [
  {
    name: "compact",
    description: "Summarize the conversation so far to free up context",
    // A recorded transcript has nothing to summarize.
    available: (session) => session.harness !== "replay" && session.status !== "Failed",
    async run(session) {
      const { response, error } = await api.POST("/api/v1/sessions/{id}/compact", { params: { path: { id: Number(session.id) } } });
      return response.ok ? undefined : typeof error === "string" ? error : `Compacting failed (${response.status}).`;
    },
  },
];

/** The commands whose name starts with what is typed after the slash; none once the text is more than a command. */
export function matchingCommands(text: string, session: Session | undefined): SlashCommand[] {
  if (!session || !text.startsWith("/") || /\s/.test(text.trimEnd())) return [];
  const typed = text.trim().slice(1).toLowerCase();
  return slashCommands.filter((command) => command.available(session) && command.name.startsWith(typed));
}

/** The list above the composer while a command is being typed. */
export function SlashMenu(props: { commands: SlashCommand[]; selected: number; onPick: (command: SlashCommand) => void; onHover: (index: number) => void }) {
  return (
    <div class="absolute inset-x-0 bottom-full z-30 mb-2 animate-pop select-none overflow-hidden rounded-xl border border-line bg-panel p-1 text-sm shadow-lg shadow-black/10">
      <p class="px-2.5 pb-1 pt-1.5 text-xs font-medium text-muted">Commands</p>
      <For each={props.commands} keyed={(command) => command.name}>
        {(command, index) => (
          <button
            type="button"
            class={["flex w-full items-baseline gap-3 rounded-lg px-2.5 py-1.5 text-left", index() === props.selected ? "bg-hover" : "hover:bg-hover"]}
            onMouseEnter={() => props.onHover(index())}
            onMouseDown={(event) => event.preventDefault()} // keeps the focus in the composer
            onClick={() => props.onPick(command())}
          >
            <span class="font-mono font-medium">/{command().name}</span>
            <span class="min-w-0 truncate text-muted">{command().description}</span>
          </button>
        )}
      </For>
    </div>
  );
}
