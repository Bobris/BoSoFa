import { getPlatform } from "@bosofa/api";
import { useLocation, useNavigate } from "@solidjs/router";
import { createEffect, createSignal, Errored, Loading, onSettled, Show } from "solid-js";
import type { JSX } from "@solidjs/web";
import { branding } from "./branding";
import { MenuIcon, NewIcon, SidebarIcon } from "./components/icons";
import Sidebar from "./components/Sidebar";
import { followTitleBar, startWindowDrag, zoomWindow } from "./frame";
import { Router } from "./router";
import { useWorkspace, WorkspaceProvider } from "./workspace";

/** Pages a person sees before they are signed in, without the sidebar. */
const bare = ["/login", "/desktop/authorize"];
const collapsedKey = "bosofa.sidebarCollapsed";

function rememberedCollapsed(): boolean {
  try {
    return localStorage.getItem(collapsedKey) === "1";
  } catch {
    return false;
  }
}

function Content(props: { children: JSX.Element }) {
  return (
    <Errored
      fallback={(error, reset) => (
        <main class="p-6">
          <p class="text-red-600">{String(error())}</p>
          <button class="mt-3 underline" onClick={reset}>
            Try again
          </button>
        </main>
      )}
    >
      <Loading fallback={<main class="p-6 text-sm text-muted">Loading…</main>}>
        <main class="flex-1 px-4 py-4 sm:px-6 sm:py-6">{props.children}</main>
      </Loading>
    </Errored>
  );
}

/** The sidebar beside whatever is open. The desktop app will hold several of these contents as tabs. */
function Shell(props: { children: JSX.Element }) {
  const workspace = useWorkspace();
  const navigate = useNavigate();
  const location = useLocation();
  const [collapsed, setCollapsed] = createSignal(rememberedCollapsed());
  const [drawer, setDrawer] = createSignal(false);

  createEffect(
    () => workspace.me(),
    (user) => {
      if (user === null) navigate(`/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`, { replace: true });
    },
  );

  function toggle() {
    const next = !collapsed();
    setCollapsed(next);
    try {
      localStorage.setItem(collapsedKey, next ? "1" : "0");
    } catch {
      /* private mode */
    }
  }

  function startNew() {
    workspace.newSession();
    navigate("/");
  }

  onSettled(followTitleBar);

  onSettled(() => {
    const onKey = (event: KeyboardEvent) => {
      const mod = event.metaKey || event.ctrlKey;
      if (mod && event.shiftKey && event.key.toLowerCase() === "o") {
        event.preventDefault();
        startNew();
      } else if (mod && !event.shiftKey && event.key === "\\") {
        event.preventDefault();
        toggle();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  });

  const iconButton = "flex size-7 items-center justify-center rounded-md text-muted hover:bg-hover hover:text-ink";
  return (
    <div class={["flex min-h-screen [--bar:var(--titlebar-height,3rem)]", { "md:[--bar:0px]": !collapsed() }]}>
      <Show when={drawer()}>
        <div class="fixed inset-0 z-30 bg-black/30 md:hidden" onClick={() => setDrawer(false)} />
      </Show>
      <aside
        class={[
          "fixed inset-y-0 left-0 z-40 w-72 border-r border-line transition-transform duration-200 md:sticky md:top-0 md:z-auto md:h-screen md:w-64 md:shrink-0",
          drawer() ? "translate-x-0" : "-translate-x-full md:translate-x-0",
          { "md:hidden": collapsed() },
        ]}
      >
        <Sidebar
          onNavigate={() => setDrawer(false)}
          onCollapse={() => (drawer() ? setDrawer(false) : toggle())}
        />
      </aside>
      <div class="flex min-w-0 flex-1 flex-col">
        <div
          class={[
            "sticky top-0 z-20 flex h-[var(--titlebar-height,3rem)] items-center gap-1 bg-canvas/90 pr-2 backdrop-blur pl-[max(0.5rem,calc(var(--titlebar-inset,0px)+0.25rem))]",
            { "md:hidden": !collapsed() },
          ]}
          onPointerDown={startWindowDrag}
          onDblClick={zoomWindow}
        >
          <button type="button" class={[iconButton, "md:hidden"]} title="Menu" onClick={() => setDrawer(true)}>
            <MenuIcon class="size-[18px]" />
          </button>
          <button type="button" class={[iconButton, "hidden md:flex"]} title="Show the sidebar (⌘\)" onClick={toggle}>
            <SidebarIcon class="size-[18px]" />
          </button>
          <button type="button" class={iconButton} title="New session (⌘⇧O)" onClick={startNew}>
            <NewIcon class="size-[18px]" />
          </button>
          <span class="ml-1 truncate text-sm font-semibold tracking-tight">{branding().name}</span>
        </div>
        <Content>{props.children}</Content>
      </div>
    </div>
  );
}

export default function App() {
  return (
    <Router>
      {(props) => {
        const location = useLocation();
        return (
          <Show
            when={!bare.includes(location.pathname)}
            fallback={
              <>
                <Show when={getPlatform().frame}>
                  {/* Under the title bar: somewhere to move the window from. */}
                  <div class="fixed inset-x-0 top-0 z-10 h-[var(--titlebar-height,3rem)]" onPointerDown={startWindowDrag} onDblClick={zoomWindow} />
                </Show>
                <Content>{props.children}</Content>
              </>
            }
          >
            <WorkspaceProvider>
              <Shell>{props.children}</Shell>
            </WorkspaceProvider>
          </Show>
        );
      }}
    </Router>
  );
}
