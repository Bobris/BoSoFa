import { createContext, onFlush, useContext } from "solid-js";
import type { JSX } from "@solidjs/web";
import { createPopoverState, type Placement, type PopoverState } from "./popover";

type MenuState = PopoverState & { focusLast: boolean };
const MenuContext = createContext<MenuState>();

const items = (menu: HTMLElement) =>
  [...menu.querySelectorAll<HTMLElement>('[role="menuitem"]')].filter((el) => el.getAttribute("aria-disabled") !== "true");

function focusItem(menu: HTMLElement, pick: (list: HTMLElement[], current: number) => number) {
  const list = items(menu);
  if (list.length === 0) return;
  const current = list.indexOf(document.activeElement as HTMLElement);
  list[(pick(list, current) + list.length) % list.length]?.focus();
}

/** Dropdown menu: a native auto popover + WAI-ARIA menu keyboard model (arrows, Home/End, typeahead). */
export function Menu(props: { onOpenChange?: (open: boolean) => void; children: JSX.Element }) {
  const state: MenuState = Object.assign(createPopoverState((open) => props.onOpenChange?.(open)), { focusLast: false });
  return <MenuContext value={state}>{props.children}</MenuContext>;
}

export function MenuTrigger(props: { class?: string; children: JSX.Element }) {
  const menu = useContext(MenuContext);
  return (
    <button
      type="button"
      ref={menu.setTrigger}
      class={props.class}
      popovertarget={menu.id}
      aria-haspopup="menu"
      style={{ "anchor-name": menu.anchor }}
      onKeyDown={(e) => {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
          menu.focusLast = e.key === "ArrowUp";
          menu.show();
        }
      }}
      onClick={() => (menu.focusLast = false)}
    >
      {props.children}
    </button>
  );
}

export function MenuContent(props: { class?: string; placement?: Placement; "aria-label"?: string; children: JSX.Element }) {
  const menu = useContext(MenuContext);
  let el!: HTMLElement;
  let typeahead = "";
  let typeaheadTimer: ReturnType<typeof setTimeout> | undefined;

  const onKeyDown = (e: KeyboardEvent) => {
    switch (e.key) {
      case "ArrowDown":
        focusItem(el, (_, i) => i + 1);
        break;
      case "ArrowUp":
        focusItem(el, (l, i) => (i < 0 ? l.length - 1 : i - 1));
        break;
      case "Home":
        focusItem(el, () => 0);
        break;
      case "End":
        focusItem(el, (l) => l.length - 1);
        break;
      case "Tab":
        menu.hide();
        return;
      default:
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
          clearTimeout(typeaheadTimer);
          typeahead += e.key.toLowerCase();
          typeaheadTimer = setTimeout(() => (typeahead = ""), 500);
          const match = items(el).find((item) => item.textContent?.trim().toLowerCase().startsWith(typeahead));
          match?.focus();
        }
        return;
    }
    e.preventDefault();
  };

  return (
    <div
      id={menu.id}
      ref={(node) => {
        el = node;
        menu.setContent(node);
      }}
      popover="auto"
      role="menu"
      aria-label={props["aria-label"]}
      tabindex="-1"
      class={["ui-popover ui-menu", props.class]}
      data-placement={props.placement ?? "bottom-start"}
      style={{ "position-anchor": menu.anchor }}
      onToggle={(e) => {
        menu.onToggle(e);
        if (e.newState === "open") {
          focusItem(el, (l) => (menu.focusLast ? l.length - 1 : 0));
        }
      }}
      onKeyDown={onKeyDown}
    >
      {props.children}
    </div>
  );
}

export function MenuItem(props: { onSelect: () => void; disabled?: boolean; class?: string; children: JSX.Element }) {
  const menu = useContext(MenuContext);
  const select = () => {
    if (props.disabled) return;
    // Close first, on its own flush, so a select handler that navigates does not hold the close.
    onFlush(() => {
      menu.hide();
      menu.trigger()?.focus();
    });
    props.onSelect();
  };
  return (
    <div
      role="menuitem"
      tabindex="-1"
      aria-disabled={props.disabled ? "true" : undefined}
      class={["ui-menu-item", props.class]}
      onPointerMove={(e) => e.currentTarget.focus()}
      onClick={select}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          select();
        }
      }}
    >
      {props.children}
    </div>
  );
}

export function MenuSeparator() {
  return <div role="separator" class="ui-menu-separator" />;
}
