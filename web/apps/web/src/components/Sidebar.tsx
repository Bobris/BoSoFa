import { api, getPlatform } from "@bosofa/api";
import { useLocation, useNavigate } from "@solidjs/router";
import { createMemo, createSignal, For, Loading, Match, Show, Switch } from "solid-js";
import type { JSX } from "@solidjs/web";
import { branding } from "../branding";
import { startWindowDrag, zoomWindow } from "../frame";
import { logout } from "../session";
import { isAdmin, useWorkspace, type Activity } from "../workspace";
import {
  ChatIcon,
  ChevronUpDownIcon,
  DownloadIcon,
  DraftIcon,
  KeyIcon,
  ListIcon,
  MachineIcon,
  NewIcon,
  PackageIcon,
  RocketIcon,
  SearchIcon,
  SidebarIcon,
  SignOutIcon,
  TerminalIcon,
  TokenIcon,
  UsersIcon,
  CloseIcon,
} from "./icons";
import { Popover } from "./Popover";
import UpdateBanner from "./UpdateBanner";

const DAY = 86_400_000;

/** Today, yesterday, this week, earlier: the buckets people look for things in. */
function groupOf(at: number, now: number): string {
  const startOfToday = new Date(now).setHours(0, 0, 0, 0);
  if (at >= startOfToday) return "Today";
  if (at >= startOfToday - DAY) return "Yesterday";
  if (at >= startOfToday - 6 * DAY) return "Previous 7 days";
  return "Older";
}

function hrefOf(entry: Activity): string {
  switch (entry.kind) {
    case "session":
      return `/sessions/${entry.session.id}`;
    case "draft":
      return `/?draft=${entry.draft.id}`;
    case "terminal":
      return `/terminals/${entry.terminal.machineId}/${entry.terminal.terminal.id}`;
  }
}

function SessionDot(props: { status: string }) {
  return (
    <Switch>
      <Match when={props.status === "WaitingForApproval"}>
        <span class="size-2 shrink-0 rounded-full bg-amber-500 ring-2 ring-amber-500/25" title="Needs your approval" />
      </Match>
      <Match when={props.status === "Running" || props.status === "Starting"}>
        <span class="size-2 shrink-0 animate-pulse rounded-full bg-accent" title="Working" />
      </Match>
      <Match when={props.status === "Failed"}>
        <span class="size-2 shrink-0 rounded-full bg-red-500" title="Failed" />
      </Match>
    </Switch>
  );
}

function Row(props: { entry: Activity; active: boolean; onOpen: () => void; onDiscard: () => void }) {
  return (
    <li class="group relative">
      <a
        href={hrefOf(props.entry)}
        onClick={() => props.onOpen()}
        class={[
          "flex h-8 items-center gap-2 rounded-lg px-2 text-sm transition-colors",
          props.active ? "bg-selected text-ink" : "text-ink/80 hover:bg-hover hover:text-ink",
        ]}
        title={props.entry.kind === "terminal" ? props.entry.terminal.terminal.workingDirectory : props.entry.title}
      >
        <Switch>
          <Match when={props.entry.kind === "terminal"}>
            <TerminalIcon class="size-4 text-muted" />
          </Match>
          <Match when={props.entry.kind === "draft"}>
            <DraftIcon class="size-4 text-muted" />
          </Match>
          <Match when={props.entry.kind === "session"}>
            <ChatIcon class="size-4 text-muted" />
          </Match>
        </Switch>
        <span class={["min-w-0 flex-1 truncate", { "italic text-muted": props.entry.kind === "draft" && !props.entry.draft.title }]}>
          {props.entry.title}
        </span>
        <Switch>
          <Match when={props.entry.kind === "session" && props.entry.session}>
            {(session) => <SessionDot status={session().status} />}
          </Match>
          <Match when={props.entry.kind === "terminal" && props.entry.terminal}>
            {(terminal) => <span class="max-w-24 shrink-0 truncate text-xs text-muted">{terminal().machineName}</span>}
          </Match>
          <Match when={props.entry.kind === "draft"}>
            <span class="shrink-0 rounded bg-line/70 px-1 text-[10px] font-medium uppercase tracking-wide text-muted group-hover:invisible">
              Draft
            </span>
          </Match>
        </Switch>
      </a>
      <Show when={props.entry.kind === "draft"}>
        <button
          type="button"
          class="absolute right-1 top-1 hidden size-6 items-center justify-center rounded-md text-muted hover:bg-selected hover:text-ink group-hover:flex"
          title="Discard draft"
          onClick={() => props.onDiscard()}
        >
          <CloseIcon class="size-3.5" />
        </button>
      </Show>
    </li>
  );
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  return ((parts[0]?.[0] ?? "?") + (parts.length > 1 ? (parts.at(-1)?.[0] ?? "") : "")).toUpperCase();
}

