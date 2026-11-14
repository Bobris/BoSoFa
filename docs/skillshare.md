# SkillShare: sign-in, skills and MCP servers

Status: milestone M10 in [plan.md](plan.md). BoSoFa's side, steps 1–5 of §8, is built (2026-11-14) and tested against
`FakeSkillShare`, which plays the contract of §11; SkillShare's side, planned in its own repository
(`docs/bosofa-connection-plan.md` there, cited here as "SkillShare: <section>"), is being built separately, so nothing has run
against a real SkillShare yet. Where the build differs from the plan, the text below says so.

## 1. What and why

SkillShare (`~/Research/skillshare`) is the company catalog of agent skills. It also serves MCP servers (YouTrack, Azure
DevOps, Teams, Zendesk, Jira, Salesforce, Pendo, Work, News) with **each person's own** upstream credentials already
stored in it. Today a BoSoFa admin uploads skill zips by hand (`POST /api/v1/skills`). An MCP server in the catalog
carries one value per secret name, so a person's own YouTrack token never reaches their session.

After this feature:

- **People sign in to BoSoFa through SkillShare.** SkillShare becomes BoSoFa's OIDC provider and itself signs people
  in through Entra, as it does today. BoSoFa needs no Entra app registration of its own.
- **A SkillShare admin decides** whether BoSoFa may do this, by registering it as a connected app (§2.2). That
  registration is the connection between the two products. A BoSoFa admin then puts its client id and secret into
  BoSoFa's configuration.
- **Every sign-in is the person's own delegation.** BoSoFa receives a refresh token for that person, limited to the
  scopes SkillShare allows the app. BoSoFa only ever acts for people who signed in to it. It never acts for anyone else.
- **Every member can pick any SkillShare skill** for a session, in the composer or through a profile. BoSoFa mirrors
  the catalog and turns a picked version into an ordinary skill bundle, so the daemon and the harnesses work as today.
- **Every member's SkillShare MCP servers are available in their sessions**: on any harness, and in the server agent.
  BoSoFa exchanges the person's refresh token for a short-lived token for one server, just in time. SkillShare's
  per-person access modes (read-only, full, limited) apply as they do when Claude Code connects to SkillShare directly.

Not in scope:

- publishing skills from BoSoFa to SkillShare;
- SkillShare's own agent sessions and listeners, which are a separate product path;
- more than one SkillShare per BoSoFa.

BoSoFa keeps working with any other OIDC provider. In that case the SkillShare features stay hidden.

## 2. Sign-in through SkillShare

### 2.1 Why this shape

- **The person's own grant.** The trust is the one OAuth already gives: the person's own grant, made at sign-in, to an
  app a SkillShare admin registered. This needs no server-to-server impersonation, no key pairing, and no matching of
  `sub`s across two Entra apps.
- **One place for identity.** SkillShare already maps Entra people to its users, disables people who have not signed in
  for 60 days, and keeps per-person credentials. BoSoFa simply reuses that identity.
- **Rejected alternatives:**
  - Both products sharing one Entra app, with a server key pair (the first draft of this plan). BoSoFa could then act
    as any person, including people who never signed in to it.
  - Entra on-behalf-of. SkillShare refuses Entra access tokens on purpose.

### 2.2 Registration (the admin step)

1. A SkillShare admin opens Connected apps, then Register (SkillShare: Web UX). They enter:
   - the name;
   - the redirect URI, `https://<bosofa>/signin-oidc`;
   - the scopes, `openid profile email offline_access skills.read mcps.read mcp.use`;
   - Trusted, which means no consent screen.
2. SkillShare shows the client id and the secret once, and keeps only the secret's hash.
3. A BoSoFa admin sets the same keys as for any OIDC provider today:
   - `BoSoFa:Oidc:Authority = https://skillshare…`
   - `BoSoFa:Oidc:ClientId`
   - `BoSoFa:Oidc:ClientSecret` (a Key Vault secret on AKS)
   - `BoSoFa:Oidc:DisplayName = SkillShare`
4. BoSoFa reads SkillShare's discovery document. Its `skillshare_connect_api` entry switches the SkillShare features
   on (§4, §5).

What protects what:

- Without a SkillShare admin, no app can sign people in or get tokens.
- Without a BoSoFa admin, the configuration does not change.
- Either side can end it:
  - a SkillShare admin suspends or revokes the app, which ends every grant at once;
  - BoSoFa removes the configuration.

