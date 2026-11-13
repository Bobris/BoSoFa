import type { JSX } from "@solidjs/web";

/** Line icons drawn on a 24-unit grid, sized by the surrounding text unless a class sets otherwise. */
function Icon(props: { class?: string; children: JSX.Element; title?: string }) {
  return (
    <svg
      class={["shrink-0", props.class ?? "size-4"]}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="1.8"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden={props.title ? undefined : "true"}
    >
      {props.title ? <title>{props.title}</title> : null}
      {props.children}
    </svg>
  );
}

type IconProps = { class?: string; title?: string };

export const NewIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 20h8" />
    <path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4Z" />
  </Icon>
);
export const TerminalIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="4" width="18" height="16" rx="2.5" />
    <path d="m7 9 3 3-3 3" />
    <path d="M13 15h4" />
  </Icon>
);
export const ChatIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M20 12a8 8 0 0 1-11.6 7.1L4 20l1-4.1A8 8 0 1 1 20 12Z" />
  </Icon>
);
export const DraftIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z" />
    <path d="M14 3v5h5" />
    <path d="M9 13h6M9 17h4" />
  </Icon>
);
export const SearchIcon = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="11" cy="11" r="6.5" />
    <path d="m20 20-4.2-4.2" />
  </Icon>
);
export const ChevronDownIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m6 9 6 6 6-6" />
  </Icon>
);
export const ChevronUpDownIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m7 15 5 5 5-5" />
    <path d="m7 9 5-5 5 5" />
  </Icon>
);
export const SidebarIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="4" width="18" height="16" rx="2.5" />
    <path d="M9 4v16" />
  </Icon>
);
export const ArrowUpIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 19V5" />
    <path d="m5 12 7-7 7 7" />
  </Icon>
);
export const CloseIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M18 6 6 18M6 6l12 12" />
  </Icon>
);
export const MachineIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="4" width="18" height="12" rx="2" />
    <path d="M8 20h8M12 16v4" />
  </Icon>
);
export const FolderIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
  </Icon>
);
export const BranchIcon = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="6" cy="5" r="2" />
    <circle cx="6" cy="19" r="2" />
    <circle cx="18" cy="7" r="2" />
    <path d="M6 7v10M18 9a6 6 0 0 1-6 6H8" />
  </Icon>
);
export const SparkIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 3v4M12 17v4M3 12h4M17 12h4M5.6 5.6l2.8 2.8M15.6 15.6l2.8 2.8M5.6 18.4l2.8-2.8M15.6 8.4l2.8-2.8" />
  </Icon>
);
export const ShieldIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 3 4 6v6c0 4.4 3.4 8.4 8 9 4.6-.6 8-4.6 8-9V6Z" />
  </Icon>
);
export const KeyIcon = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="8" cy="15" r="4" />
    <path d="m10.8 12.2 8.2-8.2M16 7l2 2M14 9l2 2" />
  </Icon>
);
export const PackageIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m12 3 8 4.5v9L12 21l-8-4.5v-9Z" />
    <path d="m4 7.5 8 4.5 8-4.5M12 12v9" />
  </Icon>
);
export const DownloadIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 4v11M7 10l5 5 5-5M5 20h14" />
  </Icon>
);
export const UsersIcon = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="9" cy="8" r="3.5" />
    <path d="M2.5 20a6.5 6.5 0 0 1 13 0" />
    <path d="M16 4.5a3.5 3.5 0 0 1 0 7M18 14.5a6.5 6.5 0 0 1 3.5 5.5" />
  </Icon>
);
export const ListIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01" />
  </Icon>
);
export const TokenIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="7" width="18" height="10" rx="2" />
    <path d="M7 12h.01M11 12h.01M15 12h2" />
  </Icon>
);
export const SignOutIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3" />
    <path d="M10 17l-5-5 5-5M5 12h11" />
  </Icon>
);
export const ImageIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="4" width="18" height="16" rx="2.5" />
    <circle cx="9" cy="10" r="1.8" />
    <path d="m21 16-5-5-9 9" />
  </Icon>
);
export const StopIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="7" y="7" width="10" height="10" rx="1.5" />
  </Icon>
);
export const CheckIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m5 12.5 4.5 4.5L19 7.5" />
  </Icon>
);
export const MenuIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M4 7h16M4 12h16M4 17h16" />
  </Icon>
);
