/** What a keyboard that lies over the page covers, as the native backend reports it. */
export interface NeoKeyboardState {
    /** The height of the view that the keyboard covers, in CSS pixels; 0 once it is hidden. */
    readonly height: number;
    /** The duration of the animation of the keyboard, in seconds. */
    readonly duration: number;
}
/**
 * Calls `handler` each time the keyboard of iOS or Android comes up, changes, or goes, while the application keeps it over the page
 * (`NeoAstraIOSKeyboardOverlay`, `NeoAstraAndroidKeyboardOverlay` with `NeoAstraAndroidEdgeToEdge`); the page then keeps its
 * fields clear of it itself.
 * Returns a function that stops the calls.
 */
export declare function onKeyboard(handler: (state: NeoKeyboardState) => void, target?: EventTarget): () => void;
/** Reads the height that the keyboard covers now from the `--neoastra-keyboard-height` variable of the root element, 0 without one. */
export declare function keyboardHeight(root?: {
    readonly style: {
        getPropertyValue(name: string): string;
    };
} | undefined): number;
//# sourceMappingURL=mobile.d.ts.map