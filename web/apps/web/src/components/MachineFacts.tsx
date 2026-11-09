import type { MachineResources } from "@bosofa/api";
import { Show } from "solid-js";

/** Bytes as people read them: GB below a terabyte, TB above. */
export function bytes(value: number | string): string {
  const n = Number(value);
  if (n >= 1024 ** 4) return `${(n / 1024 ** 4).toFixed(1)} TB`;
  return `${(n / 1024 ** 3).toFixed(n >= 100 * 1024 ** 3 ? 0 : 1)} GB`;
}

/** One line for a list: "macOS 26.1 · 36 GB RAM · 120 GB free". */
export function summary(resources: MachineResources): string {
  const parts = [resources.platform];
  if (Number(resources.memoryBytes) > 0) parts.push(`${bytes(resources.memoryBytes)} RAM`);
  if (Number(resources.diskBytes) > 0) parts.push(`${bytes(resources.diskFreeBytes)} free`);
  return parts.join(" · ");
}

function Amount(props: { free: number | string; total: number | string }) {
  return (
    <Show when={Number(props.total) > 0} fallback="unknown">
      {bytes(props.free)} free of {bytes(props.total)}
    </Show>
  );
}

/** What a computer has. `live` says the figures were just measured; otherwise they are as of their time. */
export default function MachineFacts(props: { resources: MachineResources; instanceId?: string | null; live?: boolean }) {
  return (
    <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      <Show when={props.instanceId}>
        {(id) => (
          <>
            <dt class="text-muted">Identity</dt>
            <dd class="font-mono text-xs leading-5 break-all">{id()}</dd>
          </>
        )}
      </Show>
      <dt class="text-muted">Platform</dt>
      <dd>{props.resources.platform}</dd>
      <dt class="text-muted">Processors</dt>
      <dd>{Number(props.resources.processors)}</dd>
      <dt class="text-muted">Memory</dt>
      <dd>
        <Show when={Number(props.resources.memoryAvailableBytes) > 0} fallback={<>{bytes(props.resources.memoryBytes)}</>}>
          <Amount free={props.resources.memoryAvailableBytes} total={props.resources.memoryBytes} />
        </Show>
      </dd>
      <dt class="text-muted">Disk</dt>
      <dd>
        <Amount free={props.resources.diskFreeBytes} total={props.resources.diskBytes} />{" "}
        <span class="text-muted">({props.resources.diskPath})</span>
      </dd>
      <dt class="text-muted">Measured</dt>
      <dd class="text-muted">{props.live ? "just now" : new Date(props.resources.measuredAt).toLocaleString()}</dd>
    </dl>
  );
}
