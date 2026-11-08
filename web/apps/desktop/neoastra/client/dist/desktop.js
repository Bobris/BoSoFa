export const desktopCommands = Object.freeze({
    dialogs: Object.freeze({ openFile: "desktop.dialogs.open-file", openFolder: "desktop.dialogs.open-folder", saveFile: "desktop.dialogs.save-file", message: "desktop.dialogs.message" }),
    menus: Object.freeze({ activate: "desktop.menus.activate", set: "desktop.menus.set", popup: "desktop.menus.popup" }),
    tray: Object.freeze({ create: "desktop.tray.create", update: "desktop.tray.update", remove: "desktop.tray.remove", activated: "desktop.tray.activated" }),
    clipboard: Object.freeze({ readText: "desktop.clipboard.read-text", writeText: "desktop.clipboard.write-text", readRich: "desktop.clipboard.read-rich", writeRich: "desktop.clipboard.write-rich", clear: "desktop.clipboard.clear" }),
    notifications: Object.freeze({ status: "desktop.notifications.status", show: "desktop.notifications.show", remove: "desktop.notifications.remove", activated: "desktop.notifications.activated", schedule: "desktop.notifications.schedule", unschedule: "desktop.notifications.unschedule", requestPermission: "desktop.notifications.request-permission", launchActivation: "desktop.notifications.launch-activation" }),
    shortcuts: Object.freeze({ register: "desktop.shortcuts.register", unregister: "desktop.shortcuts.unregister", activated: "desktop.shortcuts.activated" }),
    system: Object.freeze({ theme: "desktop.system.theme", displays: "desktop.system.displays", metadata: "desktop.system.metadata", themeChanged: "desktop.system.theme-changed", displaysChanged: "desktop.system.displays-changed" }),
    opener: Object.freeze({ url: "desktop.opener.url", file: "desktop.opener.file", reveal: "desktop.opener.reveal" }),
    dragDrop: Object.freeze({ outbound: "desktop.drag-drop.outbound", resolveFile: "desktop.drag-drop.resolve-file", inbound: "desktop.drag-drop.inbound" }),
    safeStorage: Object.freeze({ store: "desktop.safe-storage.store", retrieve: "desktop.safe-storage.retrieve", delete: "desktop.safe-storage.delete", contains: "desktop.safe-storage.contains" }),
    haptics: Object.freeze({ play: "desktop.haptics.play" }),
    window: Object.freeze({
        getState: "desktop.window.get-state", setTitle: "desktop.window.set-title", setPosition: "desktop.window.set-position", setSize: "desktop.window.set-size", setMinimumSize: "desktop.window.set-minimum-size", setMaximumSize: "desktop.window.set-maximum-size",
        show: "desktop.window.show", hide: "desktop.window.hide", focus: "desktop.window.focus", maximize: "desktop.window.maximize", minimize: "desktop.window.minimize", restore: "desktop.window.restore", setFullscreen: "desktop.window.set-fullscreen",
        setDecorations: "desktop.window.set-decorations", setResizable: "desktop.window.set-resizable", setAlwaysOnTop: "desktop.window.set-always-on-top", setTaskbarVisible: "desktop.window.set-taskbar-visible", close: "desktop.window.close",
        interceptClose: "desktop.window.intercept-close", completeClose: "desktop.window.complete-close", closeRequested: "desktop.window.close-requested",
        setTitleBar: "desktop.window.set-title-bar", startDrag: "desktop.window.start-drag", startResize: "desktop.window.start-resize", stateChanged: "desktop.window.state-changed",
        setIcon: "desktop.window.set-icon", setRepresentedFile: "desktop.window.set-represented-file", getExtraSupport: "desktop.window.get-extra-support", requestAttention: "desktop.window.request-attention", setProgress: "desktop.window.set-progress", setBadge: "desktop.window.set-badge", setDocumentEdited: "desktop.window.set-document-edited", setContentProtection: "desktop.window.set-content-protection", setTitleBarTheme: "desktop.window.set-titlebar-theme", setBackgroundColor: "desktop.window.set-background-color",
    }),
    application: Object.freeze({ requestQuit: "desktop.application.request-quit" }),
});
const dragRegion = "[data-neoastra-drag-region]";
// Controls inside a drag region keep their own pointer behavior.
const dragExclusions = "a,button,input,select,textarea,summary,label,[role=button],[contenteditable],[data-neoastra-no-drag]";
const dragRegionRules = `${dragRegion}{app-region:drag;user-select:none}${dragRegion} :is(${dragExclusions}){app-region:no-drag}`;
/** Makes `data-neoastra-drag-region` elements move the window like a native title bar. */
function installDragRegions(target, snapshot, actions) {
    const view = target.defaultView;
    // Chromium-based backends resolve `app-region` natively, including double-click maximize and the system menu.
    if (view?.CSS?.supports?.("app-region", "drag") === true && typeof view.CSSStyleSheet === "function" && Array.isArray(target.adoptedStyleSheets)) {
        const sheet = new view.CSSStyleSheet();
        sheet.replaceSync(dragRegionRules);
        target.adoptedStyleSheets = [...target.adoptedStyleSheets, sheet];
        return () => { target.adoptedStyleSheets = target.adoptedStyleSheets.filter(item => item !== sheet); };
    }
    // Other engines have no drag-region styling, so a primary press asks the host to start the native move.
    const onMouseDown = (event) => {
        const element = view !== null && event.target instanceof view.Element ? event.target : null;
        const region = element?.closest(dragRegion);
        if (event.button !== 0 || element === null || !region)
            return;
        const excluded = element.closest(dragExclusions);
        if (excluded !== null && region.contains(excluded))
            return;
        event.preventDefault();
        const current = snapshot();
        if (event.detail === 2) {
            if (current?.resizable === true && current.state !== "Fullscreen")
                void actions.toggleMaximize(current.state === "Maximized").catch(() => { });
        }
        else if (event.detail <= 1) {
            void actions.drag().catch(() => { });
        }
    };
    target.addEventListener("mousedown", onMouseDown);
    return () => target.removeEventListener("mousedown", onMouseDown);
}
export function createDesktopClient(rpc) {
    if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function")
        throw new TypeError("A desktop RPC client is required.");
    const invoke = (command, args, options) => rpc.invoke(command, args, options);
    const subscribe = (event, handler, options) => rpc.subscribe(event, handler, options);
    const dialog = (kind, value) => ({ kind, ...value });
    const closeHandlers = new Set();
    let closeSubscription;
    let closeSetup;
    let closeOptions;
    async function dispatchClose(value) {
        let prevented = false;
        const event = Object.freeze({
            reason: value.reason,
            canCancel: value.canCancel,
            preventDefault: () => { if (value.canCancel)
                prevented = true; },
        });
        for (const handler of [...closeHandlers]) {
            try {
                await handler(event);
            }
            catch {
                prevented = value.canCancel;
            }
        }
        await invoke(desktopCommands.window.completeClose, { requestId: value.requestId, preventDefault: prevented }, closeOptions);
    }
    async function onCloseRequested(handler, options) {
        if (typeof handler !== "function")
            throw new TypeError("A window close-request handler is required.");
        closeHandlers.add(handler);
        let setup;
        try {
            if (closeSetup === undefined) {
                closeOptions = options;
                closeSubscription = subscribe(desktopCommands.window.closeRequested, value => { void dispatchClose(value).catch(() => { }); }, options);
                closeSetup = (async () => {
                    await closeSubscription;
                    const enabled = await invoke(desktopCommands.window.interceptClose, { value: true }, options);
                    if (enabled.status !== "Success")
                        throw new Error(`Could not intercept window close requests: ${enabled.status}`);
                })();
            }
            setup = closeSetup;
            await setup;
        }
        catch (error) {
            closeHandlers.delete(handler);
            if (closeSetup === setup) {
                const failedSubscription = closeSubscription;
                closeSubscription = undefined;
                closeSetup = undefined;
                closeOptions = undefined;
                if (failedSubscription !== undefined) {
                    try {
                        await (await failedSubscription)();
                    }
                    catch { /* Preserve the setup failure. */ }
                }
            }
            throw error;
        }
        let listening = true;
        return async () => {
            if (!listening)
                return;
            listening = false;
            closeHandlers.delete(handler);
            if (closeHandlers.size !== 0 || closeSubscription === undefined)
                return;
            await closeSetup;
            const unsubscribe = await closeSubscription;
            closeSubscription = undefined;
            closeSetup = undefined;
            try {
                await invoke(desktopCommands.window.interceptClose, { value: false }, closeOptions);
            }
            finally {
                await unsubscribe();
                closeOptions = undefined;
            }
        };
    }
    const windowState = (options) => invoke(desktopCommands.window.getState, {}, options);
    const onWindowStateChanged = (handler, options) => subscribe(desktopCommands.window.stateChanged, event => handler(event.value), options);
    async function attachTitleBar(options = {}) {
        const root = options.root ?? document.documentElement;
        let snapshot;
        let disposed = false;
        const apply = (value) => {
            if (disposed)
                return;
            snapshot = value;
            root.dataset.neoastraTitlebar = value.titleBar.style.toLowerCase();
            root.dataset.neoastraWindowState = value.state.toLowerCase();
            root.dataset.neoastraWindowFocused = String(value.focused);
            root.style.setProperty("--neoastra-titlebar-height", `${value.titleBar.height}px`);
            root.style.setProperty("--neoastra-titlebar-inset-left", `${value.titleBar.leftInset}px`);
            root.style.setProperty("--neoastra-titlebar-inset-right", `${value.titleBar.rightInset}px`);
            try {
                options.onChange?.(value);
            }
            catch { /* A listener failure must not stop layout updates. */ }
        };
        // Subscribe before the first query so no transition is lost between the two.
        const unsubscribe = await onWindowStateChanged(apply, options.rpc);
        let removeRegions;
        try {
            apply((await windowState(options.rpc)).value);
            removeRegions = installDragRegions(root.ownerDocument, () => snapshot, {
                drag: () => invoke(desktopCommands.window.startDrag, {}, options.rpc),
                toggleMaximize: maximized => invoke(maximized ? desktopCommands.window.restore : desktopCommands.window.maximize, {}, options.rpc),
            });
        }
        catch (error) {
            disposed = true;
            await unsubscribe();
            throw error;
        }
        return Object.freeze({
            get snapshot() { return snapshot; },
            dispose: async () => {
                if (disposed)
                    return;
                disposed = true;
                removeRegions();
                delete root.dataset.neoastraTitlebar;
                delete root.dataset.neoastraWindowState;
                delete root.dataset.neoastraWindowFocused;
                for (const name of ["height", "inset-left", "inset-right"])
                    root.style.removeProperty(`--neoastra-titlebar-${name}`);
                await unsubscribe();
            },
        });
    }
    return Object.freeze({
        dialogs: Object.freeze({
            openFile: (value, options) => invoke(desktopCommands.dialogs.openFile, dialog("openFile", value), options),
            openFolder: (value, options) => invoke(desktopCommands.dialogs.openFolder, dialog("openFolder", value), options),
            saveFile: (value, options) => invoke(desktopCommands.dialogs.saveFile, dialog("saveFile", value), options),
            message: (value, options) => invoke(desktopCommands.dialogs.message, { kind: "message", ...value }, options),
        }),
        menus: Object.freeze({
            activate: (id, options) => invoke(desktopCommands.menus.activate, { id }, options),
            set: (targetId, items, options) => invoke(desktopCommands.menus.set, { targetId, items }, options),
            popup: (targetId, x, y, options) => invoke(desktopCommands.menus.popup, { targetId, x, y }, options),
        }),
        tray: Object.freeze({
            create: (value, options) => invoke(desktopCommands.tray.create, value, options),
            update: (value, options) => invoke(desktopCommands.tray.update, value, options),
            remove: (id, options) => invoke(desktopCommands.tray.remove, { id }, options),
            onActivated: (handler, options) => subscribe(desktopCommands.tray.activated, handler, options),
        }),
        clipboard: Object.freeze({
            read: (format, options) => invoke(format === "text" ? desktopCommands.clipboard.readText : desktopCommands.clipboard.readRich, { format, operation: "read" }, options),
            write: (format, base64, options) => invoke(format === "text" ? desktopCommands.clipboard.writeText : desktopCommands.clipboard.writeRich, { format, operation: "write", base64 }, options),
            clear: (options) => invoke(desktopCommands.clipboard.clear, { format: "all", operation: "write" }, options),
        }),
        notifications: Object.freeze({
            status: (options) => invoke(desktopCommands.notifications.status, {}, options),
            show: (value, options) => invoke(desktopCommands.notifications.show, value, options),
            remove: (id, options) => invoke(desktopCommands.notifications.remove, { id }, options),
            onActivated: (handler, options) => subscribe(desktopCommands.notifications.activated, handler, options),
            /** Schedules a notification that outlives the page and the application, until it is delivered or unscheduled. */
            schedule: (value, options) => invoke(desktopCommands.notifications.schedule, value, options),
            /** Cancels a scheduled notification, also one that an earlier page or run scheduled. */
            unschedule: (id, options) => invoke(desktopCommands.notifications.unschedule, { id }, options),
            /** Asks the user to allow notifications when the system has not asked yet; call it from an action of the user. */
            requestPermission: (options) => invoke(desktopCommands.notifications.requestPermission, {}, options),
            /** Takes, once, the activation of a scheduled notification that came while no page listened, as the tap that launched the application. */
            takeLaunchActivation: async (options) => (await invoke(desktopCommands.notifications.launchActivation, {}, options)).activation ?? undefined,
        }),
        shortcuts: Object.freeze({
            register: (id, accelerator, options) => invoke(desktopCommands.shortcuts.register, { id, accelerator }, options),
            unregister: (id, options) => invoke(desktopCommands.shortcuts.unregister, { id }, options),
            onActivated: (handler, options) => subscribe(desktopCommands.shortcuts.activated, handler, options),
        }),
        system: Object.freeze({
            theme: (options) => invoke(desktopCommands.system.theme, {}, options),
            displays: (options) => invoke(desktopCommands.system.displays, {}, options),
            metadata: (options) => invoke(desktopCommands.system.metadata, {}, options),
            onThemeChanged: (handler, options) => subscribe(desktopCommands.system.themeChanged, handler, options),
            onDisplaysChanged: (handler, options) => subscribe(desktopCommands.system.displaysChanged, handler, options),
        }),
        opener: Object.freeze({
            url: (url, options) => invoke(desktopCommands.opener.url, { url }, options),
            file: (path, intent, options) => invoke(desktopCommands.opener.file, { ...path, operation: "open", intent }, options),
            reveal: (path, options) => invoke(desktopCommands.opener.reveal, { ...path, operation: "reveal" }, options),
        }),
        dragDrop: Object.freeze({
            outbound: (viewLabel, items, options) => invoke(desktopCommands.dragDrop.outbound, { viewLabel, items }, options),
            resolveFile: (token, options) => invoke(desktopCommands.dragDrop.resolveFile, { token }, options),
            onInbound: (handler, options) => subscribe(desktopCommands.dragDrop.inbound, handler, options),
        }),
        safeStorage: Object.freeze({
            store: (id, base64, options) => invoke(desktopCommands.safeStorage.store, { id, base64 }, options),
            retrieve: (id, options) => invoke(desktopCommands.safeStorage.retrieve, { id }, options),
            contains: (id, options) => invoke(desktopCommands.safeStorage.contains, { id }, options),
            delete: (id, options) => invoke(desktopCommands.safeStorage.delete, { id }, options),
        }),
        haptics: Object.freeze({
            /** Plays haptic feedback on a phone; a desktop answers `Unsupported`. */
            play: (feedback, options) => invoke(desktopCommands.haptics.play, { feedback }, options),
        }),
        window: Object.freeze({
            state: windowState,
            onStateChanged: onWindowStateChanged,
            setTitle: (value, options) => invoke(desktopCommands.window.setTitle, { value }, options),
            setPosition: (x, y, options) => invoke(desktopCommands.window.setPosition, { x, y }, options),
            setSize: (width, height, options) => invoke(desktopCommands.window.setSize, { width, height }, options),
            setMinimumSize: (width, height, options) => invoke(desktopCommands.window.setMinimumSize, { width, height }, options),
            setMaximumSize: (width, height, options) => invoke(desktopCommands.window.setMaximumSize, { width, height }, options),
            show: (options) => invoke(desktopCommands.window.show, {}, options),
            hide: (options) => invoke(desktopCommands.window.hide, {}, options),
            focus: (options) => invoke(desktopCommands.window.focus, {}, options),
            maximize: (options) => invoke(desktopCommands.window.maximize, {}, options),
            minimize: (options) => invoke(desktopCommands.window.minimize, {}, options),
            restore: (options) => invoke(desktopCommands.window.restore, {}, options),
            setFullscreen: (value, options) => invoke(desktopCommands.window.setFullscreen, { value }, options),
            setDecorations: (value, options) => invoke(desktopCommands.window.setDecorations, { value }, options),
            setResizable: (value, options) => invoke(desktopCommands.window.setResizable, { value }, options),
            setAlwaysOnTop: (value, options) => invoke(desktopCommands.window.setAlwaysOnTop, { value }, options),
            setTaskbarVisible: (value, options) => invoke(desktopCommands.window.setTaskbarVisible, { value }, options),
            setTitleBar: (value, options) => invoke(desktopCommands.window.setTitleBar, { style: value.style, height: value.height ?? 0, symbolColor: value.symbolColor, backgroundColor: value.backgroundColor }, options),
            startDrag: (options) => invoke(desktopCommands.window.startDrag, {}, options),
            startResize: (edge, options) => invoke(desktopCommands.window.startResize, { edge }, options),
            attachTitleBar,
            close: (options) => invoke(desktopCommands.window.close, {}, options),
            onCloseRequested,
            setIcon: (path, options) => invoke(desktopCommands.window.setIcon, { ...path, operation: "read" }, options),
            setRepresentedFile: (path, options) => invoke(desktopCommands.window.setRepresentedFile, path === undefined ? { operation: "read" } : { ...path, operation: "read" }, options),
            extraSupport: (options) => invoke(desktopCommands.window.getExtraSupport, {}, options),
            requestAttention: (critical = false, options) => invoke(desktopCommands.window.requestAttention, { value: critical }, options),
            setProgress: (state, value, options) => invoke(desktopCommands.window.setProgress, { state, value }, options),
            setBadge: (value, options) => invoke(desktopCommands.window.setBadge, { value }, options),
            setDocumentEdited: (value, options) => invoke(desktopCommands.window.setDocumentEdited, { value }, options),
            setContentProtection: (value, options) => invoke(desktopCommands.window.setContentProtection, { value }, options),
            setTitleBarTheme: (theme, options) => invoke(desktopCommands.window.setTitleBarTheme, { theme }, options),
            /** Sets the color that the window shows before and behind the page, as `#rrggbb` or `#rrggbbaa`; on Android the symbols of the system bars follow it. */
            setBackgroundColor: (color, options) => invoke(desktopCommands.window.setBackgroundColor, { color }, options),
        }),
        application: Object.freeze({
            requestQuit: (options) => invoke(desktopCommands.application.requestQuit, {}, options),
        }),
    });
}
//# sourceMappingURL=desktop.js.map