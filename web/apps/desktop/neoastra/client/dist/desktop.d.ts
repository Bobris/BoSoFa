import type { NeoRpcCallOptions, NeoRpcUnsubscribe } from "./rpc.js";
export type DesktopStatus = "Success" | "Canceled" | "Unsupported" | "Denied" | "NotFound" | "NoHandler" | "Conflict" | "Locked" | "Corrupt" | "LimitExceeded" | "Failed";
export interface DesktopResult {
    readonly status: DesktopStatus;
    readonly code?: string;
}
export interface DesktopValueResult<T> extends DesktopResult {
    readonly value?: T;
}
export interface DesktopBytesResult extends DesktopResult {
    readonly base64?: string;
}
export interface DesktopPathResult extends DesktopResult {
    readonly path?: string;
}
export interface DesktopPathsResult extends DesktopResult {
    readonly paths?: readonly string[];
}
export interface DesktopRpc {
    invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult>;
    subscribe<T>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe>;
}
export interface DesktopDialogFilter {
    readonly name: string;
    readonly extensions: readonly string[];
    readonly mimeTypes: readonly string[];
}
export interface DesktopFileDialogRequest {
    readonly initialLocation: string;
    readonly initialRelativePath?: string;
    readonly extensions: readonly string[];
    readonly title?: string;
    readonly suggestedFileName?: string;
    readonly allowMultiple: boolean;
    readonly filters: readonly DesktopDialogFilter[];
}
export interface DesktopMessageRequest {
    readonly title?: string;
    readonly message: string;
    readonly detail?: string;
    readonly icon: "None" | "Information" | "Warning" | "Error" | "Question";
    readonly buttons: readonly string[];
}
export interface DesktopMenuItem {
    readonly id: string;
    readonly kind: "Command" | "Submenu" | "Separator" | "Role";
    readonly text?: string;
    readonly commandId?: string;
    readonly accelerator?: string;
    readonly enabled: boolean;
    readonly visible: boolean;
    readonly checked: boolean;
    readonly role?: string;
    readonly children: readonly DesktopMenuItem[];
}
export interface DesktopTrayRequest {
    readonly id: string;
    readonly toolTip?: string;
    readonly isTemplateImage: boolean;
    readonly menu: readonly DesktopMenuItem[];
}
export interface DesktopNotificationRequest {
    readonly appIdentity: string;
    readonly category: string;
    readonly urgency: string;
    readonly persistent: boolean;
    readonly payload?: string;
    readonly id: string;
    readonly title: string;
    readonly body: string;
    readonly actions: readonly {
        readonly id: string;
        readonly title: string;
    }[];
}
export interface DesktopScheduledNotificationRequest extends DesktopNotificationRequest {
    /** The moment of delivery, ISO 8601 with its offset, such as `2026-11-02T08:00:00+01:00`. */
    readonly deliverAt: string;
}
export interface DesktopNotificationActivation {
    readonly id: string;
    readonly actionId?: string;
    readonly payload?: string;
}
export interface DesktopScopedPath {
    readonly root: string;
    readonly relativePath: string;
}
export interface DesktopOutboundDragItem {
    readonly kind: "Text" | "File" | "Url";
    readonly value?: string;
    readonly root?: string;
    readonly relativePath?: string;
}
export type DesktopWindowState = "Normal" | "Minimized" | "Maximized" | "Fullscreen";
export type DesktopWindowCloseReason = "User" | "Owner" | "ApplicationQuit" | "SessionEnd" | "System" | "Programmatic" | "Automated";
export interface DesktopWindowSnapshot {
    readonly title: string;
    readonly position: {
        readonly x: number;
        readonly y: number;
    };
    readonly size: {
        readonly width: number;
        readonly height: number;
    };
    readonly minimumSize: {
        readonly width: number;
        readonly height: number;
    };
    readonly maximumSize: {
        readonly width: number;
        readonly height: number;
    };
    readonly visible: boolean;
    readonly focused: boolean;
    readonly closed: boolean;
    readonly scaleFactor: number;
    readonly state: DesktopWindowState;
    readonly decorations: boolean;
    readonly resizable: boolean;
    readonly alwaysOnTop: boolean;
    readonly taskbarVisible?: boolean;
    readonly modal: boolean;
    readonly titleBar: DesktopWindowTitleBarLayout;
    /** The `id` of the display of the window among those of `system.displays()`, or null when the native library tells none. */
    readonly displayId?: string | null;
}
export type DesktopWindowTitleBarStyle = "Default" | "Overlay" | "Hidden";
export type DesktopWindowResizeEdge = "Left" | "Top" | "Right" | "Bottom" | "TopLeft" | "TopRight" | "BottomLeft" | "BottomRight";
/** Title-bar space in CSS pixels. The insets are the widths covered by native window controls and are zero while none are shown. */
export interface DesktopWindowTitleBarLayout {
    readonly style: DesktopWindowTitleBarStyle;
    readonly height: number;
    readonly leftInset: number;
    readonly rightInset: number;
}
/** Colors use the `#RRGGBB` or `#RRGGBBAA` form. An omitted symbol color follows the system theme; an omitted background is transparent. */
export interface DesktopWindowTitleBarOptions {
    readonly style: DesktopWindowTitleBarStyle;
    readonly height?: number;
    readonly symbolColor?: string;
    readonly backgroundColor?: string;
}
export interface DesktopTitleBarAttachOptions {
    /** Element receiving the layout attributes and custom properties. Defaults to the document element. */
    readonly root?: HTMLElement;
    /** Called with the initial snapshot and after every native state, focus, or title-bar change. */
    readonly onChange?: (snapshot: DesktopWindowSnapshot) => void;
    readonly rpc?: NeoRpcCallOptions;
}
export interface DesktopTitleBarBinding {
    /** The most recent window snapshot. */
    readonly snapshot: DesktopWindowSnapshot;
    dispose(): Promise<void>;
}
export interface DesktopWindowCloseRequestedEvent {
    readonly reason: DesktopWindowCloseReason;
    readonly canCancel: boolean;
    preventDefault(): void;
}
export type DesktopSupportLevel = "None" | "Native" | "Emulated" | "Limited";
export interface DesktopSupportInfo {
    readonly supportLevel: DesktopSupportLevel;
    readonly details?: string;
}
export interface DesktopWindowExtraSupport {
    readonly attention: DesktopSupportInfo;
    readonly progress: DesktopSupportInfo;
    readonly badge: DesktopSupportInfo;
    readonly document: DesktopSupportInfo;
    readonly contentProtection: DesktopSupportInfo;
    readonly titleBarTheme: DesktopSupportInfo;
}
export declare const desktopCommands: Readonly<{
    readonly dialogs: Readonly<{
        openFile: "desktop.dialogs.open-file";
        openFolder: "desktop.dialogs.open-folder";
        saveFile: "desktop.dialogs.save-file";
        message: "desktop.dialogs.message";
    }>;
    readonly menus: Readonly<{
        activate: "desktop.menus.activate";
        set: "desktop.menus.set";
        popup: "desktop.menus.popup";
    }>;
    readonly tray: Readonly<{
        create: "desktop.tray.create";
        update: "desktop.tray.update";
        remove: "desktop.tray.remove";
        activated: "desktop.tray.activated";
    }>;
    readonly clipboard: Readonly<{
        readText: "desktop.clipboard.read-text";
        writeText: "desktop.clipboard.write-text";
        readRich: "desktop.clipboard.read-rich";
        writeRich: "desktop.clipboard.write-rich";
        clear: "desktop.clipboard.clear";
    }>;
    readonly notifications: Readonly<{
        status: "desktop.notifications.status";
        show: "desktop.notifications.show";
        remove: "desktop.notifications.remove";
        activated: "desktop.notifications.activated";
        schedule: "desktop.notifications.schedule";
        unschedule: "desktop.notifications.unschedule";
        requestPermission: "desktop.notifications.request-permission";
        launchActivation: "desktop.notifications.launch-activation";
    }>;
    readonly shortcuts: Readonly<{
        register: "desktop.shortcuts.register";
        unregister: "desktop.shortcuts.unregister";
        activated: "desktop.shortcuts.activated";
    }>;
    readonly system: Readonly<{
        theme: "desktop.system.theme";
        displays: "desktop.system.displays";
        metadata: "desktop.system.metadata";
        themeChanged: "desktop.system.theme-changed";
        displaysChanged: "desktop.system.displays-changed";
    }>;
    readonly opener: Readonly<{
        url: "desktop.opener.url";
        file: "desktop.opener.file";
        reveal: "desktop.opener.reveal";
    }>;
    readonly dragDrop: Readonly<{
        outbound: "desktop.drag-drop.outbound";
        resolveFile: "desktop.drag-drop.resolve-file";
        inbound: "desktop.drag-drop.inbound";
    }>;
    readonly safeStorage: Readonly<{
        store: "desktop.safe-storage.store";
        retrieve: "desktop.safe-storage.retrieve";
        delete: "desktop.safe-storage.delete";
        contains: "desktop.safe-storage.contains";
    }>;
    readonly haptics: Readonly<{
        play: "desktop.haptics.play";
    }>;
    readonly window: Readonly<{
        getState: "desktop.window.get-state";
        setTitle: "desktop.window.set-title";
        setPosition: "desktop.window.set-position";
        setSize: "desktop.window.set-size";
        setMinimumSize: "desktop.window.set-minimum-size";
        setMaximumSize: "desktop.window.set-maximum-size";
        show: "desktop.window.show";
        hide: "desktop.window.hide";
        focus: "desktop.window.focus";
        maximize: "desktop.window.maximize";
        minimize: "desktop.window.minimize";
        restore: "desktop.window.restore";
        setFullscreen: "desktop.window.set-fullscreen";
        setDecorations: "desktop.window.set-decorations";
        setResizable: "desktop.window.set-resizable";
        setAlwaysOnTop: "desktop.window.set-always-on-top";
        setTaskbarVisible: "desktop.window.set-taskbar-visible";
        close: "desktop.window.close";
        interceptClose: "desktop.window.intercept-close";
        completeClose: "desktop.window.complete-close";
        closeRequested: "desktop.window.close-requested";
        setTitleBar: "desktop.window.set-title-bar";
        startDrag: "desktop.window.start-drag";
        startResize: "desktop.window.start-resize";
        stateChanged: "desktop.window.state-changed";
        setIcon: "desktop.window.set-icon";
        setRepresentedFile: "desktop.window.set-represented-file";
        getExtraSupport: "desktop.window.get-extra-support";
        requestAttention: "desktop.window.request-attention";
        setProgress: "desktop.window.set-progress";
        setBadge: "desktop.window.set-badge";
        setDocumentEdited: "desktop.window.set-document-edited";
        setContentProtection: "desktop.window.set-content-protection";
        setTitleBarTheme: "desktop.window.set-titlebar-theme";
        setBackgroundColor: "desktop.window.set-background-color";
    }>;
    readonly application: Readonly<{
        requestQuit: "desktop.application.request-quit";
    }>;
}>;
export declare function createDesktopClient(rpc: DesktopRpc): Readonly<{
    dialogs: Readonly<{
        openFile: (value: DesktopFileDialogRequest, options?: NeoRpcCallOptions) => Promise<DesktopPathsResult>;
        openFolder: (value: DesktopFileDialogRequest, options?: NeoRpcCallOptions) => Promise<DesktopPathsResult>;
        saveFile: (value: DesktopFileDialogRequest, options?: NeoRpcCallOptions) => Promise<DesktopPathResult>;
        message: (value: DesktopMessageRequest, options?: NeoRpcCallOptions) => Promise<DesktopValueResult<string>>;
    }>;
    menus: Readonly<{
        activate: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        set: (targetId: string, items: readonly DesktopMenuItem[], options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        popup: (targetId: string, x: number, y: number, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
    tray: Readonly<{
        create: (value: DesktopTrayRequest, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        update: (value: DesktopTrayRequest, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        remove: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        onActivated: (handler: (value: {
            readonly id: string;
            readonly secondary: boolean;
        }) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
    }>;
    clipboard: Readonly<{
        read: (format: "text" | "html" | "image" | "files", options?: NeoRpcCallOptions) => Promise<DesktopBytesResult>;
        write: (format: "text" | "html" | "image" | "files", base64: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        clear: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
    notifications: Readonly<{
        status: (options?: NeoRpcCallOptions) => Promise<{
            readonly status: string;
        }>;
        show: (value: DesktopNotificationRequest, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        remove: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        onActivated: (handler: (value: DesktopNotificationActivation) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
        /** Schedules a notification that outlives the page and the application, until it is delivered or unscheduled. */
        schedule: (value: DesktopScheduledNotificationRequest, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        /** Cancels a scheduled notification, also one that an earlier page or run scheduled. */
        unschedule: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        /** Asks the user to allow notifications when the system has not asked yet; call it from an action of the user. */
        requestPermission: (options?: NeoRpcCallOptions) => Promise<{
            readonly status: string;
        }>;
        /** Takes, once, the activation of a scheduled notification that came while no page listened, as the tap that launched the application. */
        takeLaunchActivation: (options?: NeoRpcCallOptions) => Promise<DesktopNotificationActivation | undefined>;
    }>;
    shortcuts: Readonly<{
        register: (id: string, accelerator: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        unregister: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        onActivated: (handler: (value: {
            readonly id: string;
        }) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
    }>;
    system: Readonly<{
        theme: (options?: NeoRpcCallOptions) => Promise<unknown>;
        displays: (options?: NeoRpcCallOptions) => Promise<unknown>;
        metadata: (options?: NeoRpcCallOptions) => Promise<unknown>;
        onThemeChanged: (handler: (value: unknown) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
        onDisplaysChanged: (handler: (value: unknown) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
    }>;
    opener: Readonly<{
        url: (url: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        file: (path: DesktopScopedPath, intent: "OpenDocument" | "OpenContainingApplication", options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        reveal: (path: DesktopScopedPath, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
    dragDrop: Readonly<{
        outbound: (viewLabel: string, items: readonly DesktopOutboundDragItem[], options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        resolveFile: (token: string, options?: NeoRpcCallOptions) => Promise<DesktopPathResult>;
        onInbound: (handler: (value: unknown) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
    }>;
    safeStorage: Readonly<{
        store: (id: string, base64: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        retrieve: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopBytesResult>;
        contains: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopValueResult<boolean>>;
        delete: (id: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
    haptics: Readonly<{
        /** Plays haptic feedback on a phone; a desktop answers `Unsupported`. */
        play: (feedback: "Light" | "Medium" | "Heavy" | "Selection" | "Success" | "Warning" | "Error", options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
    window: Readonly<{
        state: (options?: NeoRpcCallOptions) => Promise<{
            readonly value: DesktopWindowSnapshot;
        }>;
        onStateChanged: (handler: (value: DesktopWindowSnapshot) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
        setTitle: (value: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setPosition: (x: number, y: number, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setSize: (width: number, height: number, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setMinimumSize: (width: number, height: number, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setMaximumSize: (width: number, height: number, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        show: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        hide: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        focus: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        maximize: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        minimize: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        restore: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setFullscreen: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setDecorations: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setResizable: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setAlwaysOnTop: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setTaskbarVisible: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setTitleBar: (value: DesktopWindowTitleBarOptions, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        startDrag: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        startResize: (edge: DesktopWindowResizeEdge, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        attachTitleBar: (options?: DesktopTitleBarAttachOptions) => Promise<DesktopTitleBarBinding>;
        close: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        onCloseRequested: (handler: (event: DesktopWindowCloseRequestedEvent) => void | Promise<void>, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
        setIcon: (path: DesktopScopedPath, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setRepresentedFile: (path?: DesktopScopedPath, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        extraSupport: (options?: NeoRpcCallOptions) => Promise<DesktopWindowExtraSupport>;
        requestAttention: (critical?: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setProgress: (state: "None" | "Normal" | "Paused" | "Error" | "Indeterminate", value: number, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setBadge: (value?: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setDocumentEdited: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setContentProtection: (value: boolean, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        setTitleBarTheme: (theme: "System" | "Light" | "Dark", options?: NeoRpcCallOptions) => Promise<DesktopResult>;
        /** Sets the color that the window shows before and behind the page, as `#rrggbb` or `#rrggbbaa`; on Android the symbols of the system bars follow it. */
        setBackgroundColor: (color: string, options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
    application: Readonly<{
        requestQuit: (options?: NeoRpcCallOptions) => Promise<DesktopResult>;
    }>;
}>;
export type NeoDesktopClient = ReturnType<typeof createDesktopClient>;
//# sourceMappingURL=desktop.d.ts.map