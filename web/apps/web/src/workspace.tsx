import { api, type EnvironmentApproval, type MachineTerminal, type Session, type SessionDraft, type User } from "@bosofa/api";
import { createContext, createMemo, createSignal, onSettled, refresh, useContext } from "solid-js";
import type { JSX } from "@solidjs/web";
import { fetchMe } from "./session";

/** One entry of the sidebar: a session, a draft or a running terminal, ordered by when it last did something. */
export type Activity =
  | { kind: "session"; key: string; at: number; title: string; session: Session }
  | { kind: "draft"; key: string; at: number; title: string; draft: SessionDraft }
  | { kind: "terminal"; key: string; at: number; title: string; terminal: MachineTerminal };

function workspace() {
  const me = createMemo(() => fetchMe());
  // Each list reads me() before its first await, so it loads only for a signed-in person and follows sign-in changes.
  const sessions = createMemo(async (): Promise<Session[]> => (me() ? ((await api.GET("/api/v1/sessions")).data ?? []) : []));
  const drafts = createMemo(async (): Promise<SessionDraft[]> => (me() ? ((await api.GET("/api/v1/drafts")).data ?? []) : []));
  const terminals = createMemo(async (): Promise<MachineTerminal[]> =>
    me() ? ((await api.GET("/api/v1/terminals")).data ?? []).filter((entry) => !entry.terminal.exited) : [],
  );
  // Sessions of others that wait for this person, as a reviewer of their environment.
  const approvals = createMemo(async (): Promise<EnvironmentApproval[]> =>
    me() ? ((await api.GET("/api/v1/environment-approvals")).data ?? []) : [],
  );
  const activity = createMemo((): Activity[] => {
    const entries: Activity[] = [
      ...sessions().map((session): Activity => ({
        kind: "session",
        key: `s${session.id}`,
        at: Date.parse(session.updatedAt),
        title: session.title,
        session,
      })),
      ...drafts().map((draft): Activity => ({
        kind: "draft",
        key: `d${draft.id}`,
        at: Date.parse(draft.updatedAt),
        title: draft.title || "Untitled draft",
        draft,
      })),
      ...terminals().map((terminal): Activity => ({
        kind: "terminal",
        key: `t${terminal.machineId}/${terminal.terminal.id}`,
        at: Date.parse(terminal.terminal.lastActivityAt ?? terminal.terminal.startedAt),
        title: folderName(terminal.terminal.workingDirectory),
        terminal,
      })),
    ];
    return entries.sort((a, b) => b.at - a.at);
  });

  // "New session" from anywhere starts a fresh editor, even when one is open already.
  const [fresh, setFresh] = createSignal(0);

  onSettled(() => {
    // Sessions change on their own; terminals are asked of every machine, so less often. Drafts change only here.
    const quick = setInterval(() => refresh(sessions), 4000);
    const slow = setInterval(() => {
      refresh(terminals);
      refresh(drafts);
      refresh(approvals);
    }, 15_000);
    return () => {
      clearInterval(quick);
      clearInterval(slow);
    };
  });

  return {
    me,
    sessions,
    drafts,
    terminals,
    approvals,
    activity,
    fresh,
    newSession: () => setFresh((count) => count + 1),
    refreshSessions: () => refresh(sessions),
    refreshDrafts: () => refresh(drafts),
    refreshTerminals: () => refresh(terminals),
    refreshApprovals: () => refresh(approvals),
  };
}

export type Workspace = ReturnType<typeof workspace>;

const WorkspaceContext = createContext<Workspace>();

export function WorkspaceProvider(props: { children: JSX.Element }) {
  return <WorkspaceContext value={workspace()}>{props.children}</WorkspaceContext>;
}

export const useWorkspace = () => useContext(WorkspaceContext);

/** The last part of a path, which names a project better than the whole of it. */
export function folderName(path: string): string {
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.at(-1) ?? (path || "~");
}

export function isAdmin(user: User | null | undefined) {
  return user?.role === "Admin";
}
