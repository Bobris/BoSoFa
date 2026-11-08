import type { SessionStatus } from "@bosofa/api";
import { Show } from "solid-js";

const statusLabels: Record<SessionStatus, string> = {
  Starting: "Starting",
  Running: "Running",
  WaitingForApproval: "Needs approval",
  Idle: "Idle",
  Stopped: "Stopped",
  Failed: "Failed",
};

export function StatusBadge(props: { status: SessionStatus }) {
  return (
    <span
      class={[
        "inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium",
        {
          "bg-amber-500/15 text-amber-700 dark:text-amber-300": props.status === "WaitingForApproval",
          "bg-accent/15 text-accent": props.status === "Running" || props.status === "Starting",
          "bg-red-500/15 text-red-700 dark:text-red-300": props.status === "Failed",
          "bg-line text-muted": props.status === "Idle" || props.status === "Stopped",
        },
      ]}
    >
      <Show when={props.status === "Running" || props.status === "Starting"}>
        <span class="size-1.5 rounded-full bg-current animate-pulse" />
      </Show>
      {statusLabels[props.status]}
    </span>
  );
}
