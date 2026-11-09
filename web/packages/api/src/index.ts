import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type User = components["schemas"]["UserDto"];
export type VersionInfo = components["schemas"]["VersionInfo"];
export type AuthProviders = components["schemas"]["AuthProviders"];
export type Branding = components["schemas"]["Branding"];
export type Machine = components["schemas"]["MachineDto"];
export type Enrollment = components["schemas"]["EnrollmentDto"];
export type Session = components["schemas"]["SessionDto"];
export type SessionStatus = Session["status"];
export type SessionItem = components["schemas"]["SessionItemDto"];
export type RuntimeRequest = components["schemas"]["RuntimeRequestDto"];
export type CreateSession = components["schemas"]["CreateSessionRequest"];
export type HarnessAccount = components["schemas"]["HarnessAccountDto"];
export type AccountKind = HarnessAccount["kind"];
/** As stored in a user item's data: the generated schema has it only inside that JSON. */
export type PromptImage = { sha256: string; mediaType: string; size: number };
export type AccountUsage = components["schemas"]["AccountUsageDto"];
export type UsageWindow = components["schemas"]["UsageWindow"];
export type McpServer = components["schemas"]["McpServerDto"];
export type SkillBundle = components["schemas"]["SkillBundleDto"];
export type Profile = components["schemas"]["ProfileDto"];
export type FileEntry = components["schemas"]["FileEntry"];
export type Terminal = components["schemas"]["TerminalDto"];
export type ApiToken = components["schemas"]["ApiTokenDto"];
export type AuditEntry = components["schemas"]["AuditEntryDto"];
export type Release = components["schemas"]["ReleaseDto"];
export type ProductDownload = components["schemas"]["ProductDownload"];
export type DaemonRollout = components["schemas"]["DaemonRolloutDto"];
export type LiveClientMessage = components["schemas"]["LiveClientMessage"];
export type LiveServerMessage = components["schemas"]["LiveServerMessage"];

/** How this app reaches the server: the web page by same-origin cookie, the desktop app by bearer token across origins. */
export interface Platform {
  readonly kind: "web" | "desktop";
  /** Prefix of every server path; empty for same-origin. */
  readonly baseUrl: string;
  /** The Authorization header, when requests need one and someone is signed in. */
  authorization(): Promise<string | undefined>;
  /** Saves a blob of the server as a file for the person using this app. */
  saveBlob(sha256: string, name: string): Promise<void>;
  signOut(): Promise<void>;
  /** The desktop's own sign-in; absent on the web, whose login page signs in. */
  signIn?(server: string): Promise<string | undefined>;
  /** The server this desktop app last used. */
  readonly server?: string;
  /** Desktop only: this computer's daemon. Each action gives back what went wrong, if anything. */
  readonly localDaemon?: {
    status(): Promise<{ enrolled: boolean; installed: boolean }>;
    enable(): Promise<string | undefined>;
    disable(): Promise<string | undefined>;
    addClaudeAccount(label: string): Promise<string | undefined>;
  };
  /** Desktop only: this app's own updates, which come from the server it signs in to. */
  readonly updates?: {
    check(): Promise<{ current?: string | null; supported: boolean; available?: string | null; ready: boolean; problem?: string | null }>;
    /** Closes the app and starts the new version; what went wrong otherwise. */
    install(): Promise<string | undefined>;
  };
}

function createApi(platform: Platform) {
  const client = createClient<paths>({ baseUrl: platform.baseUrl, credentials: platform.kind === "web" ? "same-origin" : "omit" });
  client.use({
    async onRequest({ request }) {
      const authorization = await platform.authorization();
      if (authorization) request.headers.set("Authorization", authorization);
      return request;
    },
  });
  return client;
}

const webPlatform: Platform = {
  kind: "web",
  baseUrl: "",
  authorization: () => Promise.resolve(undefined),
  saveBlob(sha256, name) {
    const link = document.createElement("a");
    link.href = `/api/v1/blobs/${sha256}`;
    link.download = name;
    link.click();
    return Promise.resolve();
  },
  async signOut() {
    await api.POST("/auth/logout");
  },
};

let current = webPlatform;

/** Typed client of the BoSoFa server, generated from openapi.json (see `pnpm gen:api`). */
export let api = createApi(webPlatform);

/** The desktop entry calls this before anything renders. */
export function setPlatform(platform: Platform): void {
  current = platform;
  api = createApi(platform);
}

export function getPlatform(): Platform {
  return current;
}

/** Stores a file in the server's blob store and returns its hash; the error text when that fails. */
export async function uploadBlob(file: Blob): Promise<{ sha256?: string; error?: string }> {
  const authorization = await current.authorization();
  const response = await fetch(`${current.baseUrl}/api/v1/blobs`, {
    method: "POST",
    body: file,
    headers: authorization ? { Authorization: authorization } : undefined,
  });
  if (!response.ok) return { error: (await response.text()) || `The upload failed (${response.status}).` };
  return { sha256: ((await response.json()) as { sha256: string }).sha256 };
}

const blobImages = new Map<string, Promise<string | undefined>>();

/** An address an <img> can show. It is fetched first because the desktop's bearer cannot go on an image request. */
export function blobImageUrl(sha256: string, mediaType: string): Promise<string | undefined> {
  let url = blobImages.get(sha256);
  if (!url) {
    url = (async () => {
      const authorization = await current.authorization();
      const response = await fetch(`${current.baseUrl}/api/v1/blobs/${sha256}`, {
        headers: authorization ? { Authorization: authorization } : undefined,
      });
      return response.ok ? URL.createObjectURL(new Blob([await response.arrayBuffer()], { type: mediaType })) : undefined;
    })();
    blobImages.set(sha256, url);
  }
  return url;
}

/** The address of the live socket. A browser cannot put a bearer on a WebSocket, so the desktop gets a one-time ticket. */
export async function liveSocketUrl(): Promise<string> {
  const url = new URL(`${current.baseUrl}/api/v1/live`, location.href);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  if (current.kind === "desktop") {
    const { data } = await api.POST("/api/v1/live/ticket");
    if (!data) throw new Error("No ticket for the live connection.");
    url.searchParams.set("ticket", data.ticket);
  }
  return url.toString();
}