### 2.3 Identity and claims

- `sub` is SkillShare's own stable user id, the same for every app. The ID token also carries `email`, `name`, `oid`,
  `tid` and `skillshare_admin`.
- BoSoFa keys users by `(iss, sub)` as today (`UserSignedIn`). It now also records `oid` and `tid` on `User`, for
  migration (§2.5) and display.
- **Admin role.** Today BoSoFa's first user becomes admin, and nothing can change a role later.
  - With `BoSoFa:Oidc:AdminFromSkillShare = true`, the role follows `skillshare_admin` at every sign-in.
  - That gives BoSoFa role management for free, and it matches "an admin of both" (decided, §9). The setting is on in
    deployments that sign in through SkillShare.
- SkillShare refuses to sign in disabled people, so BoSoFa refuses them too.
- Dev login stays as it is. Its people get no SkillShare features.

### 2.4 The grant

- **Scopes and storage.**
  - The OIDC handler asks for `offline_access` and the scopes of §2.2.
  - `OnTokenValidated` takes the refresh token from `TokenEndpointResponse` and protects it with Data Protection:
    purpose `BoSoFa.SkillShareGrant.v1`, with the user id as sub-purpose.
  - `UserSignedIn` carries the ciphertext into a `SkillShareGrant` row. `SaveTokens` stays false, so no token goes into
    the cookie.
- **Lifetime and rotation.**
  - A refresh token issued to a confidential client does not rotate (SkillShare: Decisions). Using it extends its
    expiry, 90 days from last use, so BoSoFa writes a new ciphertext only when someone signs in.
  - Each sign-in replaces the grant.
  - The ciphertexts stay in the event log forever. As for accounts (plan §5.4.1), the key ring is what keeps them
    unreadable.
- **When the grant stops working.** SkillShare answers `invalid_grant` when:
  - the person revoked BoSoFa in SkillShare;
  - an admin revoked the app;
  - the person was disabled;
  - the token simply expired.

  BoSoFa then marks the grant `Expired`. The UI asks the person to sign in again, and their sessions get an item saying
  so.
- **Desktop.** Its sign-in goes through the server's own login (`/auth/desktop/start`), so it stores the grant the same
  way.

### 2.5 Moving existing users

Switching the authority changes every user's `(iss, sub)`, so without care everyone would become a new user and lose
their machines, sessions and accounts.

- `BoSoFa:Oidc:MigrateFromIssuer` names the previous issuer.
- On someone's first SkillShare sign-in, if no user has the new `(iss, sub)`, the handler looks for a user of the old
  issuer:
  1. first by `oid`, recorded from the old provider by step 1 (§8) when it was Entra;
  2. then by the email SkillShare verified.
- The match takes over that user's key and is audited (`user.migrated`). Each old user can be taken over only once.
- After everyone has moved, remove the setting.

### 2.6 Availability

Signing in to BoSoFa now needs SkillShare, which runs one replica. When SkillShare is down:

- these keep working: running sessions, daemons (their own tokens), desktop apps (BoSoFa's own rotating refresh
  tokens), API tokens, and browser cookies until they expire (14 days, sliding);
- these wait: new browser sign-ins, and new SkillShare tokens for skills and MCP servers.

## 3. Domain

New relations go in `src/BoSoFa.Server.Domain/SkillShare.cs`. The whole feature needs one `ApplicationGeneration` bump.

