import { type NeoAstraConnectionState, type NeoAstraRejectedFrame } from "./shared.js";
export { NeoAstraClientError } from "./shared.js";
export type { NeoAstraConnectionState, NeoAstraRejectedFrame } from "./shared.js";
export interface NeoAstraRuntimeInfo {
    readonly available: true;
    readonly protocolMajor: number;
    readonly protocolMinor: number;
    readonly negotiatedFeatures: readonly string[];
    readonly viewLabel: string;
    readonly documentSessionId: string;
    readonly platform: "windows" | "macos" | "linux" | "ios" | "android";
    readonly backend: "webview2" | "wkwebview" | "webkitgtk" | "android-webview";
    readonly wholeViewTrust: boolean;
}
export interface NeoAstraTransportDiagnostic {
    readonly level: "debug" | "information" | "warning" | "error";
    readonly code: string;
    readonly message: string;
    readonly correlationId?: string;
}
export interface ConnectOptions {
    readonly handshakeTimeoutMilliseconds?: number;
}
export interface NeoAstraConnection {
    readonly runtimeInfo: NeoAstraRuntimeInfo;
    readonly state: NeoAstraConnectionState;
    readonly closed: AbortSignal;
    /**
     * Why the connection closed, set before `closed` aborts: `"client_close"` after `close()`, otherwise the
     * host's reason, such as `"navigation"` or `"rpc_session_closed"`. A document cannot connect twice, so a
     * host close of a document that keeps running is recovered from by reloading it.
     */
    readonly closeReason?: string;
    hasFeature(feature: string): boolean;
    send(frame: unknown): void;
    /**
     * Registers the single receiver of application frames. `rejected`, when given, is told about an inbound frame
     * the client refused (for example deeper than the negotiated JSON depth), with its top-level identity, so the
     * call, channel or subscription it answers can fail instead of waiting forever; the frame itself is dropped.
     */
    setReceiveHandler(handler: (frame: Readonly<Record<string, unknown>>) => void, rejected?: (frame: NeoAstraRejectedFrame) => void): () => void;
    close(): void;
}
export declare function isAvailable(): boolean;
export declare function getRuntimeInfo(): NeoAstraRuntimeInfo | undefined;
export declare function onDiagnostic(listener: (value: NeoAstraTransportDiagnostic) => void): () => void;
export declare function connect(options?: ConnectOptions): Promise<NeoAstraConnection>;
export { NeoRpcClient, NeoRpcError, invoke, invokeChannel, rpcClient, subscribe, } from "./rpc.js";
export type { NeoRpcCallOptions, NeoRpcClientOptions, NeoRpcErrorValue, NeoRpcEventHandler, NeoRpcUnsubscribe, } from "./rpc.js";
export { createDesktopClient, desktopCommands } from "./desktop.js";
export type { DesktopBytesResult, DesktopDialogFilter, DesktopFileDialogRequest, DesktopMenuItem, DesktopMessageRequest, DesktopNotificationRequest, DesktopOutboundDragItem, DesktopPathResult, DesktopPathsResult, DesktopResult, DesktopRpc, DesktopScopedPath, DesktopStatus, DesktopSupportInfo, DesktopSupportLevel, DesktopTitleBarAttachOptions, DesktopTitleBarBinding, DesktopTrayRequest, DesktopValueResult, DesktopWindowExtraSupport, DesktopWindowResizeEdge, DesktopWindowSnapshot, DesktopWindowState, DesktopWindowTitleBarLayout, DesktopWindowTitleBarOptions, DesktopWindowTitleBarStyle, NeoDesktopClient, } from "./desktop.js";
export { keyboardHeight, onKeyboard } from "./mobile.js";
export type { NeoKeyboardState } from "./mobile.js";
export { createUpdateClient, updateCommands } from "./updates.js";
export type { NeoUpdateClient, NeoUpdateMode, NeoUpdatePhase, NeoUpdateRpc, NeoUpdateStatus } from "./updates.js";
//# sourceMappingURL=index.d.ts.map