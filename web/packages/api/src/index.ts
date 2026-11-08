import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type User = components["schemas"]["UserDto"];
export type VersionInfo = components["schemas"]["VersionInfo"];
export type AuthProviders = components["schemas"]["AuthProviders"];
export type Machine = components["schemas"]["MachineDto"];
export type Enrollment = components["schemas"]["EnrollmentDto"];

/** Typed client of the BoSoFa server, generated from openapi.json (see `pnpm gen:api`). Same-origin, cookie auth. */
export const api = createClient<paths>({ baseUrl: "", credentials: "same-origin" });
