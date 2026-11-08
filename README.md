# BoSoFa

Bobris Software Factory: run AI coding harnesses (Claude Code, Codex, ACP agents) on your machines and cloud machines,
and control them from one web UI or desktop app.

- **Server**: C# Minimal API on BTDB replication (single node for development, multiple nodes on AKS), serving a
  SolidJS 2 single-page app.
- **Daemon** (`bosofad`): Native AOT service on each machine; installs and runs harnesses, provides MCP servers and
  skills, transfers files and buffers its traffic while the server is away.
- **Desktop**: NeoAstra shell around the same SPA, which can also manage the local daemon.

See [docs/plan.md](docs/plan.md) for the architecture and milestones, and [AGENTS.md](AGENTS.md) to build and run it.
