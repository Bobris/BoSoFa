import { createEffect, createUniqueId } from "solid-js";
import type { JSX } from "@solidjs/web";

export type DialogProps = {
  /** Controlled open state. */
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: JSX.Element;
  description?: JSX.Element;
  /** Escape and backdrop click close the dialog unless this is false. */
  dismissible?: boolean;
  class?: string;
  children?: JSX.Element;
};

/**
 * Modal dialog on the native <dialog> element. The platform gives us the top layer, `inert` on the rest of the
 * page, Escape (the `cancel` event), focus moved into the dialog and restored to the opener on close. We add:
 * controlled state, backdrop click, labelling and scroll lock (in ui.css).
 */
export function Dialog(props: DialogProps) {
  const titleId = createUniqueId();
  const descriptionId = createUniqueId();
  let el!: HTMLDialogElement;
  let downOnBackdrop = false;

  createEffect(
    () => props.open,
    (open) => {
      if (open && !el.open) el.showModal();
      else if (!open && el.open) el.close();
    },
  );

  const dismiss = () => {
    if (props.dismissible !== false) props.onOpenChange(false);
  };

  return (
    <dialog
      ref={el}
      class={["ui-dialog", props.class]}
      aria-labelledby={titleId}
      aria-describedby={props.description ? descriptionId : undefined}
      onCancel={(e) => {
        // Escape: keep the element open and let the controlled prop close it.
        e.preventDefault();
        dismiss();
      }}
      onClose={() => {
        // form[method=dialog] or another close() we did not issue ourselves.
        if (props.open) props.onOpenChange(false);
      }}
      onPointerDown={(e) => (downOnBackdrop = e.target === el)}
      onClick={(e) => {
        // The dialog has no padding; a click whose target is the element itself landed on ::backdrop.
        if (downOnBackdrop && e.target === el) dismiss();
        downOnBackdrop = false;
      }}
    >
      <div class="ui-dialog-body">
        <h2 id={titleId} class="ui-dialog-title">
          {props.title}
        </h2>
        {props.description && (
          <p id={descriptionId} class="ui-dialog-description">
            {props.description}
          </p>
        )}
        {props.children}
      </div>
    </dialog>
  );
}
