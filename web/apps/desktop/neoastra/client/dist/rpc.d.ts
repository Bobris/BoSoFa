import { type NeoAstraConnection } from "./index.js";
export interface NeoRpcCallOptions {
    /** Cancels an invocation, an opened channel observation, or a pending subscription handshake. */
    readonly signal?: AbortSignal;
    /** Bounds invocation completion or subscription acknowledgement to at most ten minutes. */
    readonly timeoutMilliseconds?: number;
    /** Requires the host to accept this generated contract hash. */
    readonly contractHash?: string;
}
export interface NeoRpcErrorValue {
    readonly code: string;
    readonly message: string;
    readonly correlationId?: string;
    /** Advisory only: does not prove backend work was never admitted. Calls are never automatically replayed. */
    readonly retryable: boolean;
}
export declare class NeoRpcError extends Error implements NeoRpcErrorValue {
    readonly code: string;
    readonly correlationId?: string;
    readonly retryable: boolean;
    constructor(value: NeoRpcErrorValue);
}
export type NeoRpcEventHandler<T> = (value: T) => void;
export type NeoRpcUnsubscribe = () => Promise<void>;
export interface NeoRpcClientOptions {
    readonly idFactory?: () => string;
    readonly maximumBufferedChannelItems?: number;
}
export declare class NeoRpcClient {
    readonly connection: NeoAstraConnection;
    private readonly options;
    private readonly pending;
    private readonly pendingSubscriptions;
    private readonly subscriptions;
    private readonly channels;
    private readonly abandoned;
    private readonly removeReceive;
    private readonly connectionClosed;
    private detached;
    private counter;
    private closed;
    private readonly maximumBufferedChannelItems;
    constructor(connection: NeoAstraConnection, options?: NeoRpcClientOptions);
    invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult>;
    subscribe<T>(event: string, handler: NeoRpcEventHandler<T>, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe>;
    /** Opens a channel and retains signal cancellation until its terminal state. The timeout bounds opening only. */
    invokeChannel<TRequest, TItem>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<AsyncIterable<TItem>>;
    /** Claims a channel. Aborting stops observation, discards buffered items, and asks the host to close the channel. */
    channel<T>(id: string, signal?: AbortSignal): AsyncIterable<T>;
    closeResource(id: string): void;
    close(): void;
    private receive;
    private receiveResult;
    private receiveSubscribed;
    private receiveEvent;
    private receiveChannelItem;
    private receiveChannelTerminal;
    private receiveRejected;
    private failChannel;
    private failAll;
    private detach;
    private abandon;
    private cleanupChannel;
    private nextId;
    private ensureChannel;
}
export declare function rpcClient(): Promise<NeoRpcClient>;
export declare function invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult>;
export declare function subscribe<T>(event: string, handler: NeoRpcEventHandler<T>, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe>;
export declare function invokeChannel<TRequest, TItem>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<AsyncIterable<TItem>>;
//# sourceMappingURL=rpc.d.ts.map