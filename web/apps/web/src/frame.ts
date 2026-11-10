import { getPlatform } from "@bosofa/api";

/** Pressing what is not a control in a top strip moves the window, as a title bar does; only where the page draws the title bar. */
function onBackground(event: MouseEvent) {
  return getPlatform().frame !== undefined && event.button === 0 && !(event.target as Element).closest("button, a, input, textarea, select, [role=button]");
}

export function startWindowDrag(event: PointerEvent) {
  if (!onBackground(event)) return;
  event.preventDefault();
  getPlatform().frame!.startDrag();
}

export function zoomWindow(event: MouseEvent) {
  if (onBackground(event)) getPlatform().frame!.zoom();
}

/** Publishes where the native window buttons sit as `--titlebar-inset` and `--titlebar-height`, again whenever the window changes
 * size (full screen has none). Returns the cleanup. */
export function followTitleBar(): () => void {
  const frame = getPlatform().frame;
  if (!frame) return () => {};
  const root = document.documentElement;
  let pending = false;
  const update = async () => {
    if (pending) return;
    pending = true;
    try {
      const layout = await frame.titleBar();
      root.style.setProperty("--titlebar-inset", `${layout.leftInset}px`);
      root.style.setProperty("--titlebar-height", `${layout.height}px`);
    } catch {
      /* keep what was there */
    } finally {
      pending = false;
    }
  };
  void update();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const onResize = () => {
    clearTimeout(timer);
    timer = setTimeout(() => void update(), 150);
  };
  window.addEventListener("resize", onResize);
  return () => {
    clearTimeout(timer);
    window.removeEventListener("resize", onResize);
  };
}
