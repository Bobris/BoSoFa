# Releasing

A release is one version of both products, the daemon (`bosofad`) and the desktop app, for every RID. The
[release workflow](../.github/workflows/release.yml) builds them, signs their manifests and publishes them to one BoSoFa
server. Computers install from that server's `/install` page and update from it (plan §5.7).

## The key

The release key is an ECDSA P-256 key. Its public half is compiled into every daemon and desktop build, which accept only
manifests it signed. Its private half is a PEM file that lives in one place: a GitHub secret of the `release` environment.

Losing it means installed computers can no longer be updated: they must be reinstalled from a build with a new key. Keep
one offline copy (a password manager or an encrypted drive) besides the secret. A leaked key lets its holder sign
updates, but they still reach computers only through a server where they hold an admin's API token.

## One-time setup

1. Create the key on a trusted computer. The command prints the public key.

   ```bash
   bosofad release-keygen --out bosofa-release.pem
   ```

2. In the repository's settings:
   - Variable `BOSOFA_RELEASE_PUBLIC_KEY`: the printed public key (repository level, because every build job embeds it).
   - Environment `release`, with yourself as a required reviewer and deployments limited to `main` and `v*` tags:
     - secret `BOSOFA_RELEASE_KEY`: the whole contents of `bosofa-release.pem`, including the BEGIN and END lines;
     - secret `BOSOFA_TOKEN`: an API token of a server admin with full control (Settings → API tokens);
     - variable `BOSOFA_SERVER`: the server's origin, such as `https://bosofa.example.com`.

   With the GitHub CLI:

   ```bash
   gh variable set BOSOFA_RELEASE_PUBLIC_KEY --body '<public key>'
   ```

   ```bash
   gh secret set BOSOFA_RELEASE_KEY --env release < bosofa-release.pem
   ```

   ```bash
   gh secret set BOSOFA_TOKEN --env release
   ```

   ```bash
   gh variable set BOSOFA_SERVER --env release --body https://bosofa.example.com
   ```

3. Move `bosofa-release.pem` to its offline copy and delete it from the computer.

## Making a release

Push a tag, or run the workflow by hand with a version and a channel:

```bash
git tag v1.2.0 && git push origin v1.2.0
```

```bash
gh workflow run release -f version=1.3.0-beta.1 -f channel=beta
```

A tag with a suffix (`v1.3.0-beta.1`) goes to the beta channel. The build jobs see no secrets. The publish job waits for
the reviewer's approval, then uploads the files and signs with the Linux daemon it just built, which refuses a key other
than the public key it embeds. Daemons follow the server's rollout; desktop apps take the newest stable release within six
hours, or at their next start.

## Without the workflow

`bosofad release-publish` also signs with a local file (`--key bosofa-release.pem`) or with the PEM text in
`BOSOFA_RELEASE_KEY`. The API token comes from `BOSOFA_TOKEN` either way.

## Replacing the key

Builds trust exactly one public key. To replace it, build one release with the new public key and sign it with the old
key, let every computer take it, then swap the secret and the variable for the new key. The workflow refuses that
mismatch, so publish the bridge release by hand, with a `bosofad` built without `BoSoFaReleasePublicKey` and
`--key` pointing at the old key. A computer that skipped the bridge release must be reinstalled from `/install`.
