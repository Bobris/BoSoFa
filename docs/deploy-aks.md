# Deploying BoSoFa on AKS

The AKS workload is a Pulumi project in the operations repositories, the same way SkillShare is deployed. This repository
ships the server image (`deploy/Dockerfile`); this page is the contract that project implements. Staging ran from an
equivalent Helm chart, which was removed once Pulumi became the way to deploy (2026-11-09).

The server runs as a StatefulSet. Azure Blob storage holds all durable state; each pod's `/data` is a cache rebuilt on
restart. Each pod serves plain HTTP on one port, 8080, for clients and for the other nodes. A reverse proxy in front
terminates TLS. Traffic between pods is unencrypted, including the shared peer key, so a NetworkPolicy admits only the
proxy and the other nodes.

## What the cluster needs

| Resource | Setting |
|---|---|
| AKS | OIDC issuer and workload identity on, a network policy engine |
| Storage account | No hierarchical namespace; the settings under [Backups](#backups) |
| Key Vault | RBAC mode, an RSA key for Data Protection |
| Managed identity | `Storage Blob Data Contributor` on the account, `Key Vault Crypto Service Encryption User` on the vault |
| Federated credential | On that identity: the cluster's issuer, subject `system:serviceaccount:<namespace>:bosofa` |
| Reverse proxy | Terminates TLS, adds `X-Forwarded-For` and `X-Forwarded-Proto`, passes the original `Host`; its namespace is admitted by the NetworkPolicy |

## Image

Build from a clean checkout. `az acr build` on a working tree full of build output failed here while downloading its context.

```bash
az acr build --registry <registry> --image bosofa-server:<tag> --file deploy/Dockerfile --platform linux/amd64 .
```

## Secrets

| Key | Content |
|---|---|
| `peer-key` | Bearer key the nodes share for their EventLog requests; 32 random bytes as hex. Generate it in the stack (for example Pulumi's `random.RandomPassword`) and keep it as a stack secret. |
| `oidc-client-secret` | With OIDC: the client secret of the server's app registration. |

Put both in one Kubernetes Secret in the namespace, created by the same project.

## Workload

All objects live in one namespace (`bosofa` below) and share the selector labels `app.kubernetes.io/name: bosofa` and
`app.kubernetes.io/instance: bosofa`.

**ServiceAccount** `bosofa`, annotated `azure.workload.identity/client-id: <client id of the managed identity>`.

**Services:**

- `bosofa`: ClusterIP for the proxy, port 80 to the pods' 8080.
- `bosofa-peers`: headless (`clusterIP: None`) with `publishNotReadyAddresses: true`, port 8080. It gives each pod stable
  DNS, and nodes must find each other before they are ready.

**StatefulSet** `bosofa`:

- `serviceName: bosofa-peers`, 3 replicas, `podManagementPolicy: Parallel` (the nodes elect a leader among themselves;
  starting them one by one only delays that), `RollingUpdate`.
- Pod label `azure.workload.identity/use: "true"` besides the selector labels; `serviceAccountName: bosofa`.
- Topology spread over `kubernetes.io/hostname`, `maxSkew: 1`, `ScheduleAnyway`.
- Pod security: `runAsNonRoot: true`, `fsGroup: 1654`. Container: `allowPrivilegeEscalation: false`,
  `readOnlyRootFilesystem: true`, all capabilities dropped.
- Container `server`, port `http` 8080. Volumes: `emptyDir` on `/data` (size limit 10Gi; the cache) and on `/tmp`.
- `terminationGracePeriodSeconds: 60`, and a `preStop` sleep of 5 seconds so the proxy stops routing to the pod before it
  refuses connections.
- Probes. Kestrel starts listening after the database is restored, which takes as long as the history is big:
  - startup: `GET /health/live`, every 5 s, 180 failures allowed;
  - liveness: `GET /health/live`, every 10 s, timeout 5 s;
  - readiness: `GET /health/ready`, every 3 s, timeout 3 s.
- Resources: requests 250m CPU and 512Mi, memory limit 2Gi.

Environment:

| Variable | Value |
|---|---|
| `POD_NAME` | from `metadata.name` |
| `BoSoFa__DataDirectory` | `/data` |
| `BoSoFa__WebRoot` | `/app/wwwroot` |
| `BoSoFa__TrustForwardedHeaders` | `true` |
| `BoSoFa__Cluster__NodeId` | `$(POD_NAME)` |
| `BoSoFa__Cluster__PeerUrl` | `http://$(POD_NAME).bosofa-peers.<namespace>.svc.cluster.local:8080` |
| `BoSoFa__Cluster__PeerKey` | the Secret's `peer-key` |
| `BoSoFa__Cluster__Azure__ServiceUri` | `https://<account>.blob.core.windows.net` |
| `BoSoFa__Cluster__Azure__Container` | `bosofa` |
| `BoSoFa__Cluster__Azure__KeyVaultKey` | `https://<vault>.vault.azure.net/keys/<key>`; unset leaves the Data Protection key ring unwrapped |
| `BoSoFa__Oidc__Authority`, `__ClientId`, `__DisplayName` | the sign-in provider; `__ClientSecret` from the Secret's `oidc-client-secret` |
| `BoSoFa__Branding__Name`, `__Tagline` | optional white labeling; an empty tagline shows none |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` | optional, see [Telemetry](#telemetry) |

Never set `BoSoFa__DevLogin` on a cluster anyone else can reach: it lets anyone sign in without a password.

**PodDisruptionBudget**: `maxUnavailable: 1`.

**NetworkPolicy** on the pods, policy type Ingress: allow port `http` from pods with the selector labels and from the
proxy's namespace (`app-routing-system` for AKS application routing). The server trusts `X-Forwarded-*`, and the peer key
crosses this port unencrypted, so nothing else may reach it.

## Reverse proxy

The proxy must not buffer, and must allow long idle connections: sessions stream over WebSockets, daemons may long-poll,
and files pass through the server. With ingress-nginx (including AKS application routing), annotate the Ingress:

```yaml
nginx.ingress.kubernetes.io/proxy-buffering: "off"
nginx.ingress.kubernetes.io/proxy-request-buffering: "off"
nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
nginx.ingress.kubernetes.io/proxy-body-size: "0"
```

With Caddy, proxying to the Service:

```caddyfile
bosofa.example.com {
    reverse_proxy bosofa.bosofa.svc.cluster.local:80 {
        flush_interval -1
    }
}
```

Caddy adds the forwarded headers by default. The server refuses its peer routes (`/internal`, `/_btdb`) on any request
carrying `X-Forwarded-For`, so a proxy that does not add it would expose them to the peer key alone.

The proxy must also pass the original `Host` and add `X-Forwarded-Proto`: the install scripts at `/install/*` name the
server by the address they were fetched from, so a missing header would make them download from `http://` or from the
pod's own address.

## Upgrades

A new image tag applied with `pulumi up` rolls the pods one by one. Sessions continue, and daemons reconnect by themselves.

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

To restore, stop every pod first. Scaling with `kubectl` is fine for the restore itself; the next `pulumi up` sets the
stack's replica count again.

```bash
kubectl -n bosofa scale statefulset/bosofa --replicas=0
kubectl -n bosofa wait --for=delete pod -l app.kubernetes.io/name=bosofa --timeout=120s
sleep 20   # the leader's 15 s lease must have expired
az storage blob restore -g <group> --account-name <account> --time-to-restore <UTC time>
kubectl -n bosofa scale statefulset/bosofa --replicas=3
```

Everything written after that time is gone, including machines enrolled and sessions started since.

On staging a restore of a very small container took 37 minutes, so plan for a long outage whatever the size.

### Daemons after a restore

Daemons reconnect by themselves and keep working, even though the restored server hands out IDs they have already seen. A command counts as a duplicate only with the same ID and issue time.

A session that existed at the restore point carries on, and its transcript gets a notice that part of it was lost. A session started after that moment no longer exists on the server. If the server later gives its ID to a new session, the daemon ends the old one and leaves any worktree in place.

## Telemetry

With `OTEL_EXPORTER_OTLP_ENDPOINT` set (for example `http://otel-collector.monitoring:4318`, protocol `http/protobuf`) the server exports traces, metrics and logs over OTLP. The other standard `OTEL_*` variables tune it. The meter `BoSoFa.Server` adds `bosofa.daemons.connected`, `bosofa.node.leader` and `bosofa.events.applied`.