| Relation | Fields |
| --- | --- |
| `SkillShareGrant` | `UserId`, `ProtectedRefreshToken`, `Scopes`, `GrantedAt`, `Status` (`Active`, `Expired`) |
| `SkillShareSkill` | `Id` (SkillShare's), `Name`, `BundleName` (its folder on machines), `Description`, `Tags`, `Versions` (version, published at, latest, content SHA-256), `Deprecated`, `Deleted`, `Owner` (name) |
| `SkillShareSync` | one row: `Cursor` (SkillShare's time of the last read), `LastSyncAt`, `LastError` |
| `SkillShareMcpChoice` | `UserId`, `Off` (providers the person turned off; everything else they connected is on) |

Changes to what exists:

- `User` gains `ObjectId` and `TenantId`.
- `UserSignedIn` gains the grant's ciphertext and `IsAdmin` (from the claim, when configured).
- `Session` gains `Skills` (its own skills, SkillShare's with "latest" resolved at creation) and `SkillShareMcp` (the
  creator's SkillShare servers it gets, with their URLs). A session with either fetches a launch profile even without a
  profile.
- `SkillRef` gains `Source`: null for a bundle of this server's catalog, `skillshare` for a SkillShare skill, whose `Name` is
  then its SkillShare id and whose `Version` may be `latest`. A profile names a SkillShare server as `skillshare:<provider>`.
- *Built differently:* `SkillBundle` is unchanged. A SkillShare version is not a bundle row: its archive's SHA-256 is the
  catalog's, so it is a blob of that name, and `SkillShareSkill` resolves it. Its folder is the skill's name made a catalog
  name (`Grafana fetch` → `grafana-fetch`); when two skills of a launch share a folder name, the first wins: the profile's
  own bundles, then the session's.
- New events: `SkillShareGrantExpired`, `SkillShareCatalogSynced` (only the skills that changed),
  `SkillShareMcpChoiceSaved`. Migration and the role are part of `UserSignedIn` (fields `MigrateFromIssuer`,
  `ProviderSaysRole`, `ProviderSaysAdmin`, `ObjectId`, `TenantId`, and the grant's `GrantId`, `ProtectedGrant`), audited as
  `user.migrated` and `user.role`.

Access tokens from SkillShare are **never** written to events, relations, logs or blobs. They live only in a memory
cache on each node (§5.2).

## 4. Skills

### 4.1 Mirroring the catalog

- The BTDB backend action `skillshare-catalog-sync`, hosted by `BackendActionsService` as AGENTS.md requires, runs
  every 10 minutes (`[RepeatedlyScheduled(10)]`), on "Sync now", and once at the first sign-in through SkillShare.
- It gets an app token through `client_credentials` with scope `skills.read`, then calls
  `GET /api/connect/v1/skills?changedSince=`.
- It publishes `SkillShareCatalogSynced` with only what changed, so an unchanged catalog writes nothing to the event
  log, which is kept forever.
- Deleted skills are marked as gone, not removed. Profiles that name them then say so instead of failing silently.
- An unchanged catalog moves the cursor at most hourly, and a failure is recorded once, not at every attempt.

### 4.2 From a SkillShare version to a bundle

- **Import.** A version's archive reaches the blob store the first time a session starts with it.
  - The session's endpoint fetches `GET /api/connect/v1/skills/{id}/versions/{v}/archive`: a zip with `SKILL.md` at its
    root (SkillShare: API).
  - It checks the zip's SHA-256 against the catalog's and stores it as the blob of that hash. No event: the blob's name is
    the catalog's hash already.
  - *Built differently:* no backend action and no "Import" button. Fetching one zip is short, request-driven work, and it
    happens at most once per version, guarded per hash on each node.
- **From there nothing changes.** `SessionProfiles` downloads the bundle, checks it and snapshots it. Claude Code gets
  it through `--plugin-dir`, Codex through `$CODEX_HOME/skills`.
- **A version not imported yet.** A session start that needs it waits for the import, at most 30 s. After that the
  session fails with a clear item.
- **Fetched once.** The archive is content-addressed, so each version is fetched once per BoSoFa, not once per machine.
- **Statistics.** Each session that uses a skill reports a download with the person's token, so SkillShare's
  statistics count BoSoFa's use per person.

### 4.3 Choosing skills

- **Profiles** (admins) may list SkillShare skills. Decided 2026-11-14:
  - by default a skill means `latest`, resolved when a session starts;
  - pinning a version is optional.
- **Sessions:**
  - The composer gets a Skills picker below the prompt, next to the profile. It searches local and SkillShare skills
    by name, description and tag.
  - `CreateSessionRequest.Skills`, and the draft's JSON, add skills to the profile's.
  - Every member may pick any skill, as in SkillShare.
- **Snapshots.** A session's skills are fixed at its start (plan §5.5), so a new SkillShare version never changes a
  running session.
- **Gap kept from today.** OpenCode (ACP) gets no skills. When it is the harness, the composer says so.

## 5. MCP servers

### 5.1 A person's servers

- **Listing.** `GET /api/v1/skillshare/mcps` calls SkillShare's `GET /api/connect/v1/me/mcps` with the person's
  `mcps.read` token. For each server it returns:
  - id, title and URL;
  - whether the person connected it in SkillShare;
  - its access mode.

  Each node keeps the answer for 5 minutes per person.
- **Defaults.** Decided 2026-11-14: every server the person connected in SkillShare is on by default in all their
  sessions.
  - The person can turn servers off on the Accounts page (`SkillShareMcpChoice`).
  - Each session can untick or tick servers in the composer.
  - Servers not yet connected link to SkillShare's `/mcps`.
- **Profiles** may name `skillshare:<provider>` too. If the creator has not connected that server, the session starts
  without it. *Not built yet:* a transcript item saying so.

### 5.2 Token broker

`SkillShareTokens` in the server:

- **Methods:**
  - `AppTokenAsync()` uses `client_credentials` with the client secret, for skills.
  - `UserTokenAsync(userId, resource)` uses the person's refresh token with `resource=<MCP URL>` (RFC 8707). SkillShare
    returns an access token for that one server.
- **Caching.** Tokens are cached on each node until 5 minutes before they expire, and are not shared between nodes.
  Since the refresh token does not rotate, any node may use it at any time, with no lock.
- **Refusals.** `invalid_grant` marks the grant `Expired` (§2.4). An unknown server gets a typed error.
- **Audit.** User tokens are audited as `skillshare.token`: the person, the server, the session. There is one entry per
  session and server, so long sessions do not flood the audit.

### 5.3 Delivery to sessions

- **The profile.** The resolved profile (`SessionProfileResponse`) lists SkillShare servers as HTTP servers. Their
  `Authorization` value is a new reference, `${skillshare-token:<provider>}`, which the server does **not** resolve into
  the response.
- **The gateway.** The daemon's gateway (`McpGateway`, `/mcp/<session>/<server>`) asks
  `POST /api/daemon/v1/sessions/{id}/skillshare-tokens/{provider}` for a token:
  - when it first calls the server;
  - when its token is within 5 minutes of expiring;
  - once more after a 401.
- **The server's checks.** It checks what the account lease checks (the session runs on this machine and is not
  stopped). It also checks that the provider is in the session's resolved set. Then it asks the broker for the
  **session creator's** token.
- **When there is none** (the grant was revoked, say), the gateway answers the harness 502 with the reason, never a 401 that
  could start an OAuth sign-in.
- **What the harness sees.** Only the loopback gateway with the session bearer. No SkillShare token reaches a model, a
  shell or a config file (secrets.md §6). The daemon adds each token to the masker anyway.
- **The server agent** resolves the same reference in-process for `McpHttpClient`, with the same refresh after a 401.
- **Tool names** stay as each harness makes them (`mcp__youtrack__search_issues` for Claude Code). The server id is the
  provider id.

## 6. API and UI

- **API:**
  - `GET /api/v1/skillshare` (everyone): whether the server signs in through SkillShare, and the person's grant; for
    admins also the last sync, its error, and how many people have an active grant.
  - `POST /api/v1/skillshare/sync` (admins).
  - `GET /api/v1/skillshare/skills` (members; the UI filters).
  - `GET /api/v1/skillshare/mcps` and `PUT /api/v1/skillshare/mcp-choice` (the person's own).
  - `CreateSessionRequest.Skills` and `.SkillShareMcpServers` (null: the person's defaults).
  - Daemon: `POST /api/daemon/v1/sessions/{id}/skillshare-tokens/{provider}?fresh=`.
- **UI:**
  - The catalog page lists SkillShare skills; admins see the status above there with "Sync now" (*built differently:* no
    page of its own), and the profile form offers SkillShare skills and servers.
  - The composer gets the Skills picker and the SkillShare servers.
  - The Accounts page gets a SkillShare section: who the person is signed in as, the grant's state ("sign in again"),
    and their servers with on/off switches.
- **Naming.** UI names come from branding, never a hard-coded `BoSoFa` (AGENTS.md). SkillShare's name is shown as its
  discovery document gives it.
- **Contracts.** `openapi.json` and `schema.d.ts` are regenerated as usual.

## 7. Testing

- **`FakeSkillShare`** (`tests/BoSoFa.Server.Tests/FakeSkillShare.cs`) is an OIDC provider on a loopback port:
  - discovery, JWKS, and ID tokens signed with ES256;
  - authorize (trusted, so no screen), and a token endpoint with `authorization_code`, `refresh_token` + `resource`,
    and `client_credentials`;
  - the connect API (skills, archives, `me/mcps`);
  - an HTTP MCP server that requires its tokens.

  It plays the contract of §11, so a change there shows up as a failing test here. Its authorize endpoint signs in a
  chosen person at once; a browser gets the form post a real provider answers with, so it can also run by hand for the UI.
- **`SkillShareTest`** covers (built):
  - sign-in storing a grant, the role following the claim, and no refresh token in the server's files in plain text;
  - migration from the previous issuer by email, where the old user is taken over once;
  - the catalog sync, a profile naming SkillShare skills and servers (and one naming a skill that is not there);
  - a replay session that gets the latest version of a picked skill, counts the download for its person, and reaches its
    person's YouTrack through the gateway; an expired token replaced by a fresh one; a revoked grant ending it (502, the
    grant marked expired); another machine refused the session's token;
  - the server agent's MCP client asking for a fresh token after a 401.
- *Not built yet:* the cluster test asking for a user token on another node, and a migration by `oid`.
- **One manual run** against preprod SkillShare (`https://skillshare.quadientcloudpreprod.eu`) once its side is
  deployed. The UI was tried by hand against `FakeSkillShare` (2026-11-14): sign-in, the Accounts section and its switches,
  the catalog's SkillShare skills, and a session started from the composer with a picked skill and the person's server.

## 8. Steps

Each step needs the named SkillShare phase deployed first, except when run against `FakeSkillShare`. Steps 1–5 are built
against `FakeSkillShare` (2026-11-14); none has run against a real SkillShare yet.

1. **Record `oid` and `tid`** at sign-in from the current provider, in a release made before the switch, so that
   migration can match by `oid`. *Built.*
2. **Sign-in through SkillShare** (SkillShare phase 1):
   - the grant captured at sign-in, the admin claim, and migration;
   - `FakeSkillShare`;
   - [deploy-single-node.md](deploy-single-node.md) and [deploy-aks.md](deploy-aks.md) explaining the registration.

   *Done when* staging signs in through preprod SkillShare and existing users keep their machines and sessions.
   *Built*; waits for SkillShare phase 1.
3. **Token broker** (SkillShare phase 2): app and user tokens, the cache, `invalid_grant`, and audit. *Built.*
4. **Skills** (SkillShare phase 3):
   - the sync and import backend actions, and the bundle source;
   - the catalog page, profile references, and the composer's Skills picker.

   *Done when* a member picks a SkillShare skill in the composer and a Claude Code session on a machine uses it.
   *Built* (without the backend action for imports, §4.2); waits for SkillShare phase 3.
5. **MCP servers** (SkillShare phase 2):
   - listing and choices, and the Accounts section;
   - the composer, the gateway's token endpoint, and the server agent.

   *Done when* a member's Claude Code session searches their YouTrack through SkillShare with their own access mode,
   and the server agent does the same with no machine. *Built*; waits for SkillShare phase 2. The server agent's side is
   covered by its MCP client's test, not by an agent session.
6. **Polish:**
   - errors explained in the UI;
   - usage on the admin page;
   - Codex and OpenCode runs with SkillShare servers.

*M10 is done when*:

- people sign in through SkillShare;
- a member starts a Claude Code session with a SkillShare skill and their own SkillShare MCP servers;
- revoking BoSoFa in SkillShare stops that person's next MCP call.

## 9. Decisions

Taken 2026-11-14:

1. **Sign-in.** SkillShare is BoSoFa's sign-in provider. A SkillShare admin approves BoSoFa by registering it, and each
   person's sign-in is their grant. This replaces the shared Entra app with a paired key.
2. **MCP defaults.** Every server a person connected in SkillShare is on by default, and each session can change that.
3. **Skill versions.** Profiles use `latest` at session start. Pinning is optional.
4. **YouTrack tokens.** SkillShare's Base64 YouTrack tokens are fixed separately and do not block this.
5. **Admin role.** It follows SkillShare's admins (`AdminFromSkillShare`). BoSoFa has no way to change roles today, and
   "an admin of both" is the intent.
6. **Migration by email.** Email is the fallback when `oid` is missing: only for users of the previous issuer, each
   once, audited.

## 10. Risks

| Risk | Mitigation |
| --- | --- |
| Sign-in depends on SkillShare, which runs one replica | Everything already signed in keeps working (§2.6). Cookies last 14 days. |
| BoSoFa holds a refresh token for everyone who signed in | Protected like accounts (Data Protection, the key ring in Key Vault). Limited to the app's scopes. Revocable by the person, or for everyone by a SkillShare admin. Access tokens last at most 1 hour and are kept in memory only. |
| Migration links the wrong person | `oid` first. Email only within the old issuer, once per user, audited. |
| A large catalog or many sessions load SkillShare | Only changes are synced. Each archive is fetched once per version. Tokens are cached until near expiry. |
| SkillShare's contract changes | A versioned `/api/connect/v1`, and `FakeSkillShare` plays the contract in CI. |

## 11. Wire contract

What BoSoFa sends and reads, so that SkillShare's side can be built to it. `FakeSkillShare` plays exactly this. BoSoFa reads
JSON tolerantly: unknown fields are ignored, and the alternatives named below are accepted too.

**Discovery**, `GET <authority>/.well-known/openid-configuration` (OIDC Discovery), plus:

| Field | Meaning |
| --- | --- |
| `skillshare_connect_api` | The connect API's base URL, `https://<host>/api/connect/v1`. Its presence is what makes BoSoFa treat the provider as SkillShare. |
| `skillshare_mcp_resources` | The MCP servers' URLs (informational). |
| `skillshare_name` | Optional: the name the UI shows; `SkillShare` without it. |
| `skillshare_mcp_setup_url` | Optional: where a person connects their tools; `<authority>/mcps` without it. |

`issuer` must be the authority exactly as configured in BoSoFa (no trailing slash), and `jwks_uri` must serve the ES256 key.

**Sign-in** (authorization code with PKCE S256, `response_mode=form_post`, client authentication `client_secret_basic` on the
token endpoint). BoSoFa asks for `openid profile email offline_access skills.read mcps.read mcp.use`. The ID token carries
`sub`, `email`, `name`, `nonce`, and optionally `oid`, `tid` and `skillshare_admin` (a JSON boolean). The token response's
`refresh_token` is stored; `scope` is recorded when present.

**Tokens**, `POST <token_endpoint>`, form-encoded, always with `client_secret_basic`:

| Request | Answer |
| --- | --- |
| `grant_type=client_credentials&scope=skills.read` | an app token for the skills endpoints |
| `grant_type=refresh_token&refresh_token=…&scope=mcps.read skills.read` (no `resource`) | a person's token for the connect API |
| `grant_type=refresh_token&refresh_token=…&scope=mcp.use&resource=<MCP URL>` | a person's token for that MCP server |

The answer is `{ "access_token", "token_type": "Bearer", "expires_in" }`; the refresh token does not rotate, so no new one is
expected. Errors are `{ "error", "error_description" }`: `invalid_grant` marks the person's grant expired in BoSoFa;
`invalid_client` and `unauthorized_client` mean the app itself is refused; `invalid_target` and `invalid_scope` refuse the
request only.

**Connect API**, bearer tokens as above:

- `GET skills` and `GET skills?changedSince=<ISO 8601>` (app token) → `{ "skills": [ … ], "now": "<ISO 8601>" }`; a bare array
  is accepted too. Each skill: `id` (string or number), `name`, `description`, `tags` (strings, or objects with `slug` or
  `name`), `owner` (a string, or an object with `name`; `ownerName` also works), `deprecated`, `deleted`, and `versions`:
  `[{ "version", "publishedAt", "latest", "contentSha256" }]` with every version of a skill that changed, not only the new
  one. `now` is SkillShare's clock, the next `changedSince`; without it BoSoFa uses its own minus two minutes.
- `GET skills/{id}/versions/{version}/archive` (app token) → the zip whose SHA-256 (lowercase hex) is `contentSha256`, with
  `SKILL.md` at its root; at most 64 MiB.
- `POST skills/{id}/versions/{version}/downloads` (a person's token, body `{}`) → any 2xx; counts a use for that person.
- `GET me/mcps` (a person's token) → `{ "mcps": [ … ] }` (a bare array, or `providers`, are accepted too). Each server: `id`
  (BoSoFa takes only lowercase letters, digits, `-` and `_`), `title`, `url` (the exact resource for its tokens),
  `connected`, `accessMode`, `setupUrl`.

**MCP servers** take the person's token as `Authorization: Bearer` and answer 401 when it is no longer valid; BoSoFa then asks
once for a fresh one.
