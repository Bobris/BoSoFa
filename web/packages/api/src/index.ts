import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type User = components["schemas"]["UserDto"];
export type VersionInfo = components["schemas"]["VersionInfo"];
export type AuthProviders = components["schemas"]["AuthProviders"];
export type Machine = components["schemas"]["MachineDto"];
export type Enrollment = components["schemas"]["EnrollmentDto"];
export type Session = components["schemas"]["SessionDto"];
export type SessionStatus = Session["status"];
export type SessionItem = components["schemas"]["SessionItemDto"];
export type RuntimeRequest = components["schemas"]["RuntimeRequestDto"];
export type CreateSession = components["schemas"]["CreateSessionRequest"];
export type HarnessAccount = components["schemas"]["HarnessAccountDto"];
export type AccountKind = HarnessAccount["kind"];
export type McpServer = components["schemas"]["McpServerDto"];
export type SkillBundle = components["schemas"]["SkillBundleDto"];
export type Profile = components["schemas"]["ProfileDto"];
export type LiveClientMessage = components["schemas"]["LiveClientMessage"];
export type LiveServerMessage = components["schemas"]["LiveServerMessage"];

/** Typed client of the BoSoFa server, generated from openapi.json (see `pnpm gen:api`). Same-origin, cookie auth. */
export const api = createClient<paths>({ baseUrl: "", credentials: "same-origin" });
