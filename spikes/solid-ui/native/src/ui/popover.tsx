import { createContext, createSignal, createUniqueId, useContext } from "solid-js";
import type { JSX } from "@solidjs/web";

export type Placement = "bottom-start" | "bottom-end" | "top-start" | "top-end" | "right-start" | "left-start";

/** Shared by Popover and Menu: one popover element anchored to one trigger. */
export type PopoverState = {
  id: string;
  anchor: string;
  open: () => boolean;
  show: () => void;
  hide: () => void;
  /** Wired to the popover element's `toggle` event, the single source of truth for `open`. */
  onToggle: (e: ToggleEvent) => void;
  setContent: (el: HTMLElement) => void;
  setTrigger: (el: HTMLElement) => void;
  content: () => HTMLElement | undefined;
  trigger: () => HTMLElement | undefined;
};

export function createPopoverState(onOpenChange?: (open: boolean) => void): PopoverState {
  const id = createUniqueId();
  const [open, setOpen] = createSignal(false);
  let content: HTMLElement | undefined;
  let trigger: HTMLElement | undefined;
  return {
    id,
    anchor: `--ui-anchor-${id}`,
    open,
    // `source` (Chromium 133+) makes the trigger the implicit anchor and focus-return target; TS 5.9 lacks the type.
    show: () => (content?.showPopover as (o?: { source?: HTMLElement }) => void)?.call(content, { source: trigger }),
    hide: () => content?.hidePopover(),
    onToggle: (e) => {
      const next = e.newState === "open";
      setOpen(next);
      onOpenChange?.(next);
    },
    setContent: (el) => (content = el),
    setTrigger: (el) => (trigger = el),
    content: () => content,
    trigger: () => trigger,
  };
}

const PopoverContext = createContext<PopoverState>();

/** Read the enclosing popover; throws (ContextNotFoundError) outside <Popover>. */
export const usePopover = () => useContext(PopoverContext);

export function Popover(props: { onOpenChange?: (open: boolean) => void; children: JSX.Element }) {
  // onOpenChange is read when the toggle fires, so a later prop change is still honoured.
  const state = createPopoverState((open) => props.onOpenChange?.(open));
  return <PopoverContext value={state}>{props.children}</PopoverContext>;
}

export function PopoverTrigger(props: { class?: string; children: JSX.Element }) {
  const state = usePopover();
  return (
    <button
      type="button"
      ref={state.setTrigger}
      class={props.class}
      // popovertarget gives toggle-on-click, aria-expanded and focus return to the invoker with no script.
      popovertarget={state.id}
      style={{ "anchor-name": state.anchor }}
    >
      {props.children}
    </button>
  );
}

export function PopoverContent(props: {
  class?: string;
  placement?: Placement;
  /** "auto" light-dismisses and closes other auto popovers; "manual" does neither. */
  mode?: "auto" | "manual";
  role?: JSX.HTMLAttributes<HTMLDivElement>["role"];
  "aria-label"?: string;
  children: JSX.Element;
}) {
  const state = usePopover();
  return (
    <div
      id={state.id}
      ref={state.setContent}
      popover={props.mode ?? "auto"}
      role={props.role ?? "dialog"}
      aria-label={props["aria-label"]}
      class={["ui-popover", props.class]}
      data-placement={props.placement ?? "bottom-start"}
      style={{ "position-anchor": state.anchor }}
      onToggle={state.onToggle}
    >
      {props.children}
    </div>
  );
}
