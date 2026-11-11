import { createMemo, createSignal, For, Repeat, Show } from "solid-js";
import { CheckIcon, ChevronDownIcon } from "./icons";
import { Popover } from "./Popover";

/** A model as a chooser shows it; an empty id stands for the harness's own choice. */
export type ModelOption = {
  id: string;
  name: string;
  description?: string | null;
  efforts: readonly string[];
  defaultEffort?: string | null;
};

/** Beyond this many models a filter helps more than scrolling. */
const FILTER_FROM = 8;

/** How full a level's meter is drawn: its place among the model's levels. */
function Meter(props: { filled: number; of: number }) {
  return (
    <span class="flex h-3 items-end gap-px" aria-hidden="true">
      <Repeat count={props.of}>
        {(index) => (
          <span
            class={[
              "w-[3px] rounded-[1px] bg-current",
              { "opacity-25": index >= props.filled },
            ]}
            style={{ height: `${4 + (8 * (index + 1)) / props.of}px` }}
          />
        )}
      </Repeat>
    </span>
  );
}

/**
 * One chooser for the model and how hard it thinks: the models, filtered when there are many, and the chosen one's reasoning
 * levels under them. A model can also be named outright, for one the list does not know.
 */
export function ModelPicker(props: {
  models: readonly ModelOption[];
  model: string;
  effort: string;
  onModel: (id: string) => void;
  onEffort: (effort: string) => void;
  /** Says why the list is short or missing: the machine is offline, the harness could not tell. */
  status?: string;
  /** No model named is a choice of its own: the harness picks. */
  offersDefault?: boolean;
}) {
  const [open, setOpen] = createSignal(false);
  const [filter, setFilter] = createSignal("");
  const options = createMemo((): ModelOption[] => {
    const listed = [...props.models];
    return props.offersDefault && !listed.some((option) => option.id === "")
      ? [
          {
            id: "",
            name: "Default",
            description: "The harness's own choice",
            efforts: [],
          },
          ...listed,
        ]
      : listed;
  });
  const shown = createMemo(() => {
    const words = filter().trim().toLowerCase();
    return words
      ? options().filter((option) =>
          `${option.name} ${option.id} ${option.description ?? ""}`
            .toLowerCase()
            .includes(words),
        )
      : options();
  });
  const selected = () => options().find((option) => option.id === props.model);
  const efforts = () => selected()?.efforts ?? [];
  const label = () => {
    const name =
      selected()?.name ??
      (props.model ||
        (props.offersDefault ? "Default model" : "Choose a model"));
    return props.effort && efforts().includes(props.effort)
      ? `${name} · ${props.effort}`
      : name;
  };
  const typed = () => filter().trim();

  function choose(id: string, levels: readonly string[]) {
    props.onModel(id);
    if (props.effort && !levels.includes(props.effort)) props.onEffort("");
    setFilter("");
    // With levels to choose from the chooser stays, so the level can follow.
    if (levels.length === 0) setOpen(false);
  }

  return (
    <div class="relative">
      <button
        type="button"
        class={[
          "flex h-8 max-w-56 items-center gap-1.5 rounded-lg px-2.5 text-sm text-muted transition-colors hover:bg-hover hover:text-ink",
          { "bg-hover text-ink": open() },
        ]}
        title={selected()?.description ?? undefined}
        onClick={() => setOpen((value) => !value)}
      >
        <Show when={props.effort && efforts().includes(props.effort)}>
          <Meter
            filled={efforts().indexOf(props.effort) + 1}
            of={efforts().length}
          />
        </Show>
        <span class="truncate">{label()}</span>
        <ChevronDownIcon class="size-3.5 opacity-60" />
      </button>
      <Popover
        open={open()}
        onClose={() => setOpen(false)}
        class="right-0 top-full mt-1.5 w-96 max-w-[calc(100vw-2rem)]"
      >
        <Show
          when={options().length > FILTER_FROM}
          fallback={
            <p class="px-2.5 pb-1 pt-1.5 text-xs font-medium text-muted">
              Model
            </p>
          }
        >
          <input
            ref={(element) => requestAnimationFrame(() => element.focus())} // a long list is found by typing, as soon as it opens
            class="mb-1 w-full rounded-lg bg-hover px-2.5 py-1.5 text-sm outline-none placeholder:text-muted"
            placeholder="Find a model, or name one"
            value={filter()}
            onInput={(event) => setFilter(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key !== "Enter") return;
              event.preventDefault();
              const first = shown()[0];
              if (first) choose(first.id, first.efforts);
              else if (typed()) choose(typed(), []);
            }}
          />
        </Show>
        <div class="max-h-52 overflow-y-auto">
          <For each={shown()} keyed={(option) => option.id}>
            {(option) => (
              <button
                type="button"
                class="flex w-full items-start gap-2 rounded-lg px-2.5 py-1.5 text-left hover:bg-hover"
                onClick={() => choose(option().id, option().efforts)}
              >
                <span class="min-w-0 flex-1">
                  <span class="block truncate">{option().name}</span>
                  <Show when={option().description}>
                    {(description) => (
                      <span class="block truncate text-xs text-muted">
                        {description()}
                      </span>
                    )}
                  </Show>
                </span>
                <span class="mt-0.5 size-4 shrink-0">
                  <Show when={option().id === props.model}>
                    <CheckIcon class="size-4 text-accent" />
                  </Show>
                </span>
              </button>
            )}
          </For>
          <Show
            when={typed() && !options().some((option) => option.id === typed())}
          >
            <button
              type="button"
              class="w-full rounded-lg px-2.5 py-1.5 text-left hover:bg-hover"
              onClick={() => choose(typed(), [])}
            >
              Use <span class="font-mono">{typed()}</span>
            </button>
          </Show>
          <Show when={props.status}>
            {(status) => (
              <p class="px-2.5 py-1.5 text-xs text-muted">{status()}</p>
            )}
          </Show>
        </div>
        <Show when={options().length <= FILTER_FROM}>
          <input
            class="mt-1 w-full rounded-lg px-2.5 py-1.5 text-sm outline-none placeholder:text-muted hover:bg-hover focus:bg-hover"
            placeholder="Another model, by name…"
            value={filter()}
            onInput={(event) => setFilter(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key !== "Enter" || !typed()) return;
              event.preventDefault();
              choose(typed(), []);
            }}
          />
        </Show>
        <Show when={efforts().length > 0}>
          <div class="mt-1 border-t border-line px-2.5 pb-1.5 pt-2">
            <p class="pb-1.5 text-xs font-medium text-muted">Reasoning</p>
            <div class="flex flex-wrap gap-1">
              <For each={["", ...efforts()]}>
                {(level, index) => (
                  <button
                    type="button"
                    class={[
                      "flex h-7 items-center gap-1.5 rounded-md px-2 text-xs transition-colors",
                      level === props.effort
                        ? "bg-accent text-white"
                        : "bg-hover text-muted hover:text-ink",
                    ]}
                    title={
                      level
                        ? undefined
                        : selected()?.defaultEffort
                          ? `The model's default: ${selected()?.defaultEffort}`
                          : "The model's default"
                    }
                    onClick={() => {
                      props.onEffort(level);
                      setOpen(false);
                    }}
                  >
                    <Show when={level}>
                      <Meter filled={index()} of={efforts().length} />
                    </Show>
                    {level || "Default"}
                  </button>
                )}
              </For>
            </div>
          </div>
        </Show>
      </Popover>
    </div>
  );
}
