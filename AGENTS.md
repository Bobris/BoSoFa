# BoSoFa: working agreement

BoSoFa (Bobris Software Factory) runs AI coding harnesses on many machines, controlled from one server. The plan,
architecture and milestones are in [docs/plan.md](docs/plan.md); experiment notes are in `docs/spikes/`.

## Layout

- `src/BoSoFa.Protocol`: wire contracts shared by server, daemon and clients; references nothing else of BoSoFa.
- `src/BoSoFa.Server.Domain`: BTDB event-sourced domain (events, deterministic handlers, relations).
- `src/BoSoFa.Server`: ASP.NET Core Minimal API host (`bosofa-server`), auth, BTDB node, serves the SPA.
- `src/BoSoFa.Daemon`: `bosofad`, the per-machine daemon, published with Native AOT.
- `tests/`: xUnit. `BoSoFa.Architecture.Tests` enforces project boundaries.
- `web/`: pnpm workspace. `apps/web` is the SolidJS 2 SPA, `packages/api` the generated API client.

## Commands

- `dotnet build BoSoFa.slnx` / `dotnet test BoSoFa.slnx`
- Server for development: `dotnet run --project src/BoSoFa.Server` (http://localhost:5080, dev login on, data in `data/server`).
- SPA with hot reload: `pnpm --dir web dev` (http://localhost:5173, proxies the server). `pnpm --dir web build` writes
  `web/apps/web/dist`, which the development server also serves directly.
- Daemon: `dotnet publish src/BoSoFa.Daemon -r osx-arm64 -c Release -o artifacts/daemon/osx-arm64`.
- API changed: `BOSOFA_UPDATE_CONTRACT=1 dotnet test tests/BoSoFa.Server.Tests --filter ContractTest`, then
  `pnpm --dir web gen:api`. CI fails when either `openapi.json` or `schema.d.ts` is stale.

## Rules

- Every product assembly is `IsAotCompatible` and warnings are errors: no reflection-based JSON (use the
  source-generated contexts `ProtocolJsonContext` / `ServerJsonContext`), configuration binding goes through the
  binding generator, BTDB metadata through `BTDB.SourceGenerator`. Only the daemon actually publishes AOT.
- The daemon references only `BoSoFa.Protocol` and talks to the server only over HTTP(S), dialing out.
- BTDB handlers (`IEventHandler.Consume`) must be deterministic: use `ev.When`, never the clock, randomness or I/O;
  allocate IDs with `Ambient.AllocateId()`. Register new relations in `Domain.Relations` and handlers in
  `Domain.CreateDispatcher()`. Persisted relation changes need care (see BTDB `Doc/ReplicationHosting.md`).
- Keep the EventPublisher's `data` and `metadata` EventLog topics forever (no `EventLogOptions.Retention` for them).
- Solid 2 is a release candidate. Before writing UI code read `https://v2.solidjs.com/llms-small.txt` and
  `web/node_modules/solid-js/CHEATSHEET.md`. Never write Solid 1.x APIs: two-argument `createEffect`, `onSettled`
  instead of `onMount`, `<Loading>`/`<Errored>`, async `createMemo` instead of `createResource`, draft store setters,
  `<Ctx value>`. Never destructure props. Treat every `[CODE]` dev diagnostic as a bug. Pin Solid packages exactly and
  upgrade them together.
- Write documentation, identifiers and commit messages in English. Commit only when asked.
