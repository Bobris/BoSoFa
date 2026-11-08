import type { UsageWindow } from "@bosofa/api";

/** One limit window: how much is used and when it resets. */
export default function Limit(props: { name: string; window: UsageWindow }) {
  const percent = () => Math.min(100, Math.round(Number(props.window.utilization) * 100));
  const resets = () =>
    props.window.resetsAt == null
      ? ""
      : `, resets ${new Date(props.window.resetsAt).toLocaleString([], { weekday: "short", hour: "2-digit", minute: "2-digit" })}`;
  return (
    <div class="flex-1">
      <div class="flex justify-between gap-2">
        <span>{props.name}</span>
        <span class="text-muted">
          {percent()}%{resets()}
        </span>
      </div>
      <div class="mt-1 h-1 rounded bg-line">
        <div class={["h-1 rounded", percent() >= 90 ? "bg-red-600" : "bg-accent"]} style={{ width: `${percent()}%` }} />
      </div>
    </div>
  );
}
