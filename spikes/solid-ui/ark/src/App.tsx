import { Dialog } from "@ark-ui/solid/dialog";
import { Menu } from "@ark-ui/solid/menu";
import { Popover } from "@ark-ui/solid/popover";
import { Portal } from "@solidjs/web";

const btn = "rounded border border-line bg-panel px-3 py-1.5 text-sm";
const surface = "rounded-lg border border-line bg-panel p-4 shadow-lg";

export default function App() {
  return (
    <main class="mx-auto max-w-2xl space-y-6 p-6">
      <h1 class="text-xl font-semibold">@ark-ui/solid on Solid 2.0.0-rc.21</h1>
      <section class="flex gap-3">
        <Dialog.Root>
          <Dialog.Trigger class={btn}>Open dialog</Dialog.Trigger>
          <Portal>
            <Dialog.Backdrop class="fixed inset-0 bg-black/40" />
            <Dialog.Positioner class="fixed inset-0 grid place-items-center">
              <Dialog.Content class={surface}>
                <Dialog.Title>Delete session?</Dialog.Title>
                <Dialog.Description>This cannot be undone.</Dialog.Description>
                <Dialog.CloseTrigger class={btn}>Cancel</Dialog.CloseTrigger>
              </Dialog.Content>
            </Dialog.Positioner>
          </Portal>
        </Dialog.Root>

        <Popover.Root>
          <Popover.Trigger class={btn}>Popover</Popover.Trigger>
          <Popover.Positioner>
            <Popover.Content class={surface}>
              <Popover.Title>Machine</Popover.Title>
            </Popover.Content>
          </Popover.Positioner>
        </Popover.Root>

        <Menu.Root>
          <Menu.Trigger class={btn}>Menu</Menu.Trigger>
          <Menu.Positioner>
            <Menu.Content class={surface}>
              <Menu.Item value="rename">Rename</Menu.Item>
              <Menu.Item value="archive">Archive</Menu.Item>
            </Menu.Content>
          </Menu.Positioner>
        </Menu.Root>
      </section>
    </main>
  );
}
