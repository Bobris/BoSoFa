import { createUniqueId, onSettled } from "solid-js";
import type { JSX } from "@solidjs/web";
import type { Placement } from "./popover";

/** `popover="hint"` does not close open auto popovers (a menu stays open under its tooltip). Chromium only so far. */
const hintSupported = (() => {
  const probe = document.createElement("div");
  probe.setAttribute("popover", "hint");
  return probe.popover === "hint";
})();

export type TooltipTriggerProps = {
  "aria-describedby": string;
  style: JSX.CSSProperties;
  onPointerEnter: () => void;
  onPointerLeave: () => void;
  onFocus: () => void;
  onBlur: () => void;
  onKeyDown: (e: KeyboardEvent) => void;
};

/**
 * Tooltip on a hint/manual popover: top layer, anchor-positioned, shown after a delay on hover or keyboard focus.
 * The trigger is rendered by the caller so any element can carry it: `{(p) => <button {...p}>…</button>}`.
 */
export function Tooltip(props: {
  content: JSX.Element;
  placement?: Placement;
  delay?: number;
  children: (trigger: TooltipTriggerProps) => JSX.Element;
}) {
  const id = createUniqueId();
  const anchor = `--ui-tip-${id}`;
  let tip!: HTMLDivElement;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const show = () => {
    clearTimeout(timer);
    timer = setTimeout(() => tip.isConnected && !tip.matches(":popover-open") && tip.showPopover(), props.delay ?? 400);
  };
  const hide = () => {
    clearTimeout(timer);
    if (tip.matches(":popover-open")) tip.hidePopover();
  };
  onSettled(() => () => clearTimeout(timer));

  const trigger: TooltipTriggerProps = {
    "aria-describedby": id,
    style: { "anchor-name": anchor },
    onPointerEnter: show,
    onPointerLeave: hide,
    onFocus: show,
    onBlur: hide,
    onKeyDown: (e) => e.key === "Escape" && hide(),
  };

  return (
    <>
      {props.children(trigger)}
      <div
        id={id}
        ref={tip}
        role="tooltip"
        popover={hintSupported ? "hint" : "manual"}
        class="ui-popover ui-tooltip"
        data-placement={props.placement ?? "top-start"}
        style={{ "position-anchor": anchor }}
      >
        {props.content}
      </div>
    </>
  );
}
