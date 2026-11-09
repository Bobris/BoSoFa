import { api, getPlatform, type Branding } from "@bosofa/api";
import { createSignal } from "solid-js";

/** What a server shows unless its configuration (BoSoFa:Branding) says otherwise. */
const defaults: Branding = { name: "BoSoFa", tagline: "Bobris Software Factory" };
const storageKey = "bosofa.branding";

function remembered(): Branding {
  try {
    const value = JSON.parse(localStorage.getItem(storageKey) ?? "null") as Branding | null;
    if (typeof value?.name === "string" && value.name && typeof value.tagline === "string") return value;
  } catch {
    // Storage blocked or unreadable: the defaults do.
  }
  return defaults;
}

const [branding, setBranding] = createSignal<Branding>(remembered());
document.title = branding().name;

function apply(value: Branding) {
  document.title = value.name;
  setBranding(value);
}

/** The server's names for itself (white labeling). The last ones seen show at once, so a reload does not flash the defaults. */
export { branding };

/** Asks the server once at start; a desktop app that has no server yet keeps what it last saw. */
export async function loadBranding(): Promise<void> {
  if (getPlatform().kind === "desktop" && !getPlatform().baseUrl) return;
  try {
    const { data } = await api.GET("/api/v1/branding");
    if (!data) return;
    apply(data);
    localStorage.setItem(storageKey, JSON.stringify(data));
  } catch {
    // Offline or storage blocked: keep what is shown.
  }
}
