# Spike ③: NeoAstra desktop shell with the Solid 2 SPA

Date: 2026-11-08. Code: [`spikes/neoastra-hello`](../../spikes/neoastra-hello). Machine: macOS 26.6.2, Apple Silicon (WKWebView).

## Verdict

- **It works.** A NeoAstra 1.12.0 window serves a SolidJS 2 (rc.21) SPA from `app://bosofa`. The SPA calls a typed
  NeoRpc service (`system.info`: machine name, OS, runtime) through the generated TypeScript client.
- **The unmodified `web/apps/web/dist` also runs in the desktop.** It shows "Hello <user>" and the People list from a
  live BoSoFa server, with no change to `web/` or the server. A C# `app://` scheme provider forwards `/api` and `/auth`
  to the server and keeps the auth cookie in a C# cookie jar.
- **Cookies cannot work cross-origin from `app://`.** WebKit sent no cookie at all to a CORS server that set them, not
  even `SameSite=None; Secure`. Bearer tokens over CORS work.
- **Recommendation:** the page calls the server **directly** with **CORS plus a short-lived bearer access token**. C#
  owns the refresh token (OS keychain) and the desktop sign-in flow. It hands the page access tokens over NeoRpc, and the
  page uses a one-time ticket for the live WebSocket. Details in the [recommended design](#recommended-desktop--server-design)
  section. The `app://` proxy is a useful zero-change preview, but it should not be the production transport.
- **No blockers.** The [caveats](#caveats-and-gotchas) are all manageable.

## What was built

| File | Purpose |
|---|---|
| `NeoAstraHello.csproj`, `Directory.Build.props` | net10.0 Exe, `NeoAstra` 1.12.0, framework-dependent, `PublishReadyToRun`. The props pin the generated RPC binding and the staged `@neoastra/client` to `obj/neoastra` beside the project, because the spikes build into `artifacts/spikes` (`UseArtifactsOutput`). |
| `neoastra.json` | Origin `app://bosofa`, npm frontend, production CSP with `connect-src 'self' http://localhost:5080 ws://localhost:5080 http://127.0.0.1:5099`. |
| `Program.cs` | Explicit `NeoApplication` composition (like CodeAlta's `DesktopApplication.RunAsync`), not `NeoApp.Run`, because the simple host cannot take a custom scheme provider. Sets up the window, the environment with the `app` scheme, `NeoRpcBuilder`, the view (`TrustedOrigins` bridge for `app://bosofa`), a navigation policy that allows only `app://bosofa/*`, an optional `CaptureAsync` screenshot, and auto-close. |
| `Services.cs` | `[NeoRpcService("system")]` `info`/`report` and `[NeoRpcService("server")]` `fetch`/`origin` (the RPC proxy), with a source-generated `JsonSerializerContext`. |
| `ServerSession.cs` | One `HttpClient` with a `CookieContainer`: the desktop's session with the server, shared by both proxies. |
| `AppSchemeProvider.cs` | `AppSchemeProvider` forwards `/api/`, `/auth/`, `/health`, `/openapi` to the server (the interception prototype). `SpaDirectoryProvider` serves any Vite `dist` with an `index.html` fallback and a CSP decided **at run time**. |
| `frontend/` | Minimal Solid 2 Vite app (same setup as `web/apps/web`, without Tailwind or the router) plus `base: "./"` and the `#neoastra` alias. `src/probes.ts` runs the communication probes and reports each one to the page and to the C# console. |
| `tools/cors-probe.py` | A tiny CORS-enabled HTTP server on `127.0.0.1:5099` that reports which `Origin`, `Cookie` and `Authorization` it received. It stands in for a future CORS-enabled BoSoFa server; the real server was not changed. |

## Versions

| Component | Version |
|---|---|
| NeoAstra / NeoAstra.Core (NuGet) | **1.12.0**, the latest on nuget.org as of 2026-11-08 and the same as CodeAlta |
| `@neoastra/client` | 0.1.0, staged from the NuGet package (`file:../obj/neoastra/client`) |
| .NET SDK / runtime | 10.0.403 / 10.0.14 |
| solid-js, @solidjs/web | 2.0.0-rc.21 (exact) |
| @solidjs/vite-plugin | 3.0.0-next.57 (exact) |
| vite / typescript | 8.3.6 / 5.9.3 (exact) |
| Node / npm | 23.11.0 / 11.7.0 |

## Build and run

```sh
cd spikes/neoastra-hello
dotnet build        # C# + RPC codegen, stages @neoastra/client, locked `npm ci` when needed, `npm run build`, asset manifest
dotnet run --no-build

# The probes need the server and the CORS stand-in:
dotnet run --project src/BoSoFa.Server        # http://localhost:5080
python3 spikes/neoastra-hello/tools/cors-probe.py

# The real SPA, unmodified, through the app:// proxy (C# signs in first with the dev login):
pnpm --dir web build
SPIKE_SPA_DIST=$PWD/web/apps/web/dist SPIKE_DEV_LOGIN=me@example.com dotnet run --project spikes/neoastra-hello --no-build

# Release, ReadyToRun, framework-dependent:
dotnet publish spikes/neoastra-hello -c Release -r osx-arm64   # -> artifacts/spikes/publish/NeoAstraHello/release_osx-arm64
```

Environment knobs:

- `BOSOFA_SERVER`: the server origin (default `http://localhost:5080`).
- `SPIKE_SPA_DIST`: serve this directory instead of the spike frontend.
- `SPIKE_DEV_LOGIN`: sign the C# cookie jar in before the page loads.
- `SPIKE_CAPTURE=<png>`: save a capture of the view, then log `document.body.innerText`.
- `SPIKE_AUTOCLOSE=<s>`: close the window after that many seconds.

The first `npm install` that created `frontend/package-lock.json` was run by hand once, after
`dotnet build -t:NeoAstraStageFrontendClient`. Every later build restores from the lockfile. A full incremental
`dotnet build` including the Vite build takes about 3.9 s.

## Startup time and size

Startup is measured from the OS process start time to each milestone. "Page rendered" is the Solid `onSettled` report
over RPC. The window was not frontmost (see the caveats).

| Run | → Main | environment | view | navigate | page rendered | 13 probes done |
|---|---|---|---|---|---|---|
| Debug, first run ever | 344 ms | 956 ms | 1032 ms | 1097 ms | 1233 ms | 1490 ms |
| Release R2R, first run | 253 ms | 654 ms | 710 ms | 750 ms | 853 ms | 1104 ms |
| Release R2R, warm (×2) | 52–55 ms | ~270 ms | ~330 ms | ~365 ms | **~477 ms** | ~680 ms |

The "environment / view / navigate" columns are process-relative: the logged `clock` value plus the "→ Main" offset. The
page JavaScript itself settles about 10 ms after it starts.

Publish output for osx-arm64 (framework-dependent, R2R): **7.6 MB** in total.

| File | Size |
|---|---|
| `NeoAstra.dll` | 4.4 MB |
| `NeoAstra.Core.dll` | 2.0 MB |
| `libneoastra_native.dylib` | 0.38 MB |
| Microsoft.Extensions abstractions | ~0.6 MB |
| App | 0.22 MB |
| Frontend `assets/` | 80 KB |

The spike's JS is 69 KB (24.5 KB gzip). `web/apps/web/dist` is 104 KB. A self-contained build would add the .NET
runtime, roughly 70 MB.

## Probe results (WKWebView, page origin `app://bosofa`, `isSecureContext = true`)

| Probe | Result |
|---|---|
| NeoRpc `system.info` from Solid (`createMemo(() => system.info({}))` under `<Loading>`) | OK |
| a. `fetch(http://localhost:5080/api/v1/version)` | **Fails** (`TypeError: Load failed`): the server sends no `Access-Control-Allow-Origin`. CSP allowed the request. |
| a. same request with `mode: "no-cors"` | Opaque response: the server is reachable, but the page cannot read the body |
| a. `POST /auth/dev-login` with JSON and credentials | **Fails**: the preflight gets `405` from the server (`OPTIONS` is not handled) |
| a. `new WebSocket(ws://localhost:5080/api/v1/live)` | Closes with 1006, because the endpoint does not exist yet. CSP (`ws://localhost:5080`) did not block the attempt. |
| a. CORS server `GET /echo` | OK. The server sees `Origin: app://bosofa` and `Sec-Fetch-Site: cross-site`. |
| b. CORS server sets `lax`, `none` (`SameSite=None; Secure`) and `plain` cookies, then `/echo` with `credentials: "include"` | **No cookie sent** (`Cookie: null`). WebKit blocks third-party cookies (ITP), and `app://` is never same-site with `http(s)://` |
| d. CORS server, `Authorization: Bearer …` | OK: a preflight, then the header arrives |
| c. RPC proxy (`server.fetch`): sign-out → `me` → dev-login → `me` | OK: 401, 204, 200 with the user (45–50 ms for the 4 calls, warm) |
| c. `app://` proxy: same-origin `fetch("/auth/…")`, `fetch("/api/v1/me")`, same sequence | OK: 401, 204, 200 (15 ms for the 4 calls, warm). The POST JSON body (53 B) reached C#. |
| c. `app://` proxy `/api/v1/users` (JSON body), `/api/v1/nope` (404) | OK. The 404 status is preserved. |
| The page cannot see the auth cookie (`document.cookie === ""`) | OK |
| The **unmodified `web/apps/web/dist`** with `SPIKE_DEV_LOGIN` | Renders "Hello Desktop desktop" and People (3 users). `/api/v1/me` and `/api/v1/users` went through the `app://` proxy |

The `app://` proxy requests ran on **managed thread 1**, the native UI thread.

The probes signed in through the development login. That created or renamed the user `desktop@bosofa.dev` in the
development database (`data/server`).

## Findings on the four questions

**(a) Can `app://` reach `http://localhost:5080`?**

- The network path works, and NeoAstra's production CSP accepts remote `http:`/`ws:` origins in `connect-src`. The
  schema rejects only `*` and `unsafe-eval`.
- What blocks it today is CORS: the server sends no CORS headers and answers preflights with 405.
- WKWebView sends `Origin: app://bosofa`. A server CORS policy can name that origin exactly.
- `neoastra.json`'s CSP is fixed at build time. A server URL chosen at run time needs the document's CSP header set by
  our own resource provider at run time. `SpaDirectoryProvider` shows how. A `<meta>` CSP can only tighten a policy,
  never loosen it.

**(b) Do cookies work?**

- No, and no server setting fixes it. WebKit treats every request from `app://bosofa` to the server as cross-site and
  drops cookies in both directions, even `SameSite=None; Secure`. That is WebKit's third-party cookie blocking.
- The same applies to the WebSocket handshake. Cookie auth from the desktop page is therefore out.

**(c) Proxying through C#.** Both variants work and keep credentials out of the page.

- **Scheme interception** (`INeoResourceProvider` on `app://`) runs the unmodified SPA. Its limits in NeoAstra 1.12.0:
  - `GetResponse` is **synchronous and runs on the native UI thread**, so every API call blocks the UI for its full
    round trip. On a remote or slow server that freezes the window.
  - The public `NeoResourceResponse` factories give a body only with status 200; any other status is `Empty`. 401/404
    bodies and ProblemDetails are lost, and 201-with-body becomes 200 or empty.
  - Responses are buffered, up to 64 MiB. There is no streaming and no server-sent events.
  - A custom scheme can never carry a WebSocket, so `/api/v1/live` would still need another path.
  - WKWebView did pass the POST body. The docs say "when exposed by the backend", so behaviour on WebView2 and
    WebKitGTK still needs checking.
- **RPC proxy** (`server.fetch` NeoRpc service):
  - It is asynchronous, runs off the UI thread, and returns any status with its body.
  - It could stream (NeoRpc channels and events) and tunnel the live WebSocket through a C# `ClientWebSocket`.
  - Costs: the SPA needs a transport abstraction instead of plain `fetch`, payloads are serialized twice (JSON inside
    JSON), and it measured about 3× slower than the scheme path locally (still under 15 ms per call).

**(d) Bearer tokens.**

- They work over CORS: a preflight, then the `Authorization` header arrives, with no credentials mode needed.
- Bearer suits the desktop:
  - It needs no cookies.
  - It is not open to CSRF.
  - It is the same model as `/auth/desktop/start` in the plan (§4.3) and as API tokens.
  - `openapi-fetch` (`@bosofa/api`) supports middleware, so a desktop `Platform` can inject the header and a `baseUrl`.
- The browser `WebSocket` API cannot set headers, so the live socket needs a short-lived ticket in the query string (or
  a first auth message).
- Having the access token visible to page script adds little risk: script in the trusted `app://` document already
  controls the native NeoRpc bridge.

## Recommended desktop ↔ server design

1. **Direct HTTPS and WSS from the page to the server, bearer-authenticated.** The SPA keeps one code path for web and
   desktop. The desktop `Platform` (plan §6) supplies the `baseUrl`, an `Authorization` header (an `openapi-fetch`
   middleware) and a live-socket URL with a ticket. The web `Platform` keeps same-origin cookies. REST and the live
   WebSocket stay plain browser networking: no UI-thread hop, no double serialization, and streaming works.
2. **C# owns the long-lived secret.**
   - The desktop flow: system browser, loopback redirect (RFC 8252), one-time code, access + refresh tokens.
   - The refresh token goes into the OS keychain, through NeoAstra's safe storage or the planned keychain RPC.
   - A NeoRpc `auth` service gives the page short-lived access tokens: `accessToken()`, refresh on 401, `signOut()`.
   - The page keeps access tokens in memory only, never in `localStorage`.
3. **CSP is decided at run time.** Serve `index.html` through a wrapping provider that writes
   `connect-src 'self' https://<server> wss://<server>` for the configured server, as `SpaDirectoryProvider` does. Keep
   everything else as strict as the build-time policy.
4. **Keep the RPC proxy as an escape hatch, not the default.** It covers what WKWebView networking cannot:
   - private-CA or mTLS servers, where .NET trust configuration is easier than per-platform webview TLS handling;
   - corporate proxies;
   - calls to the local daemon.
5. **The `app://` interception proxy** is useful now, to show the existing SPA in the desktop with zero changes (M0/M1
   previews). It should not ship as the transport because of the UI-thread, status-body and WebSocket limits above.

### Server changes this needs (not made in this spike)

1. **CORS.**
   - `AddCors` with a policy for a configurable list of desktop origins (default `app://bosofa`).
   - Methods GET/POST/PUT/PATCH/DELETE; headers `authorization`, `content-type`; a preflight max-age.
   - No `AllowCredentials`.
   - Call `UseCors()` before authentication, so preflights get 204 instead of today's 405.
2. **A bearer scheme next to the cookie.**
   - Access tokens signed with the server keys (plan §4.4), validated by a JWT or BearerToken handler.
   - A policy scheme picks bearer when an `Authorization` header is present and the cookie otherwise.
   - Endpoints `/auth/desktop/start`, `/auth/desktop/token` (code exchange with PKCE), `/auth/desktop/refresh`,
     `/auth/desktop/revoke`.
   - Refresh tokens are stored hashed and rotated, like `ApiToken`.
   - The development login needs a desktop variant that ends in tokens, for example the browser dev-login page
     finishing the loopback flow.
3. **Live WebSocket auth.**
   - `POST /api/v1/live/ticket` (bearer or cookie) returns a single-use ticket valid for about 30 s.
   - `/api/v1/live?ticket=…` accepts it.
   - Validate `Origin` against the allow-list (web origin + `app://bosofa`) to prevent cross-site WebSocket hijacking.
     The cookie-authenticated web socket needs this check anyway.
4. Optional: the ProblemDetails / 401 contract stays the same for bearer callers, so the SPA's error handling is shared.

## Caveats and gotchas

- **`NeoApp.Run` (the simple host) has no hook for a custom scheme provider.** Interception and run-time CSP need
  explicit `NeoApplication` composition, as CodeAlta does. That costs about 60 lines and was easy.
- **Automatic frontend restore is npm-only.** It runs `npm ci` and needs a `package-lock.json` under `frontend.root`.
  The BoSoFa web workspace is pnpm.
  - For `web/apps/desktop`, run the pnpm build ourselves and give NeoAstra the output, with `NeoAstraPrebuiltAssets=true`
    and `NeoAstraPrebuiltAssetDirectory` equal to the configured `dist`, or with `NeoAstraBuildFrontend=false` plus our
    own build step.
  - Alternatively, use a runtime directory provider as this spike does with `SPIKE_SPA_DIST`; that skips NeoAstra's
    asset validation and hash manifest.
- **NeoAstra prefers Vite `base: "./"`.** `web/apps/web` builds with `/`. Absolute `/assets/...` paths still resolve on
  `app://bosofa`, so the unmodified dist worked; keep the root base for the desktop app as well.
- **Navigate to `app://bosofa/`, not `/index.html`.** The Solid router uses path history and would otherwise show
  NotFound.
- The navigation policy must allow every `app://bosofa/*` path. The OIDC link in `Login.tsx` (`/auth/login`) is a
  top-level navigation that the desktop policy cancels. The desktop sign-in has to go through the system browser
  anyway.
- **`UseArtifactsOutput` moves `obj`.** Set `NeoRpcIntermediateOutputPath` and `NeoAstraFrontendClientStageDirectory`
  in a `Directory.Build.props`; otherwise `@neoastra/client` is not staged where `package.json` expects it.
- **The window was not frontmost.** Launched as a plain executable from a background shell, it opened but did not come
  to the front: the full-screen `screencapture` showed other apps, and the screenshot was taken with
  `NeoAstra.CaptureAsync` instead. The real app needs an `.app` bundle and activation, as `DesktopMacBundle` does in
  CodeAlta.
- **Not tried:**
  - `dotnet neoastra dev` (needs the `NeoAstra.Tool` package) for Vite hot reload inside the window;
  - Windows (WebView2) and Linux (WebKitGTK; no `TrustedOrigins` bridge there, so `TrustEntireView` is required);
  - the system-browser and loopback sign-in flow.
- `tsc --noEmit` passes, and the production build renders correctly. The page console, and so Solid's dev-mode `[CODE]`
  diagnostics, was not inspected inside the webview (Web Inspector is a per-view opt-in on macOS).
