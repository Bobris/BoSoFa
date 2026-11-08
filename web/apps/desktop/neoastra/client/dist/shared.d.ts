export declare const PROTOCOL_MAJOR = 1;
export declare const PROTOCOL_MINOR = 0;
export declare const SUPPORTED_FEATURES: readonly ["invoke", "cancel", "events"];
export declare const HARD_MAXIMUM_FRAME_BYTES: number;
export declare const DEFAULT_MAXIMUM_FRAME_BYTES: number;
export declare const DEFAULT_MAXIMUM_JSON_DEPTH = 32;
export declare const DEFAULT_DIAGNOSTIC_QUEUE_LIMIT = 100;
export declare const DEFAULT_HANDSHAKE_TIMEOUT_MILLISECONDS = 10000;
export type NeoAstraConnectionState = "unavailable" | "discovering" | "handshaking" | "connected" | "closing" | "closed" | "failed";
export declare class NeoAstraClientError extends Error {
    readonly code: string;
    readonly correlationId?: string;
    readonly retryable: boolean;
    constructor(code: string, message: string, retryable?: boolean, correlationId?: string);
}
/** Tells whether a string takes more than `maximumBytes` in UTF-8, encoding it only when its length cannot decide. */
export declare function exceedsUtf8Bytes(value: string, maximumBytes: number): boolean;
/**
 * Checks a frame the page sends and returns its JSON, made once and handed to the bootstrap with the frame.
 * The host reads the envelope around the frame with the same depth limit, so the frame keeps one level for it.
 */
export declare function checkOutboundFrame(value: unknown, maximumFrameBytes: number, maximumJsonDepth?: number): string;
/**
 * Checks a frame the host sent, whose own depth is limited by the negotiated depth. Its size is not measured
 * again: the host bounds every envelope it sends by the negotiated byte limit before posting it.
 */
export declare function assertInboundFrame(value: unknown, maximumJsonDepth?: number): asserts value is Record<string, unknown>;
/**
 * Tells whether a value nests more than `maximumDepth` objects and arrays, counted as System.Text.Json counts
 * `MaxDepth` on the host: each object or array is one level, and a primitive value adds none. The walk is
 * recursive and allocates nothing; it stops at the limit, so a cyclic value is reported as too deep. An object
 * that serializes through `toJSON`, such as a `Date`, counts as one level, which can only be stricter.
 */
export declare function exceedsJsonDepth(value: unknown, maximumDepth: number): boolean;
/**
 * The top-level identity of an inbound frame the client refused, given to the receive handler's rejection
 * callback so that the call, channel or subscription it answers fails instead of waiting forever.
 */
export interface NeoAstraRejectedFrame {
    readonly kind: string;
    readonly id?: string;
    readonly channel?: string;
    readonly subscription?: string;
    readonly sequence?: number;
    readonly error: NeoAstraClientError;
}
export declare function describeRejectedFrame(value: Record<string, unknown>, error: NeoAstraClientError): NeoAstraRejectedFrame;
/** Reads the bounded reason token of a host close frame; anything else is reported as "unspecified". */
export declare function parseCloseReason(value: unknown): string;
export declare function isRecord(value: unknown): value is Record<string, unknown>;
//# sourceMappingURL=shared.d.ts.map