import type { NeoRpcCallOptions, NeoRpcUnsubscribe } from "./rpc.js";
/** Truthful updater availability. `available` is intentionally absent until artifact-specific qualification exists. */
export type NeoUpdateMode = "unavailable" | "experimental" | "store-managed";
export type NeoUpdatePhase = "idle" | "checking" | "available" | "downloading" | "ready" | "installing" | "rolled-back" | "failed";
export interface NeoUpdateStatus {
    readonly mode: NeoUpdateMode;
    readonly phase: NeoUpdatePhase;
    readonly currentVersion: string;
    readonly availableVersion?: string;
    readonly progressPercent?: number;
    readonly canInstall: boolean;
    readonly code?: string;
}
export interface NeoUpdateRpc {
    invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult>;
    subscribe<T>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe>;
}
export declare const updateCommands: Readonly<{
    readonly status: "updates.status";
    readonly check: "updates.check";
    readonly download: "updates.download";
    readonly install: "updates.install-restart";
    readonly changed: "updates.changed";
}>;
/**
 * Creates the bounded renderer update surface. Feed URLs, keys, channels, versions, artifact paths,
 * helper commands, and rollback targets are deliberately not accepted from renderer code.
 */
export declare function createUpdateClient(rpc: NeoUpdateRpc): Readonly<{
    status: (options?: NeoRpcCallOptions) => Promise<NeoUpdateStatus>;
    check: (options?: NeoRpcCallOptions) => Promise<NeoUpdateStatus>;
    download: (options?: NeoRpcCallOptions) => Promise<NeoUpdateStatus>;
    /** Requires a separately granted high-risk backend permission and trusted user confirmation. */
    installAndRestart: (options?: NeoRpcCallOptions) => Promise<NeoUpdateStatus>;
    onChanged: (handler: (value: NeoUpdateStatus) => void, options?: NeoRpcCallOptions) => Promise<NeoRpcUnsubscribe>;
}>;
export type NeoUpdateClient = ReturnType<typeof createUpdateClient>;
//# sourceMappingURL=updates.d.ts.map