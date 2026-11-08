import type { ConnectOptions, NeoAstraConnection, NeoAstraRuntimeInfo, NeoAstraTransportDiagnostic } from "./index.js";
import { NeoRpcClient } from "./rpc.js";
import { createDesktopClient } from "./desktop.js";
export interface MockScheduler {
    setTimeout(callback: () => void, delayMilliseconds: number): unknown;
    clearTimeout(handle: unknown): void;
}
export interface MockTransportOptions {
    readonly runtimeInfo?: Partial<Omit<NeoAstraRuntimeInfo, "available">>;
    readonly negotiatedFeatures?: readonly string[];
    readonly connectDelayMilliseconds?: number;
    readonly protocolMajor?: number;
    readonly maximumFrameBytes?: number;
    readonly maximumJsonDepth?: number;
    readonly scheduler?: MockScheduler;
    readonly idFactory?: () => string;
}
export interface MockNeoAstraClient {
    isAvailable(): boolean;
    getRuntimeInfo(): NeoAstraRuntimeInfo | undefined;
    connect(options?: ConnectOptions): Promise<NeoAstraConnection>;
    onDiagnostic(listener: (value: NeoAstraTransportDiagnostic) => void): () => void;
    readonly outboundFrames: readonly Readonly<Record<string, unknown>>[];
    injectInbound(frame: unknown): void;
    injectMalformed(): void;
    /** Closes the connection as the host would, reporting `reason` (for example `"rpc_session_closed"`) as its `closeReason`. */
    close(reason?: string): void;
    navigate(overrides?: MockTransportOptions): MockNeoAstraClient;
}
export declare function createMockClient(options?: MockTransportOptions): MockNeoAstraClient;
export interface MockRpcInvocation {
    readonly command: string;
    readonly args: unknown;
    readonly signal: AbortSignal;
}
/** A small capability-neutral desktop mock. Applications must opt in each command result explicitly. */
export interface MockDesktopHarness {
    readonly client: ReturnType<typeof createDesktopClient>;
    readonly invocations: readonly {
        readonly command: string;
        readonly args: unknown;
    }[];
    setResult(command: string, result: unknown): void;
    emit(event: string, value: unknown): void;
}
export declare function createMockDesktop(): MockDesktopHarness;
/** Deny-by-default mock for the experimental safe update renderer surface. */
export declare function createMockUpdates(): {
    client: Readonly<{
        status: (options?: import("./rpc.js").NeoRpcCallOptions) => Promise<import("./updates.js").NeoUpdateStatus>;
        check: (options?: import("./rpc.js").NeoRpcCallOptions) => Promise<import("./updates.js").NeoUpdateStatus>;
        download: (options?: import("./rpc.js").NeoRpcCallOptions) => Promise<import("./updates.js").NeoUpdateStatus>;
        installAndRestart: (options?: import("./rpc.js").NeoRpcCallOptions) => Promise<import("./updates.js").NeoUpdateStatus>;
        onChanged: (handler: (value: import("./updates.js").NeoUpdateStatus) => void, options?: import("./rpc.js").NeoRpcCallOptions) => Promise<import("./rpc.js").NeoRpcUnsubscribe>;
    }>;
    invocations: {
        command: string;
        args: unknown;
    }[];
    setResult: (command: string, result: unknown) => Map<string, unknown>;
    emit: (event: string, value: unknown) => void;
};
export type MockRpcHandler = (invocation: MockRpcInvocation) => unknown | Promise<unknown>;
export interface MockRpcHarness {
    readonly client: NeoRpcClient;
    readonly outboundFrames: readonly Readonly<Record<string, unknown>>[];
    register(command: string, handler: MockRpcHandler): () => void;
    emit(event: string, value: unknown): void;
    close(): void;
}
export declare function createMockRpcHarness(options?: MockTransportOptions): MockRpcHarness;
//# sourceMappingURL=testing.d.ts.map