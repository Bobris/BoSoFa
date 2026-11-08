import { server, system } from "#neoastra";

export interface ProbeResult {
  readonly name: string;
  readonly ok: boolean;
  readonly detail: string;
}

/** A local CORS-enabled HTTP server (tools/cors-probe.py) that answers what the browser sent it. */
const corsProbe = "http://127.0.0.1:5099";
const json = { "Content-Type": "application/json" };
const devLogin = JSON.stringify({ email: "desktop@bosofa.dev", name: "Desktop Probe" });

async function attempt(name: string, run: () => Promise<{ ok: boolean; detail: string }>): Promise<ProbeResult> {
  const start = performance.now();
  try {
    const { ok, detail } = await run();
    return { name, ok, detail: `${detail} [${(performance.now() - start).toFixed(0)} ms]` };
  } catch (error) {
    return { name, ok: false, detail: `${String(error)} [${(performance.now() - start).toFixed(0)} ms]` };
  }
}

async function describe(response: Response): Promise<string> {
  const text = response.type === "opaque" ? "" : await response.text();
  return `${response.status} ${response.type} ${text.slice(0, 160)}`;
}

/** Runs every probe in order and reports each one to the page and to the C# console. */
export async function runProbes(report: (result: ProbeResult) => void): Promise<void> {
  const { origin: target } = await server.origin({});
  const probes: Array<[string, () => Promise<{ ok: boolean; detail: string }>]> = [
    ["page origin", async () => ({ ok: true, detail: `location.origin=${location.origin} secure=${isSecureContext}` })],
    // (a) The page calls the server directly. The server sends no CORS headers yet.
    ["a. direct GET /api/v1/version", async () => {
      const response = await fetch(`${target}/api/v1/version`);
      return { ok: response.ok, detail: await describe(response) };
    }],
    ["a. direct GET no-cors /health/ready", async () => {
      const response = await fetch(`${target}/health/ready`, { mode: "no-cors" });
      return { ok: response.type === "opaque", detail: `${await describe(response)} (reachable, unreadable)` };
    }],
    ["a. direct POST /auth/dev-login (credentials)", async () => {
      const response = await fetch(`${target}/auth/dev-login`, { method: "POST", credentials: "include", headers: json, body: devLogin });
      return { ok: response.ok, detail: await describe(response) };
    }],
    ["a. direct WebSocket ws://…/api/v1/live", () => new Promise((resolve) => {
      const socket = new WebSocket(`${target.replace(/^http/, "ws")}/api/v1/live`);
      socket.onopen = () => { socket.close(); resolve({ ok: true, detail: "open" }); };
      socket.onclose = (event) => resolve({ ok: false, detail: `closed code=${event.code} (no endpoint yet; CSP allowed the attempt)` });
    })],
    // (a)+(b)+(d) against a server that does send CORS headers.
    ["a. CORS server GET /echo", async () => {
      const response = await fetch(`${corsProbe}/echo`);
      return { ok: response.ok, detail: await describe(response) };
    }],
    ["b. CORS server cookies (set, then send with credentials)", async () => {
      await fetch(`${corsProbe}/set-cookie`, { credentials: "include" });
      const response = await fetch(`${corsProbe}/echo`, { credentials: "include" });
      return { ok: response.ok, detail: await describe(response) };
    }],
    ["d. CORS server bearer (Authorization header, preflight)", async () => {
      const response = await fetch(`${corsProbe}/echo`, { headers: { Authorization: "Bearer desktop-test-token" } });
      return { ok: response.ok, detail: await describe(response) };
    }],
    // (c) RPC proxy: C# calls the server; its cookie jar holds the session.
    ["c. RPC proxy sign-out → me → dev-login → me", async () => {
      await server.fetch({ method: "POST", path: "/auth/logout", body: null, contentType: null });
      const before = await server.fetch({ method: "GET", path: "/api/v1/me", body: null, contentType: null });
      const login = await server.fetch({ method: "POST", path: "/auth/dev-login", body: devLogin, contentType: "application/json" });
      const me = await server.fetch({ method: "GET", path: "/api/v1/me", body: null, contentType: null });
      return { ok: before.status === 401 && login.status === 204 && me.status === 200,
        detail: `me=${before.status}, login=${login.status}, me=${me.status} ${me.body}` };
    }],
    // (c) Scheme interception: same-origin fetch to app://bosofa/..., forwarded by AppSchemeProvider.
    ["c. app:// proxy sign-out → me → dev-login → me", async () => {
      await fetch("/auth/logout", { method: "POST" });
      const before = await fetch("/api/v1/me");
      const login = await fetch("/auth/dev-login", { method: "POST", headers: json, body: devLogin });
      const me = await fetch("/api/v1/me");
      return { ok: before.status === 401 && login.status === 204 && me.status === 200,
        detail: `me=${before.status}, login=${login.status}, me=${me.status} ${await me.text()}` };
    }],
    ["c. app:// proxy GET /api/v1/users (JSON body)", async () => {
      const response = await fetch("/api/v1/users");
      return { ok: response.ok, detail: await describe(response) };
    }],
    ["c. app:// proxy 404 keeps its status", async () => {
      const response = await fetch("/api/v1/nope");
      return { ok: response.status === 404, detail: await describe(response) };
    }],
    ["page sees no auth cookie", async () => ({ ok: document.cookie === "", detail: `document.cookie=${JSON.stringify(document.cookie)}` })],
  ];
  for (const [name, run] of probes) {
    const result = await attempt(name, run);
    report(result);
    await system.report(result);
  }
  await system.report({ name: "done", ok: true, detail: `performance.now()=${performance.now().toFixed(0)} ms` });
}
