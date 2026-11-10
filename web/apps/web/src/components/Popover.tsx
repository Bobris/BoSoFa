import { createEffect, createSignal, For, Show } from "solid-js";
import type { JSX } from "@solidjs/web";
import { CheckIcon, ChevronDownIcon } from "./icons";

/** Content floating by its trigger; a click elsewhere or Escape closes it. */
export function Popover(props: { open: boolean; onClose: () => void; class?: string | string[]; children: JSX.Element }) {
  let panel: HTMLDivElement | undefined;
  createEffect(
    () => props.open,
    (open) => {
      if (!open) return;
      const onPointer = (event: PointerEvent) => {
        // The trigger toggles itself; anything else outside closes.
        const target = event.target as Node;
        if (panel && !panel.contains(target) && !panel.parentElement?.contains(target)) props.onClose();
      };
      const onKey = (event: KeyboardEvent) => {
        if (event.key === "Escape") props.onClose();
      };
      document.addEventListener("pointerdown", onPointer);
      document.addEventListener("keydown", onKey);
      return () => {
        document.removeEventListener("pointerdown", onPointer);
        document.removeEventListener("keydown", onKey);
      };
    },
  );
  return (
    <Show when={props.open}>
      <div
        ref={(element) => (panel = element)}
        class={[
          "absolute z-30 min-w-56 overflow-hidden rounded-xl border border-line bg-panel p-1 text-sm shadow-lg shadow-black/10 animate-pop",
          props.class,
        ]}
      >
        {props.children}
      </div>
    </Show>
  );
}

export type Choice = { value: string; label: string; hint?: string; disabled?: boolean };

/** A compact chooser: a pill showing the current choice that opens a list of the others. */
export function Picker(props: {
  icon?: JSX.Element;
  label: string;
  title?: string;
  value: string;
  choices: Choice[];
  onChange: (value: string) => void;
  /** Opens above the pill, as in a composer at the bottom of the screen. */
  up?: boolean;
  heading?: string;
  footer?: JSX.Element;
}) {
  const [open, setOpen] = createSignal(false);
  return (
    <div class="relative">
      <button
        type="button"
        title={props.title}
        class={[
          "flex h-8 max-w-64 items-center gap-1.5 rounded-lg px-2.5 text-sm text-muted transition-colors hover:bg-hover hover:text-ink",
          { "bg-hover text-ink": open() },
        ]}
        onClick={() => setOpen((value) => !value)}
      >
        {props.icon}
        <span class="truncate">{props.label}</span>
        <ChevronDownIcon class="size-3.5 opacity-60" />
      </button>
      <Popover open={open()} onClose={() => setOpen(false)} class={[props.up ? "bottom-full mb-1.5" : "top-full mt-1.5", "left-0 max-h-80 overflow-y-auto"]}>
        <Show when={props.heading}>
          <p class="px-2.5 pb-1 pt-1.5 text-xs font-medium text-muted">{props.heading}</p>
        </Show>
        <For each={props.choices} keyed={(choice) => choice.value}>
          {(choice) => (
            <button
              type="button"
              disabled={choice().disabled}
              class="flex w-full items-start gap-2 rounded-lg px-2.5 py-1.5 text-left hover:bg-hover disabled:opacity-50 disabled:hover:bg-transparent"
              onClick={() => {
                props.onChange(choice().value);
                setOpen(false);
              }}
            >
              <span class="min-w-0 flex-1">
                <span class="block truncate">{choice().label}</span>
                <Show when={choice().hint}>{(hint) => <span class="block truncate text-xs text-muted">{hint()}</span>}</Show>
              </span>
              <span class="mt-0.5 size-4">
                <Show when={choice().value === props.value}>
                  <CheckIcon class="size-4 text-accent" />
                </Show>
              </span>
            </button>
          )}
        </For>
        <Show when={props.footer}>
          <div class="mt-1 border-t border-line pt-1">{props.footer}</div>
        </Show>
      </Popover>
    </div>
  );
}