function MenuLink(props: { href: string; icon: JSX.Element; label: string; onPick: () => void }) {
  return (
    <a href={props.href} onClick={() => props.onPick()} class="flex items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-ink/90 hover:bg-hover hover:text-ink">
      {props.icon}
      {props.label}
    </a>
  );
}

function UserMenu(props: { onNavigate: () => void }) {
  const workspace = useWorkspace();
  const navigate = useNavigate();
  const [open, setOpen] = createSignal(false);
  const pick = () => {
    setOpen(false);
    props.onNavigate();
  };
  return (
    <Show when={workspace.me()}>
      {(user) => (
        <div class="relative">
          <button
            type="button"
            class={["flex w-full items-center gap-2.5 rounded-lg p-2 text-left transition-colors hover:bg-hover", { "bg-hover": open() }]}
            onClick={() => setOpen((value) => !value)}
          >
            <span class="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent/15 text-xs font-semibold text-accent">
              {initials(user().name)}
            </span>
            <span class="min-w-0 flex-1 leading-tight">
              <span class="block truncate text-sm font-medium">{user().name}</span>
              <span class="block truncate text-xs text-muted">{user().email}</span>
            </span>
            <ChevronUpDownIcon class="size-4 text-muted" />
          </button>
          <Popover open={open()} onClose={() => setOpen(false)} class="bottom-full left-0 right-0 mb-1.5">
            <p class="px-2.5 pb-1 pt-1.5 text-xs font-medium text-muted">Settings</p>
            <MenuLink href="/machines" icon={<MachineIcon class="size-4 text-muted" />} label="Machines" onPick={pick} />
            <MenuLink href="/accounts" icon={<KeyIcon class="size-4 text-muted" />} label="Accounts" onPick={pick} />
            <MenuLink href="/catalog" icon={<PackageIcon class="size-4 text-muted" />} label="Profiles and catalog" onPick={pick} />
            <MenuLink href="/tokens" icon={<TokenIcon class="size-4 text-muted" />} label="API tokens" onPick={pick} />
            <MenuLink href="/install" icon={<DownloadIcon class="size-4 text-muted" />} label="Install the apps" onPick={pick} />
            <Show when={isAdmin(user())}>
              <p class="mt-1 border-t border-line px-2.5 pb-1 pt-2 text-xs font-medium text-muted">Administration</p>
              <MenuLink href="/people" icon={<UsersIcon class="size-4 text-muted" />} label="People" onPick={pick} />
              <MenuLink href="/releases" icon={<RocketIcon class="size-4 text-muted" />} label="Releases" onPick={pick} />
              <MenuLink href="/audit" icon={<ListIcon class="size-4 text-muted" />} label="Audit" onPick={pick} />
            </Show>
            <div class="mt-1 border-t border-line pt-1">
              <button
                type="button"
                class="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-left text-ink/90 hover:bg-hover hover:text-ink"
                onClick={async () => {
                  setOpen(false);
                  await logout();
                  navigate("/login", { replace: true });
                }}
              >
                <SignOutIcon class="size-4 text-muted" />
                Sign out
              </button>
            </div>
          </Popover>
        </div>
      )}
    </Show>
  );
}

function ActivityList(props: { filter: string; onNavigate: () => void }) {
  const workspace = useWorkspace();
  const location = useLocation();
  const groups = createMemo(() => {
    const now = Date.now();
    const needle = props.filter.trim().toLowerCase();
    const shown = needle
      ? workspace.activity().filter((entry) =>
          [entry.title, entry.kind === "terminal" ? entry.terminal.machineName : "", entry.kind === "session" ? entry.session.machineName : ""]
            .join(" ")
            .toLowerCase()
            .includes(needle),
        )
      : workspace.activity();
    const result: { name: string; entries: Activity[] }[] = [];
    for (const entry of shown) {
      const name = groupOf(entry.at, now);
      if (result.at(-1)?.name !== name) result.push({ name, entries: [] });
      result.at(-1)!.entries.push(entry);
    }
    return result;
  });
  const isActive = (entry: Activity) => {
    const path = location.pathname;
    if (entry.kind === "draft") return path === "/" && new URLSearchParams(location.search).get("draft") === String(entry.draft.id);
    return path === hrefOf(entry);
  };
  async function discard(id: number | string) {
    await api.DELETE("/api/v1/drafts/{id}", { params: { path: { id: Number(id) } } });
    workspace.refreshDrafts();
  }

  return (
    <Show
      when={groups().length > 0}
      fallback={
        <p class="px-3 py-6 text-center text-xs text-muted">
          {props.filter.trim() ? "Nothing matches." : "Sessions and terminals you start show up here."}
        </p>
      }
    >
      <For each={groups()} keyed={(group) => group.name}>
        {(group) => (
          <section class="mb-3">
            <h3 class="sticky top-0 z-10 bg-sidebar px-2 pb-1 pt-2 text-xs font-medium text-muted">{group().name}</h3>
            <ul class="space-y-px">
              <For each={group().entries} keyed={(entry) => entry.key}>
                {(entry) => (
                  <Row
                    entry={entry()}
                    active={isActive(entry())}
                    onOpen={props.onNavigate}
                    onDiscard={() => {
                      const current = entry();
                      if (current.kind === "draft") void discard(current.draft.id);
                    }}
                  />
                )}
              </For>
            </ul>
          </section>
        )}
      </For>
    </Show>
  );
}

