import { liveSocketUrl, type LiveClientMessage, type LiveServerMessage, type RuntimeRequest, type Session, type SessionItem } from "@bosofa/api";
import { createStore, onSettled, reconcile } from "solid-js";

export type Draft = { ref: string; text: string };

/** A command can print for ten minutes; a page keeps the end of it. */
const MAX_DRAFT_LENGTH = 128 * 1024;

/** A draft with this kind of ref is the output of a running tool, not the agent's answer. */
export const isToolOutput = (draft: Draft) => draft.ref.startsWith("tool-");

/** A draft with this kind of ref is reasoning the model is doing right now. */
export const isThinking = (draft: Draft) => draft.ref.startsWith("thinking-");

export type LiveSessionState = {
  session: Session | undefined;
  items: SessionItem[];
  requests: RuntimeRequest[];
  /** Streamed assistant text not yet persisted as an item, one entry per ref. */
  drafts: Draft[];
  connected: boolean;
  error: string | undefined;
};

/**
 * Follows one session over `/api/v1/live`: missing items after the last seen seq, the pending requests and live
 * text deltas. Reconnects with the last seq as its cursor, so nothing is lost while the connection is down.
 * Call from a component body: the socket opens once the component settles and closes with it.
 */
export function createLiveSession(sessionId: number) {
  const [state, setState] = createStore<LiveSessionState>({
    session: undefined,
    items: [],
    requests: [],
    drafts: [],
    connected: false,
    error: undefined,
  });
  let lastSeq = 0;

  onSettled(() => {
    let socket: WebSocket | undefined;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let closed = false;
    let backoff = 500;

    const connect = async () => {
      let url: string;
      try {
        url = await liveSocketUrl();
      } catch {
        if (!closed) retry = setTimeout(() => void connect(), 2000);
        return;
      }
      if (closed) return;
      const ws = new WebSocket(url);
      socket = ws;
      ws.onopen = () => {
        backoff = 500;
        setState((s) => {
          s.connected = true;
        });
        const subscribe: LiveClientMessage = { t: "subscribe", sessionId, afterSeq: lastSeq };
        ws.send(JSON.stringify(subscribe));
      };
      ws.onmessage = (event) => receive(JSON.parse(event.data as string) as LiveServerMessage);
      ws.onclose = () => {
        setState((s) => {
          s.connected = false;
          s.drafts = []; // deltas missed meanwhile cannot be recovered; the final items will come
        });
        if (closed) return;
        retry = setTimeout(() => void connect(), backoff);
        backoff = Math.min(backoff * 2, 10_000);
      };
    };

    const receive = (message: LiveServerMessage) => {
      switch (message.t) {
        case "session": {
          const fresh = message.items.filter((item) => Number(item.seq) > lastSeq);
          if (fresh.length > 0) lastSeq = Number(fresh[fresh.length - 1].seq);
          const refs = new Set(fresh.map((item) => item.ref).filter((ref) => ref != null));
          setState((s) => {
            // Merge, so only readers of the fields that changed re-run.
            if (s.session) reconcile(message.session, "id")(s.session);
            else s.session = message.session;
            reconcile(message.requests, "id")(s.requests);
            s.items.push(...fresh);
            if (refs.size > 0) s.drafts = s.drafts.filter((draft) => !refs.has(draft.ref));
            s.error = undefined;
          });
          break;
        }
        case "delta": {
          if (state.items.some((item) => item.ref === message.ref)) break; // arrived after its final item
          setState((s) => {
            const draft = s.drafts.find((d) => d.ref === message.ref);
            if (draft && message.reset) draft.text = message.text;
            else if (draft) draft.text = (draft.text + message.text).slice(-MAX_DRAFT_LENGTH);
            else s.drafts.push({ ref: message.ref, text: message.text });
          });
          break;
        }
        case "error":
          setState((s) => {
            s.error = message.message;
          });
          break;
      }
    };

    void connect();
    return () => {
      closed = true;
      clearTimeout(retry);
      socket?.close();
    };
  });

  return state;
}
