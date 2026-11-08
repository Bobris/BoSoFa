import { api, type User } from "@bosofa/api";

/** The signed-in user, or null when the server answers 401. */
export async function fetchMe(): Promise<User | null> {
  const { data, response } = await api.GET("/api/v1/me");
  if (response.status === 401) return null;
  if (!data) throw new Error(`Loading the current user failed (${response.status}).`);
  return data;
}

export async function logout(): Promise<void> {
  await api.POST("/auth/logout");
}
