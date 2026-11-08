/**
 * Calls `handler` each time the keyboard of iOS or Android comes up, changes, or goes, while the application keeps it over the page
 * (`NeoAstraIOSKeyboardOverlay`, `NeoAstraAndroidKeyboardOverlay` with `NeoAstraAndroidEdgeToEdge`); the page then keeps its
 * fields clear of it itself.
 * Returns a function that stops the calls.
 */
export function onKeyboard(handler, target = globalThis) {
    const listener = (event) => {
        const detail = event.detail;
        if (typeof detail?.height !== "number" || typeof detail.duration !== "number" || !(detail.height >= 0))
            return;
        handler({ height: detail.height, duration: detail.duration });
    };
    target.addEventListener("neoastrakeyboard", listener);
    return () => target.removeEventListener("neoastrakeyboard", listener);
}
/** Reads the height that the keyboard covers now from the `--neoastra-keyboard-height` variable of the root element, 0 without one. */
export function keyboardHeight(root = globalThis.document?.documentElement) {
    const value = Number.parseFloat(root?.style.getPropertyValue("--neoastra-keyboard-height") ?? "");
    return Number.isFinite(value) && value > 0 ? value : 0;
}
//# sourceMappingURL=mobile.js.map