import { Combobox } from "@kobalte/core/combobox";
import { Dialog } from "@kobalte/core/dialog";
import { DropdownMenu } from "@kobalte/core/dropdown-menu";
import { Popover } from "@kobalte/core/popover";
import { Tabs } from "@kobalte/core/tabs";
import { Toast, toaster } from "@kobalte/core/toast";
import { Tooltip } from "@kobalte/core/tooltip";
import { createSignal } from "solid-js";

const btn = "rounded border border-line bg-panel px-3 py-1.5 text-sm hover:bg-canvas";
const surface = "rounded-lg border border-line bg-panel p-4 shadow-lg";
const fruits = ["Apple", "Banana", "Blueberry", "Cherry", "Grape", "Mango", "Orange", "Pear"];

export default function App() {
  const [log, setLog] = createSignal<string[]>([]);
  const note = (s: string) => setLog((l) => [...l, s]);

  return (
    <main class="mx-auto max-w-2xl space-y-6 p-6">
      <h1 class="text-xl font-semibold">@kobalte/core 2.0.0-alpha.4 on Solid 2.0.0-rc.21</h1>

      <section class="flex flex-wrap gap-3">
        <Dialog onOpenChange={(o) => note(`dialog ${o ? "open" : "closed"}`)}>
          <Dialog.Trigger class={btn} data-testid="dialog-trigger">
            Open dialog
          </Dialog.Trigger>
          <Dialog.Portal>
            <Dialog.Overlay class="fixed inset-0 bg-black/40" />
            <div class="fixed inset-0 grid place-items-center">
              <Dialog.Content class={surface} data-testid="dialog-content">
                <Dialog.Title class="font-semibold">Delete session?</Dialog.Title>
                <Dialog.Description class="mt-1 text-sm text-muted">This cannot be undone.</Dialog.Description>
                <input class="mt-3 w-full rounded border border-line px-2 py-1" placeholder="focus trap check" />
                <div class="mt-4 flex justify-end gap-2">
                  <Dialog.CloseButton class={btn}>Cancel</Dialog.CloseButton>
                </div>
              </Dialog.Content>
            </div>
          </Dialog.Portal>
        </Dialog>

        <Popover onOpenChange={(o) => note(`popover ${o ? "open" : "closed"}`)}>
          <Popover.Trigger class={btn} data-testid="popover-trigger">
            Popover
          </Popover.Trigger>
          <Popover.Portal>
            <Popover.Content class={surface} data-testid="popover-content">
              <Popover.Title class="font-semibold">Machine</Popover.Title>
              <Popover.Description class="text-sm text-muted">mac-mini-01 is online.</Popover.Description>
            </Popover.Content>
          </Popover.Portal>
        </Popover>

        <DropdownMenu onOpenChange={(o) => note(`menu ${o ? "open" : "closed"}`)}>
          <DropdownMenu.Trigger class={btn} data-testid="menu-trigger">
            Menu
          </DropdownMenu.Trigger>
          <DropdownMenu.Portal>
            <DropdownMenu.Content class={[surface, "min-w-40 p-1"]} data-testid="menu-content">
              <DropdownMenu.Item class="rounded px-2 py-1 data-[highlighted]:bg-canvas" onSelect={() => note("menu: rename")}>
                Rename
              </DropdownMenu.Item>
              <DropdownMenu.Item class="rounded px-2 py-1 data-[highlighted]:bg-canvas" onSelect={() => note("menu: archive")}>
                Archive
              </DropdownMenu.Item>
              <DropdownMenu.Separator class="my-1 border-line" />
              <DropdownMenu.Item class="rounded px-2 py-1 text-red-600 data-[highlighted]:bg-canvas" onSelect={() => note("menu: delete")}>
                Delete
              </DropdownMenu.Item>
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu>

        <Tooltip>
          <Tooltip.Trigger class={btn} data-testid="tooltip-trigger">
            Hover me
          </Tooltip.Trigger>
          <Tooltip.Portal>
            <Tooltip.Content class="rounded bg-ink px-2 py-1 text-xs text-canvas" data-testid="tooltip-content">
              Tooltip text
            </Tooltip.Content>
          </Tooltip.Portal>
        </Tooltip>

        <button
          class={btn}
          data-testid="toast-trigger"
          onClick={() =>
            toaster.show((props) => (
              <Toast toastId={props.toastId} class={surface} data-testid="toast">
                <Toast.Title class="font-semibold">Saved</Toast.Title>
                <Toast.Description class="text-sm text-muted">Toast #{props.toastId}</Toast.Description>
              </Toast>
            ))
          }
        >
          Toast
        </button>
      </section>

      <Tabs class="w-full" defaultValue="a">
        <Tabs.List class="flex gap-2 border-b border-line" data-testid="tabs-list">
          <Tabs.Trigger value="a" class="px-3 py-1 data-[selected]:border-b-2 data-[selected]:border-accent">
            Transcript
          </Tabs.Trigger>
          <Tabs.Trigger value="b" class="px-3 py-1 data-[selected]:border-b-2 data-[selected]:border-accent">
            Diff
          </Tabs.Trigger>
        </Tabs.List>
        <Tabs.Content value="a" class="p-3" data-testid="tab-a">
          Transcript panel
        </Tabs.Content>
        <Tabs.Content value="b" class="p-3" data-testid="tab-b">
          Diff panel
        </Tabs.Content>
      </Tabs>

      <Combobox<string>
        options={fruits}
        placeholder="Pick a fruit"
        onChange={(v) => note(`combobox: ${v}`)}
        itemComponent={(props) => (
          <Combobox.Item item={props.item} class="rounded px-2 py-1 data-[highlighted]:bg-canvas">
            <Combobox.ItemLabel>{props.item.rawValue}</Combobox.ItemLabel>
          </Combobox.Item>
        )}
      >
        <Combobox.Control class="flex w-64 rounded border border-line" data-testid="combobox-control">
          <Combobox.Input class="flex-1 bg-transparent px-2 py-1" data-testid="combobox-input" />
          <Combobox.Trigger class="px-2">▾</Combobox.Trigger>
        </Combobox.Control>
        <Combobox.Portal>
          <Combobox.Content class={[surface, "p-1"]} data-testid="combobox-content">
            <Combobox.Listbox />
          </Combobox.Content>
        </Combobox.Portal>
      </Combobox>

      <Toast.Region>
        <Toast.List class="fixed bottom-4 right-4 flex flex-col gap-2" />
      </Toast.Region>

      <pre class="rounded bg-panel p-3 text-xs" data-testid="log">{log().join("\n")}</pre>
    </main>
  );
}
