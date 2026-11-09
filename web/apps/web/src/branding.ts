import { api, getPlatform, type Branding } from "@bosofa/api";
import { createSignal } from "solid-js";

const storageKey = "bosofa.branding";

/** The last names seen, so a reload does not flash anything else; else the desktop app's own build brand; else nothing
 * until the server answers. */
function initial(): Branding {
  try {
    const value = JSON.parse(localStorage.getItem(storageKey) ?? "null") as Branding | null;
    if (typeof value?.name === "string" && value.name && typeof value.tagline === "string" && typeof value.daemon === "string")
      return value;
  } catch {
    // Storage blocked or unreadable.
  }
  return getPlatform().branding ?? { name: "", tagline: "", daemon: "" };
}

const [branding, setBranding] = createSignal<Branding>(initial());
if (branding().name) document.title = branding().name;

/** The server's names for itself (white labeling): `BoSoFa:Branding` and the build's brand, which names the daemon. */
export { branding };

/** Asks the server once at start; a desktop app that has no server yet keeps its own names. */
export async function loadBranding(): Promise<void> {
  if (getPlatform().kind === "desktop" && !getPlatform().baseUrl) return;
  try {
    const { data } = await api.GET("/api/v1/branding");
    if (!data) return;
    document.title = data.name;
    setBranding(data);
    localStorage.setItem(storageKey, JSON.stringify(data));
  } catch {
    // Offline or storage blocked: keep what is shown.
  }
}
