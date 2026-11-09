import type { Platform } from "@bosofa/api";
import { auth, files, localDaemon, updates } from "#neoastra";

/** Refreshing a little early, so a token does not run out on the way to the server. */
const expiryMarginMs = 60_000;

/** The server, sign-in and tokens belong to the C# host. The page keeps access tokens in memory only. */
export async function createDesktopPlatform(): Promise<Platform> {
  const state = await auth.state({});
  let token: { value: string; expiresAt: number } | undefined;
  let pending: Promise<string | undefined> | undefined;

  async function fetchToken(): Promise<string | undefined> {
    const { accessToken, expiresAt } = await auth.accessToken({});
    if (!accessToken) {
      token = undefined;
      return undefined;
    }
    token = { value: accessToken, expiresAt: Date.parse(expiresAt!) };
    return accessToken;
  }

  return {
    kind: "desktop",
    baseUrl: state.signedIn ? state.server!.replace(/\/$/, "") : "",
    server: state.server ?? undefined,
    branding: state.branding,
    async authorization() {
      if (!state.signedIn) return undefined;
      let value = token && token.expiresAt - Date.now() > expiryMarginMs ? token.value : undefined;
      if (!value) {
        pending ??= fetchToken().finally(() => (pending = undefined));
        value = await pending;
      }
      return value ? `Bearer ${value}` : undefined;
    },
    async saveBlob(sha256, name) {
      await files.saveBlob({ sha256, name });
    },
    async signIn(server) {
      return (await auth.signIn({ server })).error ?? undefined;
    },
    async signOut() {
      await auth.signOut({});
      location.href = "/";
    },
    localDaemon: {
      status: () => localDaemon.status({}),
      enable: async () => (await localDaemon.enable({})).error ?? undefined,
      disable: async () => (await localDaemon.disable({})).error ?? undefined,
      addClaudeAccount: async (label) => (await localDaemon.addClaudeAccount({ label })).error ?? undefined,
    },
    updates: {
      check: () => updates.check({}),
      install: async () => (await updates.install({})).error ?? undefined,
    },
  };
}
