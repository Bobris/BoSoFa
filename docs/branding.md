# Branding (white labeling)

BoSoFa can carry another name. There are two levels:

| Level | What it names | Set by |
|---|---|---|
| Build brand | The daemon's executable, the desktop app, folders, services, git branches and refs, MCP tool names, download file names, install scripts, and the UI's default names | `-p:Brand=<name>` when building, from `brands/<name>.props` |
| Server branding | The name and tagline in the web page and the desktop app | `BoSoFa:Branding:Name` and `Tagline` in the server's configuration |

The server's configuration only renames the UI. Everything installed on computers comes from the build brand, so the
server, the daemon and the desktop app of one deployment must be built with the same brand.

## Brands

| Brand | Name | Full name (tagline) | Id | Daemon |
|---|---|---|---|---|
| `bosofa` (default) | BoSoFa | Bobris Software Factory | bosofa | bosofad |
| `qsf` | QSF | Quadient Software Factory | qsf | qsfd |

From these the build derives:

| What | bosofa | qsf |
|---|---|---|
| Daemon executable | `bosofad`, `bosofad.exe` | `qsfd`, `qsfd.exe` |
| Desktop executable | `bosofa-desktop` | `qsf-desktop` |
| macOS app | `BoSoFa.app` (`dev.bosofa.desktop`) | `QSF.app` (`dev.qsf.desktop`) |
| Per-user data folder | `~/Library/Application Support/BoSoFa`, `%LOCALAPPDATA%\BoSoFa`, `~/.local/share/bosofa` | `…/QSF`, `…\QSF`, `~/.local/share/qsf` |
| Services | `bosofad.service`, `com.bosofa.daemon`, `BoSoFaDaemon` | `qsfd.service`, `com.qsf.daemon`, `QSFDaemon` |
| Session branches and checkpoints | `bosofa/<id>-<slug>`, `refs/bosofa/…` | `qsf/<id>-<slug>`, `refs/qsf/…` |
| MCP server and tools | `bosofa`, `bosofa_notify_user`, … | `qsf`, `qsf_notify_user`, … |
| Downloads | `bosofad`, `bosofa-desktop-<rid>.tar.gz` | `qsfd`, `qsf-desktop-<rid>.tar.gz` |

The icons come from `brands/icons` (`BrandIcons`), drawn without letters so every brand can use them; a brand may point
`BrandIcons` at its own folder ([brands/icons/README.md](../brands/icons/README.md)).

Internal names stay the same in every brand: the projects and namespaces (`BoSoFa.*`), the configuration section
`BoSoFa`, environment variables (`BOSOFA_*`), HTTP headers (`X-BoSoFa-*`), the desktop page's origin `app://bosofa`, and
the server's own assembly (`bosofa-server.dll`).

## Building a brand

```bash
dotnet publish src/BoSoFa.Daemon -c Release -r osx-arm64 -p:Brand=qsf -o artifacts/daemon/osx-arm64
```

```bash
docker build -f deploy/Dockerfile --build-arg BRAND=qsf -t qsf-server .
```

The release workflow builds the brand in the repository variable `BOSOFA_BRAND` ([releasing.md](releasing.md)). The
web page needs no brand: it reads its names from the server (`/api/v1/branding`) and remembers them for the next start.

## Adding a brand

Copy `brands/qsf.props` to `brands/<name>.props` and change the four values. `BrandName` is letters and digits (it names
folders, an app and a Windows service), `BrandId` lowercase letters and digits, `BrandDaemon` lowercase letters, digits
and dashes. The build checks them.

Changing the brand of an existing installation is a new installation: computers keep the old daemon, service and data
folder until they are reinstalled from the new server's install page.
