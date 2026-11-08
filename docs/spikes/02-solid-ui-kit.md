# Spike ②: UI primitives for the Solid 2 SPA

Date: 2026-11-08. Question: where do `packages/ui`'s primitives (dialog, popover, dropdown menu, tabs, combobox, toast,
tooltip, splitter, virtual list) come from while Solid 2 is a release candidate?

**Recommendation:** build our own small headless set on platform features (`<dialog>`, the Popover API, CSS anchor
positioning, `inert`). Do not depend on any component library for now. Revisit `@kobalte/core` 2.x when Solid 2.0 is
stable, and only if it runs without diagnostics by then. Details are in [Recommendation](#recommendation).

## Setup

The scratch apps are in `spikes/solid-ui/{kobalte,ark,corvu,native}`. Each one is a standalone Vite app with its own
`package.json` and `pnpm-lock.yaml`, installed with `pnpm install --ignore-workspace`. None of them belongs to the `web/`
workspace. They pin the same versions as `web/apps/web`:

| Package | Version |
|---|---|
| `solid-js`, `@solidjs/web`, `@solidjs/diagnostics` | `2.0.0-rc.21` |
| `@solidjs/vite-plugin` | `3.0.0-next.57` (diagnostics auto-enabled through the `@solidjs/diagnostics` dev dependency) |
| `vite` | `8.3.6` |
| `tailwindcss`, `@tailwindcss/vite` | `4.3.5` |
| `typescript` | `5.9.3` |

Each app was checked four ways:

- `pnpm install` output, looking for peer conflicts and a second `solid-js` under `node_modules/.pnpm`.
- `tsc --noEmit`.
- `vite build`.
- In the browser pane (Chromium 152) under `vite` dev: real clicks and keys, DOM/ARIA/focus checks, and the console,
  which the plugin also forwards to the dev terminal. The `vite preview` production build got a smoke test.

To rerun one: `cd spikes/solid-ui/<name> && pnpm install --ignore-workspace && pnpm dev` (ports 5181–5184).

## Results at a glance

| Option | Version tried | Install | `tsc` | `vite build` | Works in browser | `[CODE]` diagnostics | Second solid-js |
|---|---|---|---|---|---|---|---|
| Kobalte | `@kobalte/core@2.0.0-alpha.4` (newest; `latest` is still 0.13.14) | peer warnings (pins rc.16) | ✅ | ✅ 231.6 kB / 73.2 kB gz | ✅ all 7 tried | ❌ 19 warnings, 2 codes | no |
| Ark UI | `@ark-ui/solid@6.0.0-next.0` and `5.40.0` | peer warning | ❌ | ❌ `solid-js/web` | n/a | n/a | no |
| corvu | `corvu@0.7.2` (Jan 2025) | 20+ peer warnings | not run | ❌ `solid-js/web` | n/a | n/a | no |
| Own (native) | 4 primitives, about 390 lines TSX + 120 lines CSS | clean | ✅ | ✅ 49.6 kB / 19.1 kB gz | ✅ | ✅ none | no |

The bundle sizes are whole demo apps, Solid runtime included. The Kobalte demo has 7 components and the native demo has 4.

## 1. Kobalte 2 alpha (`@kobalte/core@2.0.0-alpha.4`)

npm has `alpha: 2.0.0-alpha.4` (published 2026-10-20). Alpha releases have come roughly monthly since 2026-08-13.

**Peers.** It pins Solid exactly: `peerDependencies: { "solid-js": "2.0.0-rc.16", "@solidjs/web": "2.0.0-rc.16" }`, and
so does `@kobalte/utils@2.0.0-alpha.1`. Its `@solid-primitives/*` dependencies are on `next` tags built for Solid 2.
Install output:

```
 WARN  Issues with peer dependencies found
└─┬ @kobalte/core 2.0.0-alpha.4
  ├── ✕ unmet peer solid-js@2.0.0-rc.16: found 2.0.0-rc.21
  ├── ✕ unmet peer @solidjs/web@2.0.0-rc.16: found 2.0.0-rc.21
  └─┬ @kobalte/utils 2.0.0-alpha.1
    ├── ✕ unmet peer solid-js@2.0.0-rc.16: found 2.0.0-rc.21
    └── ✕ unmet peer @solidjs/web@2.0.0-rc.16: found 2.0.0-rc.21
```

The warnings are harmless. pnpm keeps a single `solid-js@2.0.0-rc.21`, and the Vite plugin dedupes `solid-js` and
`@solidjs/web` anyway. Still, an exact rc pin means every Solid upgrade we make runs Kobalte against an rc it was not
built for.

**Port quality.** The source really is ported to Solid 2: `omit`/`merge`, two-argument `createEffect`, `<Ctx value>`,
`Dynamic` from `@solidjs/web`. The API matches 0.13 (`Dialog.Trigger/Portal/Overlay/Content/Title/Description/CloseButton`
and so on). The 2.0 line ships these modules, among others: dialog, alert-dialog, popover, dropdown-menu, context-menu,
menubar, navigation-menu, tabs, tooltip, hover-card, toast, combobox, select, listbox, resizable (a splitter), drawer,
date-picker, calendar, number-field, otp-field and color pickers. It has no virtual list.

**Browser behaviour.** Everything we tried worked:

| Component | Checked |
|---|---|
| Dialog | Focus moves in, Tab is trapped, Escape closes, focus returns to the trigger. |
| Popover | Anchored below the trigger by floating-ui; an outside click dismisses it. |
| Dropdown menu | Arrow keys move, Enter selects, it closes, focus returns. |
| Tooltip | Opens on hover with `role=tooltip`. |
| Toast | `toaster.show` works. |
| Tabs | Click and ArrowLeft switch tabs. |
| Combobox | Typing "b" filters to Banana and Blueberry; ArrowDown + Enter selects. |

The production `vite preview` build also works.

**Diagnostics: this is the blocker.** The dev build printed 19 warnings with two codes, `EFFECT_RELAY_TEAR` (11) and
`EFFECT_WRITES_OWN_SOURCE` (8). They come from Dialog, Popover, DropdownMenu (`menu-sub-trigger/…jsx:826`), Tooltip,
Tabs (List/Trigger/Content, through `polymorphic/index.jsx:10`) and Combobox (`combobox/…jsx:29`). Most fire on mount
and the rest on first open. Tooltip triggers them as well, and our demo passes it no callbacks, so they come from
Kobalte's internals and not from the demo. Kobalte still copies derived state into signals from effects: controllable
open state, and id registration such as `createEffect(() => others.id, id => context.registerContentId(id))`. The
warnings, verbatim:

```
[EFFECT_WRITES_OWN_SOURCE] effect "effect" re-ran because of its own write: it wrote "signal", which fed back into its inputs. Two flushes to settle, and the screen rendered the pre-write value in between. The written value is a function of what the effect reads — compute it in a memo (or normalize where the source is written) instead of correcting it after the fact.
  in <App> › <Dialog> › effect

[EFFECT_RELAY_TEAR] effect "effect" ran twice for one write of "signal": once in the flush where "signal" changed, and again after effect "effect" relayed it by writing "signal" — the first frame showed the new "signal" with the stale "signal" (3 times so far). Nothing else writes "signal". If "signal" is computed from what the effect reads, make it a memo so readers get it in the same flush; if the write reads something outside the graph (layout, time), the tear is the cost of measuring.
  in <App> › <Tabs> › <DomCollectionProvider> › <provider> › computed › <TabsContext> › computed › <Polymorphic> › <Dynamic> › <Anonymous> › computed › effect › <Tabs.List> › <Polymorphic> › <Dynamic> › <Anonymous> › computed › effect › <Tabs.Trigger> › …
  at …/@kobalte/core/dist/polymorphic/index.jsx:10:9
```

Functionally these are one-frame tears, not broken behaviour. But `AGENTS.md` treats every `[CODE]` diagnostic as a
defect, and we cannot fix them in a dependency. Library warnings would also bury the warnings our own code produces.

Verdict: the most complete option, and it works today. It is still alpha, pins an older rc exactly, and is noisy under
diagnostics.

## 2. Ark UI (`@ark-ui/solid`)

npm has `latest: 5.40.0` (2026-10-29) and `next: 6.0.0-next.0` (2026-10-06). Both declare `peerDependencies:
{ "solid-js": ">=1.6.0" }`, so npm does not flag Solid 2. Both still target Solid 1 APIs, and so does their adapter
`@zag-js/solid@2.0.0-next.3`. In the installed `dist` we found 71 imports from `solid-js/web`, `solid-js/jsx-runtime`
imports, 1517 uses of `mergeProps`, 72 of `splitProps`, 41 of `onMount` and 4 of `createComputed`. The only peer warning
comes from a Solid 1 dependency:

```
└─┬ @ark-ui/solid 6.0.0-next.0
  └─┬ @zag-js/solid 2.0.0-next.3
    └─┬ @solid-primitives/keyed 1.5.3
      └── ✕ unmet peer solid-js@^1.6.12: found 2.0.0-rc.21
```

`tsc` fails because the JSX types differ: `Property 'children' does not exist on type 'IntrinsicAttributes &
DialogContentProps'`, and the same for every part. `vite build` fails the same way on 6.0.0-next.0 and 5.40.0:

```
[rolldown:vite-resolve] plugin `rolldown:vite-resolve` threw an error
Caused by:
    "./web" is not exported under the conditions ["solid", "module", "browser", "production", "import"] from package …/node_modules/solid-js (see exports field in …/node_modules/solid-js/package.json)
```

Verdict: unusable on Solid 2 today. The `6.0.0-next` line is a Zag 2 upgrade, not a Solid 2 port. The Zag state machines
themselves are framework-agnostic. Driving them from our own small Solid 2 adapter instead of `@zag-js/solid` is
possible, but we did not test it, and it would mean maintaining an adapter against Zag's own pre-release.

## 3. corvu (`corvu@0.7.2`)

The last release was 2025-01-24, with `peerDependencies: { "solid-js": "^1.8" }`. Install reports more than 20 unmet
peers, for example `@corvu/dialog 0.2.4 ✕ unmet peer solid-js@^1.8: found 2.0.0-rc.21` and
`solid-focus-trap 0.1.9 ✕ …`. The build fails with the same `"./web" is not exported …` error as Ark. There is no Solid 2
branch on npm. Verdict: Solid 1 only, and dormant.

## 4. Own primitives on platform features

`spikes/solid-ui/native/src/ui/` contains the following, written to Solid 2 rules (no destructured props, two-argument
`createEffect`, `onSettled` returning a cleanup, `onFlush` in menu select, `<Ctx value>`, the `class` array form):

| File | Lines | What the platform does | What we wrote |
|---|---|---|---|
| `dialog.tsx` | 74 | `<dialog>.showModal()`: top layer, page made inert, Tab stays in the dialog, the `cancel` event on Escape, focus restored to the opener on close | Controlled `open`/`onOpenChange`, backdrop click (with pointerdown tracking), `aria-labelledby`/`-describedby` through `createUniqueId`, CSS scroll lock (`html:has(.ui-dialog:modal)`) |
| `popover.tsx` | 97 | `popover="auto"` + `popovertarget`: top layer, light dismiss, Escape, toggle on click, implicit `aria-expanded`, focus return, and correct nesting (a popover inside a modal dialog works, and Escape closes only the top one) | `createPopoverState` (open signal driven only by the `toggle` event), context, CSS `anchor-name`/`position-anchor` per instance |
| `menu.tsx` | 144 | The same popover base | WAI-ARIA menu keyboard model: ArrowUp/Down with wrap, Home/End, disabled items skipped, single-character typeahead, ArrowDown/Up on the trigger opens on the first/last item, Tab closes, select closes and refocuses the trigger |
| `tooltip.tsx` | 73 | `popover="hint"` (it does not close an open menu), falling back to `manual` | Show/hide delay, hover and focus, Escape, `aria-describedby`; the trigger is a render prop (`{(p) => <button {...p}>}`) so any element can carry it |
| `ui.css` | 119 | CSS anchor positioning (`position-area`, `position-try-fallbacks: flip-block, flip-inline`) and `@starting-style` with `transition-behavior: allow-discrete` for enter and exit animation | Six placements, styling from the theme variables |

**Browser check (Chromium 152).** Everything behaved as designed, with zero `[CODE]` diagnostics in dev:

- The dialog is modal and centered. Focus starts on the first input and stays inside. Escape and a backdrop click close
  it, and focus returns to the opener.
- A menu nested in the dialog opens over it. Escape closes only the menu.
- The popover anchors at the trigger's bottom-left and closes on an outside click.
- In the menu, arrows skip the disabled item, End and typeahead "a" land on "Archive", and Enter selects, closes the
  menu and refocuses the trigger. A menu whose trigger sat 76 px above the viewport bottom flipped above the trigger. Tab-to-close was not exercised.
- The tooltip opens above its trigger with `role=tooltip`, and `aria-describedby` points at it.

The production build works too.

**Gotchas found:**

- Tailwind preflight sets `margin: 0` on every element. That removes the UA `margin: auto` that centers a modal
  `<dialog>`, so the dialog ends up in the top-left corner. `.ui-dialog { margin: auto }` fixes it, and so does
  resetting `inset`/`margin` on `[popover]`.
- TypeScript 5.9's DOM lib lacks `showPopover({ source })` (Chromium 133+). We had to cast it.
- `JSX` is exported from `@solidjs/web`, not `solid-js`. `popover`, `popovertarget`, `command`/`commandfor`, `closedby`,
  `onToggle`/`onBeforeToggle` and the anchor CSS properties are all typed in Solid 2's JSX and csstype.

**Browser support risk.** The Popover API and `<dialog>` are Baseline in every engine. Chromium and Safari 26 have CSS
anchor positioning. Firefox support should be checked before we commit. If Firefox matters, `@floating-ui/dom` can serve
as a fallback behind `CSS.supports("anchor-name: --a")`, about 0.5 day of work. `popover="hint"` is Chromium only, and
the code already falls back to `manual`. Spike ③ must also confirm which webview NeoAstra uses on each OS (WebKitGTK on
Linux may lag).

### Effort estimate for `packages/ui`

These figures are for one developer with agent help, and include a browser-mode test per primitive (Vitest browser mode
or Playwright).

| Primitive | Days | Notes |
|---|---|---|
| Foundations: popover state, ids, anchor CSS and the floating-ui fallback, theme tokens, test harness | 1.5 | Mostly done in the spike |
| Dialog (+ alert dialog, side drawer variant) | 1 | Spike version is close to final |
| Popover | 0.5 | Spike version is close to final |
| Dropdown menu (+ submenus, checkbox/radio items, context menu) | 2 | Submenus are the hard part (pointer grace area) |
| Tooltip | 0.5 | Spike version is close to final |
| Tabs | 1 | Roving tabindex, manual or automatic activation |
| Toast | 1 | `popover="manual"` stack, `aria-live` region, pause on hover |
| Splitter | 1–1.5 | Pointer capture, keyboard resize, persisted sizes |
| Virtual list | 1.5–2 | Wrap the framework-agnostic `@tanstack/virtual-core` (no Solid peer); dynamic row heights and stick-to-bottom for transcripts |
| Combobox | 3–4 | Listbox, `aria-activedescendant`, filtering, async options, virtualization; the largest single item |
| **Total** | **13–16 days (~3 weeks)** | Can be staged: dialog, popover, menu and tooltip are enough for M1 (about 5 days) |

## Recommendation

**Now (M0/M1):** build `packages/ui` ourselves on native platform features, starting from `spikes/solid-ui/native/src/ui`.

- Write dialog, popover, menu and tooltip first, since M1 needs them. Add tabs, toast, splitter, the virtual list on
  `@tanstack/virtual-core`, and the combobox as screens ask for them.
- Keep the API Kobalte-shaped: compound parts, controlled `open`/`onOpenChange`, `data-*` state attributes. If we switch
  the internals later, the call sites stay the same.
- No component library goes into `web/`. Ark UI and corvu do not build on Solid 2. Kobalte 2 alpha works but breaks our
  zero-diagnostics rule in every component and pins an older rc exactly. The own route is also smaller (about 19 kB gz
  for the whole demo against 73 kB) and has no third-party upgrade lockstep with Solid rcs.

**Revisit when Solid 2.0 is stable:**

- `@kobalte/core` 2.x, if a release pins the stable Solid and runs clean under `@solidjs/diagnostics`. Repeat this
  spike's Kobalte app (`spikes/solid-ui/kobalte`, which covers 7 components) and count the warnings. The first
  candidates to take from it are combobox/select, date picker and number field, the widgets where our own effort is
  highest and the platform helps least.
- Ark UI / `@zag-js/solid`, if a Solid 2 adapter ships.
- corvu: no action, unless it shows signs of life.
