# Releasing

A release is a build of the server. `dotnet publish src/BoSoFa.Server` also builds the daemon (`bosofad`) and the desktop
app for every platform, with the server's brand and version, and puts them in `bundle/` beside the server. The server's
`/install` page and its install scripts hand out exactly those builds, and installed computers update to them (plan §5.7).
There is nothing to publish separately, no signing key and no rollout to run.

## Making a release

Build the image with a version:

```bash
docker build -f deploy/Dockerfile --build-arg VERSION=1.2.0 -t bosofa-server:1.2.0 .
```

Then deploy it as usual ([deploy-aks.md](deploy-aks.md), [deploy-single-node.md](deploy-single-node.md)). Once the new
server runs:

- each daemon gets the new build offered when it next connects (the server's restart makes them reconnect), fetches it,
  checks its SHA-256 and switches as soon as none of its sessions is busy (after 30 minutes at the latest);
- each desktop app finds it within six hours, or at its next start, and offers "Restart to update".

Computers only ever move to a newer version (SemVer precedence; build metadata after `+` does not count), so raise the
version with every release. While a cluster rolls from one build to the next, a daemon that reaches an old node is
offered nothing, and one that reaches a new node moves up once.

## What the build needs

- The desktop app's page: `pnpm --dir web build` before `dotnet publish` (the Dockerfile does it).
- Nothing per platform: the daemon is a self-contained, trimmed single file and the desktop app is ReadyToRun, and both
  build for Linux, Windows and macOS from any of them. macOS executables get the SDK's ad-hoc signature, which Apple
  silicon needs to run them.
- `-p:BoSoFaBundle=false` skips the bundle (the server then offers nothing to install), `-p:BoSoFaBundleRids=osx-arm64`
  builds fewer platforms, and `dotnet build src/BoSoFa.Server -p:BoSoFaBundle=true` makes one for `dotnet run`, to try
  installs and updates locally. The work happens under `artifacts/client-bundle/`.

## Trust

The first download is trusted because it comes over HTTPS from the server the person chose, and so is every update: a
daemon takes only the file whose SHA-256 the server it is connected to named. Builds are not signed beyond that, because
whoever controls the server can already run commands on its machines.

## A broken daemon

A daemon version is not taken back by itself. If a release cannot reach the server, fix it in the next server build: a
daemon that still connects takes that one. A machine whose daemon cannot connect at all is reinstalled from `/install`,
which keeps its identity (`enroll` passes for a machine already enrolled on that server).