function ListSkeleton() {
  return (
    <div class="space-y-2 px-2 pt-3">
      <For each={[70, 85, 55, 75, 60]}>{(width) => <div class="h-4 animate-pulse rounded bg-hover" style={{ width: `${width}%` }} />}</For>
    </div>
  );
}

/** Where everything starts and is found again: new work at the top, recent work below, the person and their settings at the bottom. */
export default function Sidebar(props: { onNavigate: () => void; onCollapse: () => void }) {
  const workspace = useWorkspace();
  const navigate = useNavigate();
  const [filter, setFilter] = createSignal("");
  const action = "flex h-9 w-full items-center gap-2.5 rounded-lg px-2 text-sm font-medium text-ink/90 transition-colors hover:bg-hover hover:text-ink";
  return (
    <nav class="flex h-full select-none flex-col bg-sidebar">
      <div
        class="flex h-[var(--titlebar-height,3rem)] shrink-0 items-center justify-between gap-2 pl-[max(0.75rem,calc(var(--titlebar-inset,0px)+0.25rem))] pr-3"
        onPointerDown={startWindowDrag}
        onDblClick={zoomWindow}
      >
        <a href="/" class="min-w-0 truncate text-[15px] font-semibold tracking-tight" onClick={() => workspace.newSession()}>
          {branding().name}
        </a>
        <button
          type="button"
          class="flex size-7 items-center justify-center rounded-md text-muted hover:bg-hover hover:text-ink"
          title="Hide the sidebar (⌘\)"
          onClick={() => props.onCollapse()}
        >
          <SidebarIcon class="size-[18px]" />
        </button>
      </div>
      <div class="space-y-0.5 px-2">
        <button
          type="button"
          class={action}
          onClick={() => {
            workspace.newSession();
            navigate("/");
            props.onNavigate();
          }}
        >
          <span class="flex size-6 items-center justify-center rounded-full bg-accent text-white">
            <NewIcon class="size-3.5" />
          </span>
          New session
        </button>
        <button
          type="button"
          class={action}
          onClick={() => {
            navigate("/terminals/new");
            props.onNavigate();
          }}
        >
          <span class="flex size-6 items-center justify-center rounded-full bg-hover text-ink/80">
            <TerminalIcon class="size-3.5" />
          </span>
          New terminal
        </button>
      </div>
      <div class="px-2 pb-1 pt-3">
        <label class="flex h-8 items-center gap-2 rounded-lg bg-hover/70 px-2 text-sm text-muted focus-within:bg-hover focus-within:text-ink">
          <SearchIcon class="size-4" />
          <input
            class="min-w-0 flex-1 select-text bg-transparent text-ink outline-none placeholder:text-muted"
            placeholder="Search"
            value={filter()}
            onInput={(event) => setFilter(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === "Escape") setFilter("");
            }}
          />
        </label>
      </div>
      <div class="min-h-0 flex-1 overflow-y-auto px-2 pb-2 [scrollbar-width:thin]">
        <Loading fallback={<ListSkeleton />}>
          <ActivityList filter={filter()} onNavigate={props.onNavigate} />
        </Loading>
      </div>
      <Show when={getPlatform().updates}>
        <Loading fallback={null}>
          <div class="px-3 pb-1">
            <UpdateBanner />
          </div>
        </Loading>
      </Show>
      <div class="border-t border-line p-2">
        <Loading fallback={<div class="h-12" />}>
          <UserMenu onNavigate={props.onNavigate} />
        </Loading>
      </div>
    </nav>
  );
}
