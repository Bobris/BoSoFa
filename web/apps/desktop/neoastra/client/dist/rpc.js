import { NeoAstraClientError, connect } from "./index.js";
import { isRecord } from "./shared.js";
export class NeoRpcError extends Error {
    code;
    correlationId;
    retryable;
    constructor(value) {
        super(value.message);
        this.name = "NeoRpcError";
        this.code = value.code;
        this.correlationId = value.correlationId;
        this.retryable = value.retryable;
    }
}
export class NeoRpcClient {
    connection;
    options;
    pending = new Map();
    pendingSubscriptions = new Map();
    subscriptions = new Map();
    channels = new Map();
    // Invocations that stopped waiting (abort or timeout) before their result. The host may have committed a
    // channel first: its late result is then closed at once, since nothing will observe it. Oldest first out.
    abandoned = new Set();
    removeReceive;
    connectionClosed;
    detached = false;
    counter = 0;
    closed = false;
    maximumBufferedChannelItems;
    constructor(connection, options = {}) {
        this.connection = connection;
        this.options = options;
        if (connection.state !== "connected")
            throw new NeoAstraClientError("connection_closed", "RPC requires a connected transport.");
        const maximum = options.maximumBufferedChannelItems ?? 64;
        if (!Number.isSafeInteger(maximum) || maximum < 1 || maximum > 4096)
            throw new RangeError("maximumBufferedChannelItems must be between 1 and 4096.");
        this.maximumBufferedChannelItems = maximum;
        this.removeReceive = connection.setReceiveHandler(frame => this.receive(frame), frame => this.receiveRejected(frame));
        this.connectionClosed = () => this.failAll(new NeoRpcError({ code: "connection_closed", message: "The RPC connection closed.", retryable: true }));
        connection.closed.addEventListener("abort", this.connectionClosed, { once: true });
        if (connection.closed.aborted || connection.state !== "connected") {
            this.detach();
            throw new NeoAstraClientError("connection_closed", "The RPC connection closed while RPC was attaching.");
        }
    }
    invoke(command, args, options = {}) {
        assertWireName(command, "command");
        assertContractHash(options.contractHash);
        if (options.signal?.aborted)
            return Promise.reject(abortError());
        const id = this.nextId("req");
        return new Promise((resolve, reject) => {
            let timer;
            // An abort and a timeout both cancel the call on the host; only their error differs.
            const stop = (error) => {
                if (!this.pending.delete(id))
                    return;
                try {
                    this.connection.send(Object.freeze({ neoastra: 1, kind: "cancel", id }));
                }
                catch { }
                this.abandon(id);
                cleanup();
                reject(error);
            };
            const abort = () => stop(abortError());
            const cleanup = () => {
                options.signal?.removeEventListener("abort", abort);
                if (timer !== undefined)
                    clearTimeout(timer);
            };
            this.pending.set(id, { resolve: value => resolve(value), reject, cleanup });
            options.signal?.addEventListener("abort", abort, { once: true });
            if (options.signal?.aborted) {
                abort();
                return;
            }
            if (options.timeoutMilliseconds !== undefined) {
                if (!Number.isFinite(options.timeoutMilliseconds) || options.timeoutMilliseconds <= 0 || options.timeoutMilliseconds > 600_000) {
                    this.pending.delete(id);
                    cleanup();
                    reject(new TypeError("timeoutMilliseconds must be greater than zero and no more than ten minutes."));
                    return;
                }
                timer = setTimeout(() => stop(new NeoRpcError({ code: "timeout", message: "The RPC invocation timed out.", retryable: true })), options.timeoutMilliseconds);
            }
            try {
                this.connection.send(Object.freeze({ neoastra: 1, kind: "invoke", id, command, args, contract: options.contractHash }));
            }
            catch (error) {
                this.pending.delete(id);
                cleanup();
                reject(normalize(error));
            }
        });
    }
    subscribe(event, handler, options = {}) {
        assertWireName(event, "event");
        assertContractHash(options.contractHash);
        if (typeof handler !== "function")
            return Promise.reject(new TypeError("An event handler is required."));
        if (options.signal?.aborted)
            return Promise.reject(abortError());
        const id = this.nextId("sub");
        return new Promise((resolve, reject) => {
            let timer;
            const cleanup = () => {
                options.signal?.removeEventListener("abort", abort);
                if (timer !== undefined)
                    clearTimeout(timer);
            };
            const fail = (error) => {
                if (!this.pendingSubscriptions.delete(id))
                    return;
                try {
                    this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id }));
                }
                catch { }
                cleanup();
                reject(error);
            };
            const abort = () => fail(abortError());
            if (options.timeoutMilliseconds !== undefined && (!Number.isFinite(options.timeoutMilliseconds) || options.timeoutMilliseconds <= 0 || options.timeoutMilliseconds > 600_000)) {
                reject(new TypeError("timeoutMilliseconds must be greater than zero and no more than ten minutes."));
                return;
            }
            options.signal?.addEventListener("abort", abort, { once: true });
            this.pendingSubscriptions.set(id, {
                resolve: unsubscribe => { cleanup(); resolve(unsubscribe); },
                reject: error => { cleanup(); reject(error); },
                handler: value => handler(value),
            });
            if (options.signal?.aborted) {
                abort();
                return;
            }
            if (options.timeoutMilliseconds !== undefined)
                timer = setTimeout(() => fail(new NeoRpcError({ code: "timeout", message: "The RPC subscription acknowledgement timed out.", retryable: true })), options.timeoutMilliseconds);
            try {
                this.connection.send(Object.freeze({ neoastra: 1, kind: "subscribe", id, event, contract: options.contractHash }));
            }
            catch (error) {
                this.pendingSubscriptions.delete(id);
                cleanup();
                reject(normalize(error));
            }
        });
    }
    /** Opens a channel and retains signal cancellation until its terminal state. The timeout bounds opening only. */
    async invokeChannel(command, args, options = {}) {
        const result = await this.invoke(command, args, options);
        return this.channel(result.channel, options.signal);
    }
    /** Claims a channel. Aborting stops observation, discards buffered items, and asks the host to close the channel. */
    channel(id, signal) {
        if (this.closed)
            throw new NeoRpcError({ code: "connection_closed", message: "The RPC client is closed.", retryable: false });
        assertOpaqueId(id, "channel ID");
        const state = this.ensureChannel(id);
        const client = this;
        if (signal !== undefined && !state.closed) {
            const abort = () => {
                if (state.closed)
                    return;
                state.closed = true;
                state.error = abortError();
                state.values.length = 0;
                this.channels.delete(id);
                this.cleanupChannel(state);
                for (const waiter of state.waiters.splice(0))
                    waiter.reject(state.error);
                try {
                    this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: id }));
                }
                catch { }
            };
            (state.cleanups ??= []).push(() => signal.removeEventListener("abort", abort));
            signal.addEventListener("abort", abort, { once: true });
            if (signal.aborted)
                abort();
        }
        return {
            [Symbol.asyncIterator]() {
                return {
                    async next() {
                        if (state.values.length !== 0)
                            return state.values.shift();
                        if (state.error !== undefined) {
                            client.channels.delete(id);
                            throw state.error;
                        }
                        if (state.closed) {
                            client.channels.delete(id);
                            return { done: true, value: undefined };
                        }
                        return new Promise((resolve, reject) => state.waiters.push({ resolve, reject }));
                    },
                    async return() {
                        const notifyHost = !state.closed;
                        state.closed = true;
                        state.values.length = 0;
                        delete state.error;
                        client.channels.delete(id);
                        client.cleanupChannel(state);
                        for (const waiter of state.waiters.splice(0))
                            waiter.resolve({ done: true, value: undefined });
                        if (notifyHost) {
                            try {
                                client.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: id }));
                            }
                            catch { }
                        }
                        return { done: true, value: undefined };
                    },
                };
            },
        };
    }
    closeResource(id) {
        assertOpaqueId(id, "resource ID");
        this.connection.send(Object.freeze({ neoastra: 1, kind: "resource_close", resource: id }));
    }
    close() {
        if (this.closed)
            return;
        this.closed = true;
        for (const id of this.pending.keys()) {
            try {
                this.connection.send(Object.freeze({ neoastra: 1, kind: "cancel", id }));
            }
            catch { }
        }
        for (const id of this.pendingSubscriptions.keys()) {
            try {
                this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id }));
            }
            catch { }
        }
        for (const id of this.subscriptions.keys()) {
            try {
                this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id }));
            }
            catch { }
        }
        for (const [id, state] of this.channels) {
            if (!state.closed) {
                try {
                    this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: id }));
                }
                catch { }
            }
        }
        this.detach();
        this.failAll(new NeoRpcError({ code: "connection_closed", message: "The RPC client closed.", retryable: false }));
    }
    receive(frame) {
        switch (frame.kind) {
            case "result":
                this.receiveResult(frame);
                break;
            case "subscribed":
                this.receiveSubscribed(frame);
                break;
            case "event":
                this.receiveEvent(frame);
                break;
            case "channel_item":
                this.receiveChannelItem(frame);
                break;
            case "channel_complete":
                this.receiveChannelTerminal(frame);
                break;
            case "channel_error":
                this.receiveChannelTerminal(frame);
                break;
        }
    }
    receiveResult(frame) {
        if (typeof frame.id !== "string")
            return;
        const pending = this.pending.get(frame.id);
        if (pending === undefined) {
            if (this.abandoned.delete(frame.id) && frame.ok === true && isChannelResult(frame.value)) {
                try {
                    this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: frame.value.channel }));
                }
                catch { }
            }
            return;
        }
        this.pending.delete(frame.id);
        pending.cleanup();
        if (frame.ok === true) {
            if (isRecord(frame.value) && typeof frame.value.channel === "string" && Object.keys(frame.value).length === 1) {
                try {
                    this.ensureChannel(frame.value.channel);
                }
                catch {
                    pending.reject(new NeoRpcError({ code: "invalid_request", message: "The host returned an invalid channel ID.", retryable: false }));
                    return;
                }
            }
            pending.resolve(frame.value);
        }
        else
            pending.reject(parseError(frame.error));
    }
    receiveSubscribed(frame) {
        if (typeof frame.id !== "string")
            return;
        const pending = this.pendingSubscriptions.get(frame.id);
        if (pending === undefined)
            return;
        this.pendingSubscriptions.delete(frame.id);
        if (frame.error !== undefined) {
            pending.reject(parseError(frame.error));
            return;
        }
        this.subscriptions.set(frame.id, { handler: pending.handler, sequence: 0 });
        pending.resolve(async () => {
            if (!this.subscriptions.delete(frame.id))
                return;
            this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id: frame.id }));
        });
    }
    receiveEvent(frame) {
        if (typeof frame.subscription !== "string" || !Number.isSafeInteger(frame.sequence))
            return;
        const state = this.subscriptions.get(frame.subscription);
        if (state === undefined || frame.sequence !== state.sequence + 1)
            return;
        state.sequence = frame.sequence;
        try {
            state.handler(frame.value);
        }
        catch { }
    }
    receiveChannelItem(frame) {
        if (typeof frame.channel !== "string" || !Number.isSafeInteger(frame.sequence))
            return;
        const state = this.channels.get(frame.channel);
        if (state === undefined || state.closed || frame.sequence !== state.sequence + 1)
            return;
        state.sequence = frame.sequence;
        const value = { done: false, value: frame.value };
        const waiter = state.waiters.shift();
        if (waiter !== undefined)
            waiter.resolve(value);
        else if (state.values.length < this.maximumBufferedChannelItems)
            state.values.push(value);
        else {
            this.failChannel(frame.channel, state, new NeoRpcError({ code: "too_many_requests", message: "The frontend channel buffer limit was exhausted.", retryable: false }), true);
            return;
        }
        try {
            this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_ack", channel: frame.channel, sequence: frame.sequence }));
        }
        catch { }
    }
    receiveChannelTerminal(frame) {
        if (typeof frame.channel !== "string")
            return;
        const state = this.channels.get(frame.channel);
        if (state === undefined || state.closed)
            return;
        state.closed = true;
        this.cleanupChannel(state);
        if (frame.kind === "channel_error")
            state.error = parseError(frame.error);
        const terminal = { done: true, value: undefined };
        for (const waiter of state.waiters.splice(0)) {
            if (state.error !== undefined)
                waiter.reject(state.error);
            else
                waiter.resolve(terminal);
        }
    }
    // The transport dropped a frame it refused: fail what it answers rather than leave it waiting for it.
    receiveRejected(frame) {
        const error = new NeoRpcError({ code: frame.error.code, message: frame.error.message, retryable: false });
        switch (frame.kind) {
            case "result": {
                const pending = frame.id === undefined ? undefined : this.pending.get(frame.id);
                if (pending === undefined)
                    return;
                this.pending.delete(frame.id);
                pending.cleanup();
                pending.reject(error);
                break;
            }
            case "subscribed": {
                const pending = frame.id === undefined ? undefined : this.pendingSubscriptions.get(frame.id);
                if (pending === undefined)
                    return;
                this.pendingSubscriptions.delete(frame.id);
                try {
                    this.connection.send(Object.freeze({ neoastra: 1, kind: "unsubscribe", id: frame.id }));
                }
                catch { }
                pending.reject(error);
                break;
            }
            case "event": {
                // An event has no error path: it is skipped, and the next events of the subscription keep their sequence.
                const state = frame.subscription === undefined ? undefined : this.subscriptions.get(frame.subscription);
                if (state !== undefined && frame.sequence === state.sequence + 1)
                    state.sequence = frame.sequence;
                break;
            }
            case "channel_item":
            case "channel_complete":
            case "channel_error": {
                const state = frame.channel === undefined ? undefined : this.channels.get(frame.channel);
                if (state === undefined || state.closed)
                    return;
                // An item leaves the host channel waiting for its acknowledgement: ask the host to close it.
                this.failChannel(frame.channel, state, error, frame.kind === "channel_item");
                break;
            }
        }
    }
    failChannel(id, state, error, notifyHost) {
        state.error = error;
        state.closed = true;
        this.cleanupChannel(state);
        for (const pending of state.waiters.splice(0))
            pending.reject(error);
        if (notifyHost) {
            try {
                this.connection.send(Object.freeze({ neoastra: 1, kind: "channel_close", channel: id }));
            }
            catch { }
        }
    }
    failAll(error) {
        this.closed = true;
        this.detach();
        for (const pending of this.pending.values()) {
            pending.cleanup();
            pending.reject(error);
        }
        for (const pending of this.pendingSubscriptions.values())
            pending.reject(error);
        for (const state of this.channels.values()) {
            state.error = error;
            state.closed = true;
            state.values.length = 0;
            this.cleanupChannel(state);
            for (const waiter of state.waiters.splice(0))
                waiter.reject(error);
        }
        this.pending.clear();
        this.pendingSubscriptions.clear();
        this.subscriptions.clear();
        this.channels.clear();
        this.abandoned.clear();
    }
    detach() {
        if (this.detached)
            return;
        this.detached = true;
        this.connection.closed.removeEventListener("abort", this.connectionClosed);
        this.removeReceive();
    }
    abandon(id) {
        if (this.abandoned.size === maximumAbandonedInvocations)
            this.abandoned.delete(this.abandoned.values().next().value);
        this.abandoned.add(id);
    }
    cleanupChannel(state) {
        for (const cleanup of state.cleanups?.splice(0) ?? [])
            cleanup();
    }
    nextId(prefix) {
        if (this.closed)
            throw new NeoRpcError({ code: "connection_closed", message: "The RPC client is closed.", retryable: false });
        const generated = this.options.idFactory?.() ?? `${prefix}-${Date.now().toString(36)}-${(++this.counter).toString(36)}`;
        assertOpaqueId(generated, "generated RPC ID");
        return generated;
    }
    ensureChannel(id) {
        assertOpaqueId(id, "channel ID");
        let state = this.channels.get(id);
        if (state === undefined) {
            state = { values: [], waiters: [], sequence: 0, closed: false };
            this.channels.set(id, state);
        }
        return state;
    }
}
const maximumAbandonedInvocations = 256;
// A handshake that failed with a retryable error (a handshake timeout) is tried again by the next call, up
// to the host's default number of hello attempts. Each attempt waits for a whole handshake timeout, so
// retries never spin; past the last one, the failure stays cached like a terminal one.
const maximumHandshakeAttempts = 3;
let defaultClient;
let handshakeAttempts = 0;
export async function rpcClient() {
    if (defaultClient === undefined) {
        handshakeAttempts++;
        const attempt = connect().then(connection => new NeoRpcClient(connection));
        defaultClient = attempt;
        attempt.catch((error) => {
            if (defaultClient === attempt && handshakeAttempts < maximumHandshakeAttempts &&
                error instanceof NeoAstraClientError && error.retryable)
                defaultClient = undefined;
        });
    }
    return defaultClient;
}
export async function invoke(command, args, options) {
    return (await rpcClient()).invoke(command, args, options);
}
export async function subscribe(event, handler, options) {
    return (await rpcClient()).subscribe(event, handler, options);
}
export async function invokeChannel(command, args, options) {
    return (await rpcClient()).invokeChannel(command, args, options);
}
function parseError(value) {
    if (!isRecord(value) || typeof value.code !== "string" || !/^[a-z][a-z0-9_]*(?::[a-z][a-z0-9_]*)*$/.test(value.code) || value.code.length > 128 ||
        typeof value.message !== "string" || value.message.trim().length === 0 || value.message.length > 512 || /[\x00-\x1f\x7f-\x9f]/.test(value.message) ||
        typeof value.retryable !== "boolean" || value.correlationId !== undefined && (typeof value.correlationId !== "string" || value.correlationId.length === 0 || value.correlationId.length > 128 || !/^[\x21-\x7e]+$/.test(value.correlationId)))
        return new NeoRpcError({ code: "invalid_request", message: "The host returned an invalid RPC error.", retryable: false });
    return new NeoRpcError({
        code: value.code,
        message: value.message,
        retryable: value.retryable === true,
        correlationId: typeof value.correlationId === "string" ? value.correlationId : undefined,
    });
}
function isChannelResult(value) {
    return isRecord(value) && Object.keys(value).length === 1 && isOpaqueId(value.channel);
}
function abortError() {
    return new NeoRpcError({ code: "operation_canceled", message: "The RPC operation was canceled.", retryable: false });
}
function normalize(value) {
    return value instanceof Error ? value : new NeoRpcError({ code: "connection_closed", message: "The RPC transport failed.", retryable: true });
}
function assertWireName(value, name) {
    if (typeof value !== "string" || value.length === 0 || value.length > 192 || !/^[A-Za-z0-9_][A-Za-z0-9_.:-]*$/.test(value))
        throw new TypeError(`The ${name} wire name is invalid.`);
}
function assertContractHash(value) {
    if (value !== undefined && (value.length === 0 || value.length > 256 || !/^[\x20-\x7e]+$/.test(value)))
        throw new TypeError("The contract hash must be non-empty bounded printable ASCII.");
}
function assertOpaqueId(value, name) {
    if (!isOpaqueId(value))
        throw new TypeError(`The ${name} is invalid.`);
}
function isOpaqueId(value) {
    return typeof value === "string" && value.length !== 0 && value.length <= 128 && /^[\x21-\x7e]+$/.test(value);
}
//# sourceMappingURL=rpc.js.map