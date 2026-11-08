import { NeoRpcClient, NeoRpcError } from "./rpc.js";
import { createDesktopClient } from "./desktop.js";
import { createUpdateClient } from "./updates.js";
import { DEFAULT_MAXIMUM_FRAME_BYTES, NeoAstraClientError, PROTOCOL_MAJOR, PROTOCOL_MINOR, SUPPORTED_FEATURES, assertInboundFrame, checkOutboundFrame, describeRejectedFrame, isRecord, parseCloseReason, } from "./shared.js";
const defaultScheduler = {
    setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
    clearTimeout: handle => globalThis.clearTimeout(handle),
};
export function createMockClient(options = {}) {
    return new MockClient(options);
}
class MockClient {
    options;
    scheduler;
    diagnostics = new Set();
    mutableOutboundFrames = [];
    currentRuntimeInfo;
    handshake;
    connection;
    active = true;
    constructor(options) {
        this.options = options;
        this.scheduler = options.scheduler ?? defaultScheduler;
    }
    get outboundFrames() { return this.mutableOutboundFrames; }
    isAvailable() { return this.active; }
    getRuntimeInfo() { return this.currentRuntimeInfo; }
    connect(connectOptions = {}) {
        if (!this.active)
            return Promise.reject(new NeoAstraClientError("transport_unavailable", "The mock document is no longer active."));
        if (this.connection?.state === "connected")
            return Promise.resolve(this.connection);
        if (this.handshake !== undefined)
            return this.handshake;
        const timeout = connectOptions.handshakeTimeoutMilliseconds ?? 10_000;
        if (!Number.isFinite(timeout) || timeout <= 0 || timeout > 600_000) {
            return Promise.reject(new TypeError("handshakeTimeoutMilliseconds must be greater than zero and no more than ten minutes."));
        }
        const delay = this.options.connectDelayMilliseconds ?? 0;
        this.mutableOutboundFrames.push(Object.freeze({
            neoastra: 1,
            kind: "hello",
            protocol: Object.freeze({ major: PROTOCOL_MAJOR, minor: PROTOCOL_MINOR }),
            features: SUPPORTED_FEATURES,
            client: Object.freeze({ name: "@neoastra/client", version: "0.1.0" }),
        }));
        this.handshake = new Promise((resolve, reject) => {
            this.scheduler.setTimeout(() => {
                if (!this.active) {
                    reject(new NeoAstraClientError("connection_closed", "The mock document was replaced."));
                    return;
                }
                if (delay > timeout) {
                    this.handshake = undefined;
                    reject(new NeoAstraClientError("handshake_timeout", "The mock transport handshake timed out.", true));
                    return;
                }
                if ((this.options.protocolMajor ?? PROTOCOL_MAJOR) !== PROTOCOL_MAJOR) {
                    reject(new NeoAstraClientError("protocol_mismatch", "The mock host protocol is incompatible."));
                    return;
                }
                const id = this.options.idFactory?.() ?? "mock-document-session";
                this.currentRuntimeInfo = makeRuntimeInfo(this.options, id);
                this.connection = new MockConnection(this.currentRuntimeInfo, this.options.maximumFrameBytes ?? DEFAULT_MAXIMUM_FRAME_BYTES, this.options.maximumJsonDepth ?? 32, frame => this.mutableOutboundFrames.push(frame), diagnostic => this.emitDiagnostic(diagnostic));
                resolve(this.connection);
            }, Math.min(delay, timeout));
        });
        return this.handshake;
    }
    onDiagnostic(listener) {
        this.diagnostics.add(listener);
        return () => this.diagnostics.delete(listener);
    }
    injectInbound(frame) {
        if (!isRecord(frame) || frame.neoastra !== 1 || typeof frame.kind !== "string") {
            this.injectMalformed();
            return;
        }
        this.connection?.accept(Object.freeze({ ...frame }));
    }
    injectMalformed() {
        this.emitDiagnostic({ level: "warning", code: "invalid_frame", message: "The mock host injected a malformed frame." });
    }
    close(reason) {
        this.active = false;
        this.connection?.hostClose(parseCloseReason(reason));
    }
    navigate(overrides = {}) {
        this.close("navigation");
        return new MockClient({ ...this.options, ...overrides });
    }
    emitDiagnostic(value) {
        const frozen = Object.freeze({ ...value });
        for (const listener of this.diagnostics) {
            try {
                listener(frozen);
            }
            catch { }
        }
    }
}
class MockConnection {
    runtimeInfo;
    maximumFrameBytes;
    maximumJsonDepth;
    record;
    diagnose;
    closed;
    controller = new AbortController();
    currentState = "connected";
    reason;
    receiveHandler;
    rejectedHandler;
    constructor(runtimeInfo, maximumFrameBytes, maximumJsonDepth, record, diagnose) {
        this.runtimeInfo = runtimeInfo;
        this.maximumFrameBytes = maximumFrameBytes;
        this.maximumJsonDepth = maximumJsonDepth;
        this.record = record;
        this.diagnose = diagnose;
        this.closed = this.controller.signal;
    }
    get state() { return this.currentState; }
    get closeReason() { return this.reason; }
    hasFeature(feature) { return this.runtimeInfo.negotiatedFeatures.includes(feature); }
    send(frame) {
        if (this.currentState !== "connected")
            throw new NeoAstraClientError("connection_closed", "The mock connection is closed.");
        checkOutboundFrame(frame, this.maximumFrameBytes, this.maximumJsonDepth);
        this.record(Object.freeze({ ...frame }));
    }
    setReceiveHandler(handler, rejected) {
        if (this.receiveHandler !== undefined)
            throw new NeoAstraClientError("internal_transport_error", "A receive handler is already registered.");
        this.receiveHandler = handler;
        this.rejectedHandler = rejected;
        return () => {
            if (this.receiveHandler !== handler)
                return;
            this.receiveHandler = undefined;
            this.rejectedHandler = undefined;
        };
    }
    close() {
        if (this.currentState !== "connected")
            return;
        this.currentState = "closing";
        this.record(Object.freeze({ neoastra: 1, kind: "close" }));
        this.finishClose("client_close");
    }
    accept(frame) {
        if (this.currentState !== "connected")
            return;
        if (frame.kind === "close") {
            this.hostClose(parseCloseReason(frame.reason));
            return;
        }
        if (frame.kind === "diagnostic") {
            const level = frame.level;
            if ((level === "debug" || level === "information" || level === "warning" || level === "error") &&
                typeof frame.code === "string" && typeof frame.message === "string") {
                this.diagnose({ level, code: frame.code, message: frame.message, correlationId: typeof frame.correlationId === "string" ? frame.correlationId : undefined });
            }
            return;
        }
        try {
            assertInboundFrame(frame, this.maximumJsonDepth);
        }
        catch (error) {
            const value = error instanceof NeoAstraClientError
                ? error
                : new NeoAstraClientError("internal_transport_error", "The mock transport failed to validate an inbound frame.");
            this.diagnose({ level: "warning", code: value.code, message: value.message, correlationId: value.correlationId });
            try {
                this.rejectedHandler?.(describeRejectedFrame(frame, value));
            }
            catch { }
            return;
        }
        try {
            this.receiveHandler?.(frame);
        }
        catch (error) {
            const value = error instanceof NeoAstraClientError
                ? error
                : new NeoAstraClientError("internal_transport_error", "The mock transport failed to validate an inbound frame.");
            this.diagnose({ level: "warning", code: value.code, message: value.message, correlationId: value.correlationId });
        }
    }
    hostClose(reason) {
        if (this.currentState === "closed")
            return;
        this.diagnose({ level: "information", code: "connection_closed", message: `The mock host closed the connection (${reason}).` });
        this.finishClose(reason);
    }
    finishClose(reason) {
        if (this.currentState === "closed")
            return;
        this.currentState = "closed";
        this.reason = reason;
        this.receiveHandler = undefined;
        this.rejectedHandler = undefined;
        this.controller.abort(new NeoAstraClientError("connection_closed", "The mock connection is closed."));
    }
}
function makeRuntimeInfo(options, id) {
    const source = options.runtimeInfo ?? {};
    return Object.freeze({
        available: true,
        protocolMajor: source.protocolMajor ?? PROTOCOL_MAJOR,
        protocolMinor: source.protocolMinor ?? PROTOCOL_MINOR,
        negotiatedFeatures: Object.freeze([...(options.negotiatedFeatures ?? source.negotiatedFeatures ?? SUPPORTED_FEATURES)]),
        viewLabel: source.viewLabel ?? "mock",
        documentSessionId: source.documentSessionId ?? id,
        platform: source.platform ?? "windows",
        backend: source.backend ?? "webview2",
        wholeViewTrust: source.wholeViewTrust ?? false,
    });
}
export function createMockDesktop() {
    const results = new Map();
    const invocations = [];
    const subscriptions = new Map();
    const rpc = {
        invoke: async (command, args) => {
            invocations.push(Object.freeze({ command, args }));
            if (!results.has(command))
                throw new NeoRpcError({ code: "permission_denied", message: "The mock command has no explicit result.", retryable: false });
            return results.get(command);
        },
        subscribe: async (event, handler) => {
            let values = subscriptions.get(event);
            if (values === undefined) {
                values = new Set();
                subscriptions.set(event, values);
            }
            const untyped = handler;
            values.add(untyped);
            return async () => { values.delete(untyped); };
        },
    };
    return {
        client: createDesktopClient(rpc),
        invocations,
        setResult: (command, result) => results.set(command, result),
        emit: (event, value) => { for (const handler of subscriptions.get(event) ?? []) {
            try {
                handler(value);
            }
            catch { }
        } },
    };
}
/** Deny-by-default mock for the experimental safe update renderer surface. */
export function createMockUpdates() {
    const results = new Map();
    const invocations = [];
    const subscriptions = new Map();
    const rpc = {
        invoke: async (command, args) => {
            invocations.push(Object.freeze({ command, args }));
            if (!results.has(command))
                throw new NeoRpcError({ code: "permission_denied", message: "The mock update command has no explicit result.", retryable: false });
            return results.get(command);
        },
        subscribe: async (event, handler) => {
            let values = subscriptions.get(event);
            if (values === undefined) {
                values = new Set();
                subscriptions.set(event, values);
            }
            const untyped = handler;
            values.add(untyped);
            return async () => { values.delete(untyped); };
        },
    };
    return {
        client: createUpdateClient(rpc), invocations,
        setResult: (command, result) => results.set(command, result),
        emit: (event, value) => { for (const handler of subscriptions.get(event) ?? []) {
            try {
                handler(value);
            }
            catch { }
        } },
    };
}
export function createMockRpcHarness(options = {}) {
    const handlers = new Map();
    const subscriptions = new Map();
    const invocations = new Map();
    const frames = [];
    let receiver;
    const closed = new AbortController();
    const runtimeInfo = makeRuntimeInfo(options, options.idFactory?.() ?? "mock-rpc-session");
    const connection = {
        runtimeInfo,
        state: "connected",
        closed: closed.signal,
        hasFeature: feature => runtimeInfo.negotiatedFeatures.includes(feature),
        setReceiveHandler(handler) {
            if (receiver !== undefined)
                throw new NeoAstraClientError("internal_transport_error", "A receive handler is already registered.");
            receiver = handler;
            return () => { if (receiver === handler)
                receiver = undefined; };
        },
        send(frame) {
            checkOutboundFrame(frame, options.maximumFrameBytes ?? DEFAULT_MAXIMUM_FRAME_BYTES, options.maximumJsonDepth ?? 32);
            const value = Object.freeze({ ...frame });
            frames.push(value);
            queueMicrotask(() => dispatch(value));
        },
        close() { if (!closed.signal.aborted)
            closed.abort(); },
    };
    const client = new NeoRpcClient(connection, { idFactory: options.idFactory });
    async function dispatch(frame) {
        if (closed.signal.aborted || typeof frame.kind !== "string")
            return;
        if (frame.kind === "invoke" && typeof frame.id === "string" && typeof frame.command === "string") {
            const controller = new AbortController();
            invocations.set(frame.id, controller);
            const handler = handlers.get(frame.command);
            if (handler === undefined) {
                receiver?.(resultError(frame.id, { code: "command_not_found", message: "The mock command is not registered.", retryable: false }));
                invocations.delete(frame.id);
                return;
            }
            try {
                const value = await handler({ command: frame.command, args: frame.args, signal: controller.signal });
                if (!controller.signal.aborted)
                    receiver?.(Object.freeze({ neoastra: 1, kind: "result", id: frame.id, ok: true, value }));
            }
            catch (error) {
                const mapped = error instanceof NeoRpcError
                    ? { code: error.code, message: error.message, retryable: error.retryable, correlationId: error.correlationId }
                    : { code: "internal_error", message: "The mock command failed.", retryable: false };
                receiver?.(resultError(frame.id, mapped));
            }
            finally {
                invocations.delete(frame.id);
            }
        }
        else if (frame.kind === "cancel" && typeof frame.id === "string") {
            invocations.get(frame.id)?.abort();
        }
        else if (frame.kind === "subscribe" && typeof frame.id === "string" && typeof frame.event === "string") {
            subscriptions.set(frame.id, { event: frame.event, sequence: 0 });
            receiver?.(Object.freeze({ neoastra: 1, kind: "subscribed", id: frame.id }));
        }
        else if (frame.kind === "unsubscribe" && typeof frame.id === "string") {
            subscriptions.delete(frame.id);
        }
    }
    return {
        client,
        outboundFrames: frames,
        register(command, handler) {
            if (typeof command !== "string" || command.length === 0)
                throw new TypeError("A command is required.");
            if (typeof handler !== "function")
                throw new TypeError("A handler is required.");
            if (handlers.has(command))
                throw new TypeError("The mock command is already registered.");
            handlers.set(command, handler);
            return () => { if (handlers.get(command) === handler)
                handlers.delete(command); };
        },
        emit(event, value) {
            for (const [id, subscription] of subscriptions) {
                if (subscription.event !== event)
                    continue;
                receiver?.(Object.freeze({ neoastra: 1, kind: "event", subscription: id, sequence: ++subscription.sequence, value }));
            }
        },
        close() {
            for (const controller of invocations.values())
                controller.abort();
            invocations.clear();
            subscriptions.clear();
            connection.close();
            client.close();
        },
    };
}
function resultError(id, error) {
    return Object.freeze({ neoastra: 1, kind: "result", id, ok: false, error: Object.freeze({ ...error }) });
}
//# sourceMappingURL=testing.js.map