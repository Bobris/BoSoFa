import { createSignal } from "solid-js";
import { Dialog } from "./ui/dialog";
import { Menu, MenuContent, MenuItem, MenuSeparator, MenuTrigger } from "./ui/menu";
import { Popover, PopoverContent, PopoverTrigger } from "./ui/popover";
import { Tooltip } from "./ui/tooltip";
import "./ui/ui.css";

const btn = "rounded border border-line bg-panel px-3 py-1.5 text-sm hover:bg-canvas";

export default function App() {
  const [log, setLog] = createSignal<string[]>([]);
  const note = (s: string) => setLog((l) => [...l, s]);
  const [open, setOpen] = createSignal(false);

  return (
    <main class="mx-auto max-w-2xl space-y-6 p-6">
      <h1 class="text-xl font-semibold">Native primitives on Solid 2.0.0-rc.21</h1>

      <section class="flex flex-wrap gap-3">
        <button class={btn} data-testid="dialog-trigger" onClick={() => setOpen(true)}>
          Open dialog
        </button>
        <Dialog
          open={open()}
          onOpenChange={(o) => {
            setOpen(o);
            note(`dialog ${o ? "open" : "closed"}`);
          }}
          title="Delete session?"
          description="This cannot be undone."
        >
          <div data-testid="dialog-content">
            <input class="mt-3 w-full rounded border border-line px-2 py-1" placeholder="focus trap check" />
            <div class="mt-4 flex justify-end gap-2">
              <button class={btn} onClick={() => setOpen(false)}>
                Cancel
              </button>
              <Menu onOpenChange={(o) => note(`nested menu ${o ? "open" : "closed"}`)}>
                <MenuTrigger class={btn}>More ▾</MenuTrigger>
                <MenuContent aria-label="More">
                  <MenuItem onSelect={() => note("nested: copy id")}>Copy id</MenuItem>
                </MenuContent>
              </Menu>
            </div>
          </div>
        </Dialog>

        <Popover onOpenChange={(o) => note(`popover ${o ? "open" : "closed"}`)}>
          <PopoverTrigger class={btn}>Popover</PopoverTrigger>
          <PopoverContent aria-label="Machine">
            <div data-testid="popover-content">
              <p class="font-semibold">Machine</p>
              <p class="text-sm text-muted">mac-mini-01 is online.</p>
            </div>
          </PopoverContent>
        </Popover>

        <Menu onOpenChange={(o) => note(`menu ${o ? "open" : "closed"}`)}>
          <MenuTrigger class={btn}>Menu</MenuTrigger>
          <MenuContent aria-label="Session actions">
            <MenuItem onSelect={() => note("menu: rename")}>Rename</MenuItem>
            <MenuItem onSelect={() => note("menu: archive")}>Archive</MenuItem>
            <MenuItem disabled onSelect={() => note("menu: share")}>
              Share (disabled)
            </MenuItem>
            <MenuSeparator />
            <MenuItem class="text-red-600" onSelect={() => note("menu: delete")}>
              Delete
            </MenuItem>
          </MenuContent>
        </Menu>

        <Tooltip content="Tooltip text">
          {(p) => (
            <button {...p} class={btn} data-testid="tooltip-trigger">
              Hover me
            </button>
          )}
        </Tooltip>
      </section>

      <pre class="rounded bg-panel p-3 text-xs" data-testid="log">
        {log().join("\n")}
      </pre>
      <div class="h-[150vh]" />
    </main>
  );
}
