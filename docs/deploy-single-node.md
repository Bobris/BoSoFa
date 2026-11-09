# Running one server without Azure

A single node keeps everything on its own disk: the database, the event log, uploaded files, release binaries and the
Data Protection keys, all under `BoSoFa:DataDirectory` (`/data` in the image). It needs no Azure, no Kubernetes and
no other service. It is one process for good: the files are locked by that process, so it cannot grow into a cluster
later without moving the data (plan §11). For several nodes, see [deploy-aks.md](deploy-aks.md).

## With Docker Compose

[deploy/compose.yaml](../deploy/compose.yaml) runs the server and [Caddy](https://caddyserver.com), which gets a
certificate from Let's Encrypt for the host name. The server's port is not published; only Caddy reaches it.

1. Point a DNS name at the computer and open ports 80 and 443.
2. Register the server with your sign-in provider (OIDC), with the redirect URI `https://<host>/signin-oidc`.
3. Next to `compose.yaml`, write `.env`:

   ```bash
   BOSOFA_HOST=bosofa.example.com
   BOSOFA_OIDC_AUTHORITY=https://login.microsoftonline.com/<tenant>/v2.0
   BOSOFA_OIDC_CLIENT_ID=<client id>
   BOSOFA_OIDC_CLIENT_SECRET=<client secret>
   # Optional: the brand to build (docs/branding.md)
   BOSOFA_BRAND=qsf
   ```

4. Start it:

   ```bash
   docker compose -f deploy/compose.yaml up -d --build
   ```

The first person to sign in becomes the admin. To rename the product in the UI, add `BoSoFa__Branding__Name` and
`BoSoFa__Branding__Tagline` to the server's `environment` (a `compose.override.yaml` keeps the file as it is).

## Without Docker

Run the published server (`dotnet publish src/BoSoFa.Server -c Release`, plus `pnpm --dir web build` copied to its
`wwwroot`) as a service with:

| Setting | Value |
|---|---|
| `BoSoFa__DataDirectory` | a folder only the service's user can read |
| `ASPNETCORE_URLS` | `http://127.0.0.1:8080`, behind a reverse proxy |
| `BoSoFa__TrustForwardedHeaders` | `true` when the proxy is the only way in |
| `BoSoFa__Oidc__*` | the sign-in provider |

The proxy must not buffer, must allow hour-long connections, and must pass `Host`, `X-Forwarded-For` and
`X-Forwarded-Proto`; [deploy-aks.md](deploy-aks.md#reverse-proxy) has the settings. Never turn on `BoSoFa__DevLogin` on a
server anyone else can reach.

## Backups

Stop the server, copy the whole data folder (or the `data` volume), start it again. A copy taken while it runs may be
inconsistent. With Compose:

```bash
docker compose -f deploy/compose.yaml stop server
```

```bash
docker run --rm -v bosofa_data:/data -v "$PWD":/backup alpine tar -czf /backup/bosofa-data.tar.gz -C /data .
```

```bash
docker compose -f deploy/compose.yaml start server
```

The `keys` folder holds the Data Protection keys that encrypt harness account secrets: a backup without it restores
the accounts unreadable, and anyone with it can read them, so keep backups as private as the server.

Daemons reconnect after a restore by themselves; [deploy-aks.md](deploy-aks.md#daemons-after-a-restore) explains what
happens to sessions started after the backup.
