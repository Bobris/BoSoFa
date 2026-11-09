import { blobImageUrl, uploadBlob, type PromptImage } from "@bosofa/api";
import { createMemo, createSignal, For, Loading, Show } from "solid-js";

const accepted = ["image/png", "image/jpeg", "image/gif", "image/webp"];
const maximumCount = 10;
const maximumBytes = 5 * 1024 * 1024;

type Attached = { id: number; preview: string; sha256?: string };

let nextId = 0;

/** Images waiting to go with a prompt: each is uploaded as soon as it is pasted or dropped. */
export function createAttachments() {
  const [items, setItems] = createSignal<Attached[]>([]);
  const [error, setError] = createSignal<string>();

  function remove(id: number) {
    const item = items().find((candidate) => candidate.id === id);
    if (item) URL.revokeObjectURL(item.preview);
    setItems((all) => all.filter((candidate) => candidate.id !== id));
  }

  async function add(file: File) {
    const id = nextId++;
    setItems((all) => [...all, { id, preview: URL.createObjectURL(file) }]);
    const { sha256, error: problem } = await uploadBlob(file);
    if (!sha256) {
      setError(problem);
      remove(id);
      return;
    }
    setItems((all) => all.map((item) => (item.id === id ? { ...item, sha256 } : item)));
  }

  function addAll(files: File[]) {
    const images = files.filter((file) => accepted.includes(file.type));
    if (images.length === 0) return false;
    setError(undefined);
    let room = maximumCount - items().length;
    for (const file of images) {
      if (file.size > maximumBytes) setError("An image can be at most 5 MB.");
      else if (room-- <= 0) setError(`A message can have at most ${maximumCount} images.`);
      else void add(file);
    }
    return true;
  }

  return {
    items,
    error,
    remove,
    uploading: () => items().some((item) => !item.sha256),
    hashes: () => items().flatMap((item) => (item.sha256 ? [item.sha256] : [])),
    clear() {
      for (const item of items()) URL.revokeObjectURL(item.preview);
      setItems([]);
      setError(undefined);
    },
    onPaste(event: ClipboardEvent) {
      const data = event.clipboardData;
      // Copying from a document can offer both; the text is then what was meant.
      if (data && addAll([...data.files]) && !data.getData("text/plain")) event.preventDefault();
    },
    onDragOver(event: DragEvent) {
      if (event.dataTransfer?.types.includes("Files")) event.preventDefault();
    },
    onDrop(event: DragEvent) {
      if (event.dataTransfer && addAll([...event.dataTransfer.files])) event.preventDefault();
    },
  };
}

export function AttachmentStrip(props: { attachments: ReturnType<typeof createAttachments> }) {
  return (
    <>
      <Show when={props.attachments.items().length > 0}>
        <div class="mb-2 flex flex-wrap gap-2">
          <For each={props.attachments.items()} keyed={(item) => item.id}>
            {(item) => (
              <div class="relative">
                <img src={item().preview} alt="Attached" class={["h-16 w-16 rounded-md border border-line object-cover", !item().sha256 && "opacity-50"]} />
                <button
                  type="button"
                  class="absolute -right-1 -top-1 h-5 w-5 rounded-full border border-line bg-panel text-xs leading-none"
                  title="Remove"
                  onClick={() => props.attachments.remove(item().id)}
                >
                  ×
                </button>
              </div>
            )}
          </For>
        </div>
      </Show>
      <Show when={props.attachments.error()}>{(text) => <p class="mb-2 text-sm text-red-600">{text()}</p>}</Show>
    </>
  );
}

function SentImage(props: { image: PromptImage }) {
  const url = createMemo(() => blobImageUrl(props.image.sha256, props.image.mediaType));
  return (
    <Show when={url()} fallback={<span class="text-xs text-muted">Image not available</span>}>
      {(address) => <img src={address()} alt="Attached" class="max-h-64 max-w-full rounded-md" />}
    </Show>
  );
}

/** The images a person sent, from the transcript item's data. */
export function SentImages(props: { data: string | null | undefined }) {
  const images = (): PromptImage[] => {
    try {
      const parsed = JSON.parse(props.data ?? "null") as PromptImage[] | null;
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  };
  return (
    <Show when={images().length > 0}>
      <div class="mb-2 flex flex-wrap gap-2">
        <For each={images()} keyed={(image) => image.sha256}>
          {(image) => (
            <Loading fallback={<div class="h-16 w-16 rounded-md bg-line" />}>
              <SentImage image={image()} />
            </Loading>
          )}
        </For>
      </div>
    </Show>
  );
}
