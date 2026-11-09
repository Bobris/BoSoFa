# BoSoFa: working agreement

BoSoFa (Bobris Software Factory) runs AI coding harnesses on many machines, controlled from one server. The plan,
architecture and milestones are in [docs/plan.md](docs/plan.md); experiment notes are in `docs/spikes/`.

## Layout

- `src/BoSoFa.Protocol`: wire contracts shared by server, daemon and clients; references nothing else of BoSoFa.
- `src/BoSoFa.Server.Domain`: BTDB event-sourced domain (events, deterministic handlers, relations).
- `src/BoSoFa.Server`: ASP.NET Core Minimal API host (`bosofa-server`), auth, BTDB node, serves the SPA.
- `src/BoSoFa.Daemon`: `bosofad`, the per-machine daemon, published with Native AOT.
- `tools/BoSoFa.DevCluster`: runs a local cluster (Azurite, node processes, front door); the cluster tests use its classes.
- `tests/`: xUnit. `BoSoFa.Architecture.Tests` enforces project boundaries. The cluster and failover tests need Azurite.
- `src/BoSoFa.Desktop`: the NeoAstra desktop app; `web/apps/desktop` is the same SPA with a desktop `Platform`.
- `web/`: pnpm workspace. `apps/web` is the SolidJS 2 SPA, `packages/api` the generated API client.

## Commands

- `dotnet build BoSoFa.slnx` / `dotnet test BoSoFa.slnx`
- Server for development: `dotnet run --project src/BoSoFa.Server` (http://localhost:5080, dev login on, data in `data/server`).
- SPA with hot reload: `pnpm --dir web dev` (http://localhost:5173, proxies the server). `pnpm --dir web build` writes
  `web/apps/web/dist`, which the development server also serves directly.
- Daemon: `dotnet publish src/BoSoFa.Daemon -r osx-arm64 -c Release -o artifacts/daemon/osx-arm64`, then
  `bosofad enroll --server http://localhost:5080` (approve the code at /enroll) and `bosofad run`. Use `--data-dir` for a
  throwaway identity; the default is the per-user data directory. `run --replay-fixture tests/fixtures/claude/write-file.jsonl`
  adds the "replay" harness, which plays a recorded Claude Code transcript without a model (see `ReplayTransport`).
- Cluster for development: `dotnet run --project tools/BoSoFa.DevCluster` starts Azurite (`npm install --global
  azurite`), three nodes as separate processes and a front door on http://localhost:5080 (data in `data/cluster`).
  Type `kill 1` / `start 1` to try failover. The SPA dev server and daemons point at port 5080 as usual.
- Desktop: `pnpm --dir web build`, then `dotnet run --project src/BoSoFa.Desktop`. RPC changed: build it and commit `web/apps/desktop/neoastra/neoastra.ts`.
- Service and updates: `bosofad install --dry-run` prints the service definition; `bosofad doctor` checks a machine. Releases are signed with
  `bosofad release-keygen` / `release-publish [--product desktop]`; a build needs `-p:BoSoFaReleasePublicKey=<key>` to accept them.
  The release workflow publishes both products with the key from a GitHub secret: [docs/releasing.md](docs/releasing.md).
- White labeling: `BoSoFa:Branding:Name` / `Tagline` in the server's configuration; the SPA reads `/api/v1/branding` (`branding.ts`).
- Distribution: computers install both products from the server's `/install` page and update from it. A desktop package:
  `pnpm --dir web build`, then `dotnet publish src/BoSoFa.Desktop -c Release -r osx-arm64 -p:BoSoFaPackage=true -p:Version=<v>`
  writes `artifacts/package/bosofa-desktop-<rid>.tar.gz` (`.zip` on Windows).
- AKS: `deploy/helm/bosofa` and `deploy/Dockerfile`; the runbook is [docs/deploy-aks.md](docs/deploy-aks.md). A change to persisted relations raises `Domain.ApplicationGeneration`.
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
