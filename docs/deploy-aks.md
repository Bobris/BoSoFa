# Deploying BoSoFa on AKS

The server runs as a StatefulSet from the Helm chart in `deploy/helm/bosofa`. Azure Blob storage holds all durable state; each pod's `/data` is a cache rebuilt on restart.

Each pod serves plain HTTP on one port, 8080, for clients and for the other nodes. A reverse proxy in front terminates TLS. Traffic between pods is unencrypted, including the shared peer key, so the chart's NetworkPolicy admits only the proxy and the other nodes.

## What the cluster needs

| Resource | Setting |
|---|---|
| AKS | OIDC issuer and workload identity on, a network policy engine |
| Storage account | No hierarchical namespace; the settings under [Backups](#backups) |
| Key Vault | RBAC mode, an RSA key for Data Protection |
| Managed identity | `Storage Blob Data Contributor` on the account, `Key Vault Crypto Service Encryption User` on the vault |
| Federated credential | On that identity: the cluster's issuer, subject `system:serviceaccount:<namespace>:<release>` |
| Reverse proxy | Terminates TLS, adds `X-Forwarded-For` and `X-Forwarded-Proto`, runs in a namespace listed in `networkPolicy.ingressNamespaces` |

## Secrets

The peer key is a Secret that exists before the install, so nothing secret goes through Helm values.

```bash
kubectl -n bosofa create secret generic bosofa-secrets --from-literal=peer-key="$(openssl rand -hex 32)"
```

Add `oidc-client-secret` to that Secret when using OIDC.

## Image

Build from a clean checkout. `az acr build` on a working tree full of build output failed here while downloading its context.

```bash
az acr build --registry <registry> --image bosofa-server:<tag> --file deploy/Dockerfile --platform linux/amd64 .
```

## Install

```yaml
# values.yaml
image: { repository: <registry>.azurecr.io/bosofa-server, tag: <tag> }
secretName: bosofa-secrets
azure:
  serviceUri: https://<account>.blob.core.windows.net
  keyVaultKey: https://<vault>.vault.azure.net/keys/<key>
  clientId: <client id of the managed identity>
oidc: { authority: <issuer>, clientId: <client id> }
otel: { endpoint: <OTLP collector> }
```

```bash
helm upgrade --install bosofa deploy/helm/bosofa -n bosofa -f values.yaml
```

## Reverse proxy

The proxy must not buffer, and must allow long idle connections: sessions stream over WebSockets, daemons may long-poll, and files pass through the server. With Caddy, proxying to the release's Service:

```caddyfile
bosofa.example.com {
    reverse_proxy bosofa.bosofa.svc.cluster.local:80 {
        flush_interval -1
    }
}
```

Caddy adds the forwarded headers by default. The server refuses its peer routes (`/internal`, `/_btdb`) on any request carrying `X-Forwarded-For`, so a proxy that does not add it would expose them to the peer key alone.

The chart can instead create an Ingress (`ingress.enabled`). Its default annotations are for ingress-nginx and AKS application routing; another controller needs its own equivalents.

Never set `devLogin` on a cluster anyone else can reach: it lets anyone sign in without a password.

## Upgrades

`helm upgrade` with a new tag rolls the pods one by one. Sessions continue, and daemons reconnect by themselves.

Any change to persisted relations must raise `Domain.ApplicationGeneration` in the same build. The first node of the new generation takes over and publishes its schema, and older builds can never lead again, so **rolling back across a generation needs a restore from backup**.

### Writes pause when the leader's pod goes away

Reads continue. Writes pause for about 35 seconds on staging, when the leading pod is deleted and once during a rolling restart. They then succeed without a client retry. Peers give up on a dead address after 3 s, but the cluster's 30 s DNS cache keeps returning it for a while.

## Backups

The whole container is block blobs, so Azure point-in-time restore can return it to one moment. Enable it on the storage account; the delete retention must be longer than the restore window.

```bash
az storage account blob-service-properties update -g <group> -n <account> \
  --enable-versioning true --enable-change-feed true \
  --enable-delete-retention true --delete-retention-days 15 \
  --enable-restore-policy true --restore-days 14
```

Blob versions accumulate, so add a lifecycle rule that deletes versions older than the restore window. Point-in-time restore does not protect against losing the account: use a resource lock, and geo-redundant storage if a region loss matters.

To restore:

```bash
kubectl -n bosofa scale statefulset/bosofa --replicas=0
kubectl -n bosofa wait --for=delete pod -l app.kubernetes.io/name=bosofa --timeout=120s
sleep 20   # the leader's 15 s lease must have expired
az storage blob restore -g <group> --account-name <account> --time-to-restore <UTC time>
kubectl -n bosofa scale statefulset/bosofa --replicas=3
```

Everything written after that time is gone, including machines enrolled and sessions started since.

On staging a restore of a very small container took 37 minutes, so plan for a long outage whatever the size.

### Known limit: daemons drop new commands after a restore

After a restore the server hands out IDs again that daemons have already seen, and a daemon discards a command whose ID is in its journal. On staging, new sessions stayed in `Starting` until the IDs passed the highest one from before the restore. Everything else was intact, and daemons reconnected by themselves.

## Telemetry

With `otel.endpoint` set the server exports traces, metrics and logs over OTLP. The standard `OTEL_*` variables tune it through `extraEnv`. The meter `BoSoFa.Server` adds `bosofa.daemons.connected`, `bosofa.node.leader` and `bosofa.events.applied`.
