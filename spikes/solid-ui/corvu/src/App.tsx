import Dialog from "corvu/dialog";
import Popover from "corvu/popover";

const btn = "rounded border border-line bg-panel px-3 py-1.5 text-sm";

export default function App() {
  return (
    <main class="mx-auto max-w-2xl space-y-6 p-6">
      <h1 class="text-xl font-semibold">corvu 0.7.2 on Solid 2.0.0-rc.21</h1>
      <Dialog>
        <Dialog.Trigger class={btn}>Open dialog</Dialog.Trigger>
        <Dialog.Portal>
          <Dialog.Overlay class="fixed inset-0 bg-black/40" />
          <Dialog.Content class="fixed left-1/2 top-1/2 rounded bg-panel p-4">
            <Dialog.Label>Delete session?</Dialog.Label>
            <Dialog.Close class={btn}>Cancel</Dialog.Close>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog>
      <Popover>
        <Popover.Trigger class={btn}>Popover</Popover.Trigger>
        <Popover.Portal>
          <Popover.Content class="rounded bg-panel p-4">Machine</Popover.Content>
        </Popover.Portal>
      </Popover>
    </main>
  );
}
