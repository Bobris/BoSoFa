import { setPlatform } from "@bosofa/api";
import { createDesktopPlatform } from "./platform";

// The platform comes first: the app reads it as it renders.
setPlatform(await createDesktopPlatform());
await import("../../web/src/index");
