import { api, liveSocketUrl, type LiveClientMessage, type LiveServerMessage, type Terminal } from "@bosofa/api";
import { FitAddon } from "@xterm/addon-fit";
import { Terminal as XTerm } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { createMemo, createSignal, For, onSettled, refresh, Show } from "solid-js";

function fromBase64(data: string): Uint8Array {
  return Uint8Array.from(atob(data), (char) => char.charCodeAt(0));
}

function toBase64(text: string): string {
  let binary = "";
  for (const byte of new TextEncoder().encode(text)) binary += String.fromCharCode(byte);
  return btoa(binary);
}

/** One terminal on screen. Output is identified by its byte offset, so a reconnect shows every byte exactly once. */
export function TerminalView(props: { machineId: number; terminalId: string; onExit: () => void; class?: string }) {
  let element!: HTMLDivElement;

  onSettled(() => {
    const { machineId, terminalId } = props;
    const terminal = new XTerm({ convertEol: false, cursorBlink: true, fontSize: 13, scrollback: 5000 });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(element);
    fit.fit();

    let socket: WebSocket | undefined;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let closed = false;
    /** The next byte expected; null until a snapshot arrived. */
    let expected: number | null = null;
    let early: { offset: number; data: Uint8Array }[] = [];

    const send = (message: LiveClientMessage) => {
      if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
    };
    const subscribe = () => {
      expected = null;
      early = [];
      send({ t: "terminalSubscribe", machineId, terminalId });
    };

    const apply = (offset: number, data: Uint8Array) => {
      if (expected === null) return;
      if (offset > expected) return subscribe(); // output was dropped on the way: start over from a snapshot
      const fresh = data.subarray(Math.min(expected - offset, data.length));
      if (fresh.length === 0) return;
      terminal.write(fresh);
      expected += fresh.length;
    };

    const receive = (message: LiveServerMessage) => {
      switch (message.t) {
        case "terminalSnapshot":
          if (message.terminalId !== terminalId) break;
          terminal.reset();
          terminal.write(fromBase64(message.data));
          expected = Number(message.endOffset);
          for (const chunk of early) apply(chunk.offset, chunk.data);
          early = [];
          if (message.exited) terminal.write(`\r\n[exited with code ${message.exitCode}]\r\n`);
          else send({ t: "terminalResize", terminalId, cols: terminal.cols, rows: terminal.rows });
          break;
        case "terminalOutput": {
          if (message.terminalId !== terminalId) break;
          const data = fromBase64(message.data);
          if (expected === null) early.push({ offset: Number(message.offset), data });
          else apply(Number(message.offset), data);
          break;
        }
        case "terminalExited":
          if (message.terminalId !== terminalId) break;
          terminal.write(`\r\n[${message.message ?? `exited with code ${message.exitCode}`}]\r\n`);
          props.onExit();
          break;
      }
    };

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
      ws.onopen = subscribe;
      ws.onmessage = (event) => receive(JSON.parse(event.data as string) as LiveServerMessage);
      ws.onclose = () => {
        if (!closed) retry = setTimeout(() => void connect(), 1000);
      };
    };

    const input = terminal.onData((data) => send({ t: "terminalInput", terminalId, data: toBase64(data) }));
    const binary = terminal.onBinary((data) =>
      send({ t: "terminalInput", terminalId, data: btoa(data) }),
    );
    const resize = terminal.onResize(({ cols, rows }) => send({ t: "terminalResize", terminalId, cols, rows }));
    const observer = new ResizeObserver(() => fit.fit());
    observer.observe(element);
    void connect();
    terminal.focus();

    return () => {
      closed = true;
      clearTimeout(retry);
      socket?.close();
      observer.disconnect();
      input.dispose();
      binary.dispose();
      resize.dispose();
      terminal.dispose();
    };
  });

  return <div class={[props.class ?? "h-96", "bg-black p-1"]} ref={(value) => (element = value)} />;
}

/** The machine's terminals: open, watch together, close. */
export default function Terminals(props: {
  machineId: number;
  ref?: (controls: { open: (directory?: string) => void }) => void;
}) {
  const id = () => ({ path: { id: props.machineId } });
  const [selected, setSelected] = createSignal<string | null>(null);
  const [notice, setNotice] = createSignal<string>();
  const terminals = createMemo(async (): Promise<Terminal[]> => {
    const { data } = await api.GET("/api/v1/machines/{id}/terminals", { params: id() });
    return data?.terminals ?? [];
  });

  async function open(workingDirectory?: string) {
    setNotice(undefined);
    const { data, error } = await api.POST("/api/v1/machines/{id}/terminals", {
      params: id(),
      body: { workingDirectory: workingDirectory ?? null, cols: 100, rows: 30 },
    });
    if (!data) return setNotice(typeof error === "string" ? error : "The machine could not open a terminal.");
    refresh(terminals);
    setSelected(data.terminal.id);
  }

  async function close(terminal: Terminal) {
    await api.DELETE("/api/v1/machines/{id}/terminals/{terminalId}", {
      params: { path: { id: props.machineId, terminalId: terminal.id } },
    });
    if (selected() === terminal.id) setSelected(null);
    refresh(terminals);
  }

  props.ref?.({ open });

  return (
    <section class="rounded-lg border border-line bg-panel">
      <div class="px-4 py-3 border-b border-line flex items-center justify-between gap-3">
        <h2 class="text-sm font-medium">Terminals</h2>
        <button class="text-xs text-accent hover:underline" onClick={() => open()}>
          New terminal
        </button>
      </div>
      <Show when={notice()}>
        <p class="px-4 py-2 border-b border-line text-sm text-red-600">{notice()}</p>
      </Show>
      <ul class="divide-y divide-line text-sm">
        <For each={terminals()} keyed={(terminal) => terminal.id}>
          {(terminal) => (
            <li class="px-4 py-1.5 flex items-center justify-between gap-3">
              <button
                class={["truncate text-left hover:underline", { "font-medium": selected() === terminal().id }]}
                onClick={() => setSelected(terminal().id)}
              >
                {terminal().workingDirectory}
                <span class="text-muted"> · {terminal().exited ? `exited (${terminal().exitCode})` : terminal().shell}</span>
              </button>
              <button class="text-xs text-accent hover:underline shrink-0" onClick={() => close(terminal())}>
                Close
              </button>
            </li>
          )}
        </For>
        <Show when={terminals().length === 0}>
          <li class="px-4 py-2 text-muted">No terminals.</li>
        </Show>
      </ul>
      <For each={selected() ? [selected()!] : []}>
        {(terminalId) => (
          <div class="border-t border-line">
            <TerminalView machineId={props.machineId} terminalId={terminalId} onExit={() => refresh(terminals)} />
          </div>
        )}
      </For>
    </section>
  );
}
