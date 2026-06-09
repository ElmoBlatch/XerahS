# XIP0079 COSMIC Global Hotkeys via Self-Written Compositor Shortcuts (XerahS writes the cosmic-config custom map and the compositor spawns the capture verb)

**Status**: Open
**Version**: v0.23.0

**Area**: Linux | Wayland | Hotkeys
**Related**: XIP0077 (COSMIC custom-shortcut *manual* workaround; no GlobalShortcuts portal, compositor-reserved Print, zero detection), XIP0078 (general `GlobalShortcutsUnavailable` honest-status mechanism for portal-less Wayland), XIP0044 (X11 fallback hotkey failures, app ID mismatch, startup race, portal rebind debounce), XIP0046 (Print key mapping, GlobalShortcuts silent failure, region-selection CLI fallbacks, portal UI variance), XIP0061 (KDE-specific portal backend behavior — ConfigureShortcuts/InputCapture gaps), XIP0029 (core DBus signal/interface stability for portal services)

---

## Overview

On the COSMIC desktop there is **no `org.freedesktop.portal.GlobalShortcuts` portal**, and the X11
grab backend is dead on a native Wayland session (XIP0077 Issue A; XIP0078 Issues A–C). XIP0077
documented the **manual** answer — the user hand-binds a COSMIC custom shortcut to the CLI — and
XIP0078 added the honest `GlobalShortcutsUnavailable` status so the UI stops lying about a dead
grab. Both are stopgaps: one asks the user to do XerahS's job by hand, the other simply admits
defeat.

This XIP is **Phase 2 of XIP0077**: instead of asking the user to write a COSMIC custom shortcut,
**XerahS writes it itself**. COSMIC's shortcut configuration is a plain cosmic-config RON map on
disk that the compositor watches and live-reloads. XerahS can therefore register a global hotkey
by appending an entry to that map whose action is `Spawn("<abs>/XerahS capture <kind> --workflow-id <id>")`.
When the user presses the key, **cosmic-comp itself** launches XerahS with the capture verb; the
single-instance pipe forwards the verb to the already-running GUI; and the verb is dispatched into
the exact same `WorkflowOrchestrator` path a real in-process hotkey would have taken. End to end:

```text
COSMIC custom shortcut  (XerahS-written RON entry)
        │  user presses key
        ▼
cosmic-comp  →  Spawn("<abs>/XerahS capture region --workflow-id 4f9a…")
        ▼
second XerahS process  →  single-instance named pipe (ArgumentsReceived)
        ▼
running XerahS GUI  →  ProcessIncomingArguments  →  WorkflowOrchestrator.ExecuteWorkflowFromTriggerAsync
        ▼
same workflow execution as HotkeyTriggered
```

On COSMIC this **supersedes** XIP0078's `GlobalShortcutsUnavailable` stopgap: once XerahS can write
the compositor shortcut, the hotkey genuinely works, so the amber "unavailable" banner should be
replaced by an honest `Registered`.

> [!NOTE]
> This is COSMIC-specific by design. It depends on COSMIC's documented cosmic-config shortcut
> store and `Spawn` action. It does **not** replace the GlobalShortcuts portal path (preferred where
> it exists) or the X11 grab (correct on a real X11 session). If/when `xdg-desktop-portal-cosmic`
> ships GlobalShortcuts (XIP0077 Fix 4), the portal path takes precedence again.

---

## Problem Statement

XIP0077's "User Workaround (works today)" requires the user to manually open `cosmic-settings`,
unbind the compositor's default `Print`, and add a custom shortcut whose command is `xerahscli capture …`
(XIP0077 "User Workaround"). That is correct but it is *manual configuration the application could
perform itself* — XerahS already knows the user's chosen key and workflow; the only missing piece is
writing them into COSMIC's shortcut store and dispatching the resulting spawn.

Two things stand between "manual" and "automatic":

1. **XerahS does not write COSMIC's shortcut config.** `grep -ri 'cosmic' src/` returns zero matches
   (XIP0077 Issue C4), so there is no code that knows COSMIC's cosmic-config store, its RON grammar,
   or its `Spawn` action.
2. **A forwarded capture verb is not dispatched.** Even though COSMIC could spawn `XerahS capture …`
   today, the running instance would *drop* the verb — see the Verb-Dispatch Finding below. The
   manual workaround only works because XIP0077 binds the **CLI** binary (`xerahscli`), which starts
   its own process and runs the verb directly; it never reaches the GUI's argument handler.

The result is that XerahS on COSMIC is stuck at "tell the user to do it themselves" (XIP0077) plus
"at least don't claim it works" (XIP0078), when the platform actually offers a clean, file-based
mechanism for XerahS to wire the hotkey end-to-end.

---

## Design

The feature has three moving parts, all of which already have most of their plumbing in place.

### End-to-end flow

1. **Write the shortcut.** When the user assigns a hotkey on COSMIC, XerahS writes (read-merge-write)
   an entry into COSMIC's custom-shortcut RON map binding the user's `(modifiers, key)` to
   `Spawn("<abs>/XerahS capture <kind> --workflow-id <id>")`.
2. **Compositor spawns XerahS.** cosmic-comp watches that file and live-reloads it (cosmic-comp
   `src/config/mod.rs:251-258`); pressing the key runs the `Spawn` action, launching a fresh XerahS
   process with the capture verb.
3. **Single-instance forwards the verb.** The fresh process is not the first instance, so
   `SingleInstanceManager` redirects its argv to the running primary over the named pipe and exits
   (`src/desktop/core/XerahS.Common/SingleInstanceManager.cs:66-81`); the primary already subscribed
   to `ArgumentsReceived` (`src/desktop/app/XerahS.App/Program.cs:79-89`).
4. **Primary dispatches the verb.** `OnArgumentsReceived` posts to the UI thread and calls
   `ProcessIncomingArguments` (`src/desktop/app/XerahS.App/Program.cs:774-818`, dispatch at `:810`),
   which — once extended (see Verb-Dispatch Finding) — resolves the workflow and calls
   `WorkflowOrchestrator.ExecuteWorkflowFromTriggerAsync`
   (`src/desktop/app/XerahS.UI/Services/WorkflowOrchestrator.cs:353-454`), the exact method an
   in-process hotkey reaches via `HotkeyManager_HotkeyTriggered`
   (`WorkflowOrchestrator.cs:329-339`).

### The corrected Spawn command

XIP0077's workaround spawns the **CLI** binary `xerahscli`. This XIP spawns the **GUI** binary so the
forwarded verb lands in the running instance (single-instance + `WorkflowOrchestrator`), not a fresh
headless CLI process. The command must therefore be:

```text
'<absolute-path>/XerahS' capture --workflow-id <id>
```

> **As-built note (XIP0079 implementation).** The shipped writer emits exactly
> `'<absolute-path>/XerahS' capture --workflow-id <id>`. Two deviations from the original sketch,
> both deliberate:
> 1. **The executable path is POSIX shell-quoted.** cosmic-comp runs `Spawn(String)` through
>    `/bin/sh -c "<command>"` (`cosmic-comp src/input/actions.rs`), so an install path containing
>    spaces/metacharacters would otherwise be word-split and the launch would fail.
> 2. **No positional `<region|screen|window|transparent>` sub-verb.** `--workflow-id` alone
>    re-resolves the exact configured `WorkflowSettings`, which already carries its own capture kind,
>    so the sub-verb is redundant on the normal path. `CaptureArgsParser` therefore parses only the
>    `capture` verb + `--workflow-id`. A sub-verb *fallback* (capture something sensible when the id
>    is missing/stale) remains a documented future enhancement; today an unresolvable id is logged
>    and no capture runs.

- **Binary name is `XerahS`** (capital), not `xerahs`/`xerahscli`: the GUI assembly name is
  `<AssemblyName>XerahS</AssemblyName>` (`src/desktop/app/XerahS.App/XerahS.App.csproj:16`). The
  `xerahscli` name belongs to the separate CLI project (XIP0077 References).
- **Absolute path, shell-quoted** resolved at write time from `Environment.ProcessPath` — the same
  value XerahS already logs as its command line at startup (`src/desktop/app/XerahS.App/Program.cs:111`).
  cosmic-comp spawns with no working-directory guarantee, so a bare name would not resolve.
- **`--workflow-id <id>`** so the spawned/forwarded invocation re-resolves the *exact* configured
  `TaskSettings`, exactly as the CLI already looks up a workflow by id
  (`src/desktop/cli/XerahS.CLI/Commands/WorkflowCommand.cs:103-104`:
  `SettingsManager.WorkflowsConfig?.Hotkeys?.FirstOrDefault(w => w.Id == workflowId)`). Without the id,
  the dispatcher can only fall back to an ad-hoc default (a future sub-verb fallback).

---

## Verb-Dispatch Finding

**This is the key gap.** The single-instance machinery is fully wired, but a forwarded *capture
verb* is silently dropped today.

What works:

- `Program.cs` attaches the handler: `_singleInstanceManager.ArgumentsReceived += OnArgumentsReceived;`
  (`src/desktop/app/XerahS.App/Program.cs:88-89`), after deciding it is the first instance at
  `:81-86`.
- `SingleInstanceManager` forwards a non-primary process's argv to the primary and exits:
  `RedirectArgumentsToFirstInstance(args); ReleaseInstanceLocks();`
  (`src/desktop/core/XerahS.Common/SingleInstanceManager.cs:66-81`), and re-raises them via
  `OnArgumentsReceived` / the `ArgumentsReceived` event (`SingleInstanceManager.cs:84-87`).
- `OnArgumentsReceived` brings the window forward and routes to
  `ProcessIncomingArguments(args, source: "secondary-instance")`
  (`src/desktop/app/XerahS.App/Program.cs:774-818`, the call at `:810`).

What is missing: `ProcessIncomingArguments` only recognises **plugin packages**, **Send-to
invocations**, and **file/folder paths** — anything else is dropped
(`src/desktop/app/XerahS.App/Program.cs:820-866`). There is no capture-verb branch; the literal
placeholder marking the gap sits right above the dispatch:

> `// TODO: Process arguments if needed (e.g., file paths to open, commands to execute)`
> (`src/desktop/app/XerahS.App/Program.cs:805`)

So a spawned `XerahS capture region --workflow-id <id>` would correctly forward over the pipe, the
window would pop forward, and then the verb would be **ignored** because `ProcessIncomingArguments`
finds no plugin package, no `--send-to`, and no path. **This feature must add a capture-verb branch**
to `ProcessIncomingArguments` that resolves the workflow and routes to
`WorkflowOrchestrator.ExecuteWorkflowFromTriggerAsync`
(`src/desktop/app/XerahS.UI/Services/WorkflowOrchestrator.cs:353-454`) — the same sink reached today
by `HotkeyManager_HotkeyTriggered` (`WorkflowOrchestrator.cs:329-339`).

---

## COSMIC RON Grammar (source-verified)

The grammar below is verified against COSMIC's own source. cosmic-comp pins
`cosmic-settings-config` at rev **`defa9f79`** (`cosmic-comp Cargo.lock`:
`source = "git+https://github.com/pop-os/cosmic-settings-daemon#defa9f790432c70054ca1f39737d879ada5d0252"`),
so the daemon's shortcut model is authoritative for what cosmic-comp accepts.

### Config store and file path

- The shortcut store is a cosmic-config entry with id **`com.system76.CosmicSettings.Shortcuts`**,
  **version `1`**, and the user map lives under the key **`custom`**
  (cosmic-settings-daemon `config/src/shortcuts/mod.rs:22` `pub const ID: &str = "com.system76.CosmicSettings.Shortcuts";`;
  `mod.rs:79-82` `#[version = 1]` with `pub custom: Shortcuts`; `mod.rs:42` `context.get::<Shortcuts>("custom")`).
- cosmic-config stores each key as its own file, so the on-disk path is
  `$XDG_CONFIG_HOME` (default `~/.config`) `/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom`
  — the same path XIP0077's bare-key caveat names (XIP0077 "User Workaround" WARNING). **The whole
  file body *is* the RON value** for that key — a single map — defaulting to `{}` when no custom
  shortcuts exist (`Shortcuts` derives `Default` over an empty `HashMap`, mod.rs:103-105). XerahS
  resolves `~/.config` via `LinuxXdgDirectories` (`ConfigHome` / `ConfigDirectory`,
  `src/desktop/core/XerahS.Common/LinuxXdgDirectories.cs:31-95`).
- cosmic-comp **watches and live-reloads** this store: it builds a `ConfigWatchSource` for
  `com.system76.CosmicSettings.Shortcuts` and, on change, re-runs
  `state.common.config.shortcuts = shortcuts::shortcuts(&config)` (cosmic-comp
  `src/config/mod.rs:248-272`). No restart or D-Bus call is needed — writing the file is sufficient.

### Value shape: `Shortcuts = map<Binding, Action>`

`Shortcuts` is a transparent map `HashMap<Binding, Action>` (cosmic-settings-daemon
`config/src/shortcuts/mod.rs:103-105`, `#[serde(transparent)]`). In RON the file body is therefore a
map literal whose keys are `Binding` structs and whose values are `Action` enum values:

```ron
{
    (modifiers: [Ctrl, Shift], key: "4"): Spawn("/usr/bin/XerahS capture region --workflow-id 4f9a1c2e"),
}
```

### `Binding` (the map key)

`Binding` has **`#[serde(deny_unknown_fields)]`** and declares exactly four fields —
`modifiers`, `key`, `keycode`, `description` (cosmic-settings-daemon
`config/src/shortcuts/binding.rs:11-31`). XerahS must emit **only** `modifiers`, `key`, and
(optionally) `description`; any stray field aborts the parse for the whole map.

- **`key`** serializes via `xkb::keysym_get_name` (cosmic-settings-daemon
  `config/src/shortcuts/sym.rs:39-54`), i.e. the **canonical xkb keysym *name***. Single-character
  keys parse through `Keysym::from_char` on the **lowercased** char
  (`binding.rs:65-72`), so **letters are lowercase** (`"q"`, not `"Q"`); special keys keep their
  underscore names (`"Print"`, `"Return"`, `"space"`, `"Num_Lock"`, `"bracketleft"`). This is exactly
  the xkb naming XerahS already produces in `LinuxHotkeyService.SpecialKeyNames`
  (`src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:431`) and
  `GetCandidateKeysymNames` (`LinuxHotkeyService.cs:369-422`).
- **`modifiers`** is a list of the `Modifier` enum, whose members are exactly **`Ctrl`, `Alt`,
  `Shift`, `Super`** with **no serde rename** (cosmic-settings-daemon
  `config/src/shortcuts/modifier.rs:5-11`). `Super` is the logo/Super key — it sets `Modifiers.logo`
  (`modifier.rs:46-49, 52-60`), which cosmic-comp compares as `this.logo == other.logo`
  (cosmic-comp `src/config/key_bindings.rs:40-54`). A **bare key** (no modifier) is `modifiers: []`.
  Avalonia → COSMIC mapping: `Control → Ctrl`, `Shift → Shift`, `Alt → Alt`, `Meta → Super`.

### `Action` (the map value)

`Action` is an externally-tagged enum in exact CamelCase (cosmic-settings-daemon
`config/src/shortcuts/action.rs:7-8`, default serde external tagging). The three variants XerahS
cares about:

- **`Spawn(String)`** (`action.rs:123`) → `Spawn("cmd arg1 arg2")` — runs a command line. This is the
  action XerahS writes.
- **`Disable`** (`action.rs:16`) → masks a default binding.
- **`System(System)`** (`action.rs:120`) → built-in actions, e.g. `System(Screenshot)`, which is the
  compositor's default `Print` binding.

### Merge semantics = override; restore = delete

cosmic-comp loads `defaults`, then **`shortcuts.0.extend(custom_shortcuts.0)`** (cosmic-settings-daemon
`config/src/shortcuts/mod.rs:34-49`, the `extend` at `:48`). `HashMap::extend` overwrites on key
collision, so **a custom entry on the same `(modifiers, key)` displaces the default**. The default
screenshot binding is `(modifiers: [], key: "Print"): System(Screenshot)` (cosmic-comp
`data/keybindings.ron:114`). To rebind bare `Print` to XerahS, the user's `custom` entry on
`(modifiers: [], key: "Print")` *replaces* it; XerahS does not need to touch `defaults`.

> [!IMPORTANT]
> **Restore on unbind = delete our `custom` entry**, *not* write `Disable`. Because cosmic-comp
> `extend`s `custom` over `defaults`, removing our entry makes the default reapply automatically.
> Writing `Disable` on `(modifiers: [], key: "Print")` would *suppress screenshots entirely* (the
> default `System(Screenshot)` would be masked even after XerahS forgets the binding). Always remove
> the key, never neutralise it.

### Example RON replacing the `Print` default

```ron
{
    // XerahS-managed: bare Print → region capture for workflow 4f9a1c2e.
    // Overrides the cosmic-comp default (modifiers: [], key: "Print"): System(Screenshot).
    (modifiers: [], key: "Print", description: "XerahS: capture region (workflow 4f9a1c2e)"): Spawn("/usr/bin/XerahS capture region --workflow-id 4f9a1c2e"),

    // XerahS-managed: Ctrl+Shift+4 → region capture, preserved alongside any user entries.
    (modifiers: [Ctrl, Shift], key: "4", description: "XerahS: capture region (workflow 4f9a1c2e)"): Spawn("/usr/bin/XerahS capture region --workflow-id 4f9a1c2e"),
}
```

`description` is a **declared** field on `Binding` (`binding.rs:29-30`), so it is legal under
`deny_unknown_fields` and gives XerahS a safe tag (`"XerahS: …"`) to recognise its own entries on the
next read-merge-write without clobbering foreign ones.

---

## Implementation Plan

Six ordered steps. Each is file-anchored and notes how it is tested.

### Step 1 — Add the capture-verb contract to `AppContracts.Cli`

Define the verb literal and `--workflow-id` flag in one shared place so the GUI dispatcher, the
COSMIC writer, and the CLI cannot drift. `AppContracts.Cli` already centralises CLI flags
(`src/desktop/core/XerahS.Common/AppContracts.cs:49-71` — `SendToFlag`, `SettingsFolderFlag`, …).
Add the `capture` verb token, the `screen|window|region|transparent` subverbs (matching
`CaptureCommand.cs:40-100`), and a `WorkflowIdFlag = "--workflow-id"` constant.

*Tested by:* a unit test asserting the writer's emitted command string and the dispatcher's parser
both reference the same constants (round-trip of `capture region --workflow-id <id>`).

### Step 2 — Parse and dispatch the capture verb in `ProcessIncomingArguments`

Close the Verb-Dispatch Finding. In `ProcessIncomingArguments`
(`src/desktop/app/XerahS.App/Program.cs:820-866`, gap marked at `:805`), add a branch that, on
detecting the `capture` verb:

1. Resolves the `WorkflowSettings` by `--workflow-id` exactly like the CLI
   (`WorkflowCommand.cs:103-104`).
2. **Falls back ad-hoc** from the subverb when no id is present or the id is stale:
   `region → RectangleRegion`, `screen → PrintScreen`, `window → ActiveWindow`,
   `transparent → RectangleTransparent` (all valid `WorkflowType` members:
   `src/desktop/core/XerahS.Core/Enums.cs:168,171,180,183`).
3. Marshals to the UI thread and calls
   `WorkflowOrchestrator.ExecuteWorkflowFromTriggerAsync`
   (`src/desktop/app/XerahS.UI/Services/WorkflowOrchestrator.cs:353-454`).

Keep the verb inside single-instance forwarding — **do not** short-circuit `Main` before the mutex,
or a second invocation would start a competing process instead of reaching the pipe
(`Program.cs:79-89`, `SingleInstanceManager.cs:66-81`).

*Tested by:* a dispatch unit test feeding `["capture","region","--workflow-id","<id>"]` through the
parser and asserting it resolves the right `WorkflowSettings` / ad-hoc `WorkflowType`; manual COSMIC
end-to-end (Verification).

### Step 3 — Add `CosmicShortcutConfigWriter`

New service under `src/platform/XerahS.Platform.Linux/Services/`. It owns the **read-merge-write** of
the COSMIC `custom` RON map:

- Resolve the directory via `LinuxXdgDirectories` (`LinuxXdgDirectories.cs:31-95`) →
  `<ConfigHome>/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom`.
- **Read** the existing map (default `{}`), **preserve all foreign entries**, and add/replace only the
  XerahS-tagged entries (`description: "XerahS: …"`).
- **Atomic write**: serialize to a temp file then `File.Move(temp, target, overwrite)`, mirroring the
  settings persistence pattern in `SettingsBase.cs:189-224`.
- Emit only the declared `Binding` fields (`modifiers`, `key`, `description`) to satisfy
  `deny_unknown_fields` (`binding.rs:11-31`); emit `Modifier` tokens `Ctrl/Alt/Shift/Super`
  (`modifier.rs:5-11`) and xkb keysym names (`sym.rs:39-54`).
- **Restore = remove** the XerahS entry on the matching `(modifiers, key)` (never write `Disable`).

*Tested by:* round-trip unit tests — write then re-parse; foreign-entry preservation; bare-`Print`
override + restore (assert the default reappears); `deny_unknown_fields` compliance (a serialized
entry must parse back without unknown-field errors).

### Step 4 — Add `CosmicHotkeyService : IHotkeyService`

New `IHotkeyService` implementation (`src/platform/XerahS.Platform.Abstractions/Services/IHotkeyService.cs:31-88`)
that delegates to the writer:

- `RegisterHotkey` → writer write + return `HotkeyStatus.Registered` (the binding genuinely works via
  the compositor); **no X11 grab**.
- `UnregisterHotkey` / `UnregisterAll` → writer remove.
- **No `HotkeyTriggered`** is raised here — the trigger arrives out-of-process via the spawned verb
  (Step 2), not from an in-process key grab. The interface's `HotkeyTriggered` event stays declared
  but unused by this backend.

*Tested by:* a fake-writer unit test verifying `RegisterHotkey` calls write-with-Spawn and reports
`Registered`, and `UnregisterHotkey` calls remove.

### Step 5 — Select `CosmicHotkeyService` in `LinuxPlatform.Initialize`

Extend the selection ternary (`src/platform/XerahS.Platform.Linux/LinuxPlatform.cs:72-77`, with
`isWayland` already at `:53`). Choose `CosmicHotkeyService` when
`isWayland && !hasGlobalShortcuts && desktop == COSMIC`, **before** the `LinuxHotkeyService` fallback:

```csharp
IHotkeyService hotkeyService =
    hasGlobalShortcuts ? new WaylandPortalHotkeyService()
    : (isWayland && !hasGlobalShortcuts && isCosmic) ? new CosmicShortcutConfigWriter-backed CosmicHotkeyService()
    : new LinuxHotkeyService();
```

COSMIC detection (`isCosmic`) and the `GlobalShortcutsUnavailable` status were added by the
XIP0077/XIP0078 implementation (on the `feature/linux-hotkeys-impl` branch — `DesktopEnvironmentDetector`
COSMIC branch, `PortalBackendKind.Cosmic`, `CompositorDetector` COSMIC case), so this step consumes
that detection rather than re-introducing it.

*Tested by:* a selection unit test (mock `isWayland`, `hasGlobalShortcuts=false`, `desktop=COSMIC`
→ `CosmicHotkeyService`; `desktop=Sway` → `LinuxHotkeyService`; portal present → `WaylandPortalHotkeyService`).

### Step 6 — UI: report `Registered`, drop the amber `GlobalShortcutsUnavailable` banner on COSMIC

Because Step 4 returns `HotkeyStatus.Registered`, the UI's existing binding paints green via
`HotkeyStatusColorConverter` (`src/desktop/app/XerahS.UI/ViewModels/HotkeyConverters.cs:40-54`,
`Registered → LimeGreen`). The XIP0078 amber `GlobalShortcutsUnavailable` banner — shown on COSMIC
before this feature — must no longer appear when `CosmicHotkeyService` is active, since the binding now
genuinely fires. Wire this through the `HotkeySettingsViewModel` save path and the `HotkeyConverters`
so COSMIC users see an honest green instead of the XIP0078 stopgap warning.

*Tested by:* manual COSMIC end-to-end (Verification) confirming green status and no amber banner.

---

## Risks

- **Clobbering the user's custom RON.** A blind overwrite would destroy hand-authored COSMIC
  shortcuts. *Mitigation:* strict read-merge-write that preserves all foreign entries and only
  touches XerahS-tagged ones (`description: "XerahS: …"`), per Step 3.
- **RON parse fragility.** Hand-rolled string concatenation can emit malformed RON the compositor
  silently skips (cosmic-comp logs and inserts `Action::Disable` for unparseable actions —
  cosmic-settings-daemon `config/src/shortcuts/mod.rs:122-135`). *Mitigation:* structured parse +
  round-trip tests (Step 3).
- **`deny_unknown_fields`.** Emitting any field beyond `modifiers`/`key`/`description` aborts the
  whole-map parse (`binding.rs:11-31`). *Mitigation:* writer emits only the declared fields.
- **Spawn path drift.** A relative or stale binary path breaks the spawn after an update/move.
  *Mitigation:* resolve the absolute path from `Environment.ProcessPath` at write time
  (`Program.cs:111`).
- **Single-instance interaction.** The second (spawned) process must reach the pipe, not start a rival
  GUI. *Mitigation:* keep the verb inside single-instance forwarding; never short-circuit before the
  mutex (`Program.cs:79-89`, `SingleInstanceManager.cs:66-81`).
- **Live-reload races.** cosmic-comp reloads on every file change (cosmic-comp `src/config/mod.rs:251-258`);
  multiple non-atomic writes could be read mid-write. *Mitigation:* one atomic temp+`File.Move` per
  save (`SettingsBase.cs:189-224`).
- **Restore must delete, not `Disable`.** Writing `Disable` on `Print` would suppress screenshots
  globally (override semantics, `mod.rs:34-49`). *Mitigation:* unbind = remove the entry.
- **Modifier / keysym token mismatch.** Emitting GTK accelerator names or X11 bitmasks would not parse
  as xkb keysyms / `Modifier` tokens. *Mitigation:* reuse the xkb mapper (see below); this risk is now
  resolved from source.
- **COSMIC detection gating.** Mis-detecting the desktop would mis-route. *Mitigation:* gate on the
  XIP0077-added COSMIC detection plus `isWayland && !hasGlobalShortcuts`.
- **Workflow-id stability.** A deleted/renamed workflow leaves a stale RON entry spawning a dead id.
  *Mitigation:* prune stale XerahS entries on save and rely on the ad-hoc subverb fallback (Step 2).

### Keysym source for the writer (avoid drift)

The writer must produce **xkb keysym names**, which are exactly what `LinuxHotkeyService` already
emits: reuse `LinuxHotkeyService.SpecialKeyNames`
(`src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:431`) and
`GetCandidateKeysymNames` (`LinuxHotkeyService.cs:369-422`). **Do not** reuse
`WaylandPortalHotkeyService.ShortcutKeyNames`
(`src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:967`) — that table targets
the GTK/portal accelerator world and overlaps only by coincidence; the two could diverge. Extract a
**single shared xkb keysym mapper** consumed by both `LinuxHotkeyService` and the COSMIC writer so they
cannot drift. The X11 modifier *mask* table `GetModifierMask`
(`LinuxHotkeyService.cs:310-334`) is **not** reusable — it emits X11 bitmasks
(`ControlMask`/`ShiftMask`/`Mod1Mask`/`Mod4Mask`), whereas COSMIC needs the `Ctrl/Alt/Shift/Super`
tokens (`modifier.rs:5-11`).

---

## Resolved-from-source vs Decisions

### Open items now resolved from source

The following were genuinely open when XIP0077/XIP0078 were written; reading COSMIC's source closes
them:

- **COSMIC `Modifier` tokens are `Ctrl`, `Alt`, `Shift`, `Super`** — no serde rename
  (cosmic-settings-daemon `config/src/shortcuts/modifier.rs:5-11`); `Super` ⇒ `Modifiers.logo`
  (`modifier.rs:46-60`, cosmic-comp `src/config/key_bindings.rs:40-54`).
- **Key casing = xkb keysym names** via `keysym_get_name` (`sym.rs:39-54`): **letters lowercase**
  (`Keysym::from_char` on the lowercased char, `binding.rs:65-72`), **underscores preserved** for
  specials (`Print`, `Num_Lock`, `Return`, `space`).
- **`description:` is an allowed declared field** under `deny_unknown_fields`
  (`binding.rs:11-31, 29-30`), so XerahS can safely tag its own entries.
- **Restore = remove the entry**, because cosmic-comp `extend`s `custom` over `defaults`
  (`mod.rs:34-49`); deleting the XerahS entry reapplies the default.

### Decisions (DECIDED)

- **Spawn the GUI binary `XerahS`, not the CLI `xerahscli`.** Routing through single-instance +
  `WorkflowOrchestrator` reuses the live configuration and the exact hotkey execution path
  (`WorkflowOrchestrator.cs:329-339, 353-454`); the CLI would start a separate, configuration-blind
  process.
- **Write the XIP before implementing.** This proposal is authored first so the cross-cutting changes
  (AppContracts contract, `ProcessIncomingArguments` dispatch, a new platform service, a new hotkey
  backend, the selection change, and UI status) are reviewed as one design.

---

## Verification Steps (manual COSMIC end-to-end)

1. **Baseline (pre-feature).** On a COSMIC Wayland session, confirm XerahS shows the XIP0078 amber
   `GlobalShortcutsUnavailable` status for an assigned hotkey, and that `grep -ri 'cosmic' src/`
   on this branch returns 0 matches (XIP0077 Issue C4) — i.e. no writer exists yet.
2. **Write the shortcut.** With the feature, assign e.g. `Ctrl+Shift+4 → region` in XerahS and confirm
   `<ConfigHome>/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom` now contains a
   `(modifiers: [Ctrl, Shift], key: "4", description: "XerahS: …"): Spawn("/abs/XerahS capture region --workflow-id <id>")`
   entry, with any pre-existing foreign entries preserved.
3. **Compositor live-reload + spawn.** Without restarting COSMIC, press `Ctrl+Shift+4` while XerahS is
   backgrounded; confirm a region capture runs (cosmic-comp reloaded via
   `src/config/mod.rs:251-258` and ran the `Spawn`).
4. **Single-instance forwarding.** Confirm only one XerahS process remains (the spawned one forwarded
   its argv and exited, `SingleInstanceManager.cs:66-81`) and the log shows
   "Arguments received from another instance" → `ProcessIncomingArguments` dispatching the verb
   (`Program.cs:774-818`).
5. **Same path as a real hotkey.** Confirm the capture went through
   `WorkflowOrchestrator.ExecuteWorkflowFromTriggerAsync` (`WorkflowOrchestrator.cs:353-454`) with the
   workflow resolved by `--workflow-id`.
6. **Bare `Print` override + restore.** Assign bare `Print → region` (overriding the default
   `System(Screenshot)`, `data/keybindings.ron:114`); confirm it fires. Then unbind in XerahS and
   confirm the **default screenshot returns** — i.e. XerahS deleted its entry rather than writing
   `Disable`.
7. **No clobber.** Manually add a non-XerahS custom shortcut in `cosmic-settings`, then add/remove a
   XerahS hotkey; confirm the foreign shortcut survives both writes.
8. **UI honesty.** Confirm the hotkey shows green `Registered`
   (`HotkeyConverters.cs:40-54`) and the XIP0078 amber banner is gone on COSMIC.
9. **Build integrity.** `dotnet build` passes with 0 errors and no new warnings (per AGENTS.md) before
   any push.

---

## Open Questions

1. **Workflow-id lifecycle.** When a workflow is deleted, should the writer eagerly prune its RON
   entry, or rely on the spawned verb failing the id lookup and falling back to the ad-hoc subverb
   (Step 2)? Eager pruning is cleaner but adds a delete-time hook.
2. **Conflict with the compositor's own defaults beyond `Print`.** If the user picks a combo COSMIC
   reserves (e.g. a `Super`-based action), `extend` will override it — is silently shadowing a
   compositor default acceptable, or should XerahS warn?
3. **Window-focus side effects.** `OnArgumentsReceived` raises and activates the main window before
   dispatch (`Program.cs:783-803`); for a backgrounded capture hotkey, should the verb path suppress
   the window raise to avoid flashing the GUI on every capture?
4. **Multi-seat / multiple config roots.** cosmic-config also has a *system* layer
   (`mod.rs:57`); does XerahS only ever write the per-user `custom` file, and is that always under
   `$XDG_CONFIG_HOME`?
5. **Portal arrival (XIP0077 Fix 4).** If `xdg-desktop-portal-cosmic` ships GlobalShortcuts, XerahS
   should prefer the portal and *stop* writing RON. Should it then remove its existing RON entries, or
   leave them as a working fallback?
6. **Generalisation.** Other compositors (Hyprland, Sway) have their own config-writer-able shortcut
   mechanisms. Should `CosmicHotkeyService` be the first of a small family of "config-writing" hotkey
   backends sharing the verb-dispatch path (Step 2)?

---

## References

**Code (this repo):**
- `src/desktop/app/XerahS.App/Program.cs:79-89` — single-instance attach + `ArgumentsReceived` subscribe
- `src/desktop/app/XerahS.App/Program.cs:111` — absolute binary path via `Environment.ProcessPath`
- `src/desktop/app/XerahS.App/Program.cs:774-818` — `OnArgumentsReceived`, dispatch to `ProcessIncomingArguments` at `:810`
- `src/desktop/app/XerahS.App/Program.cs:805` — the `// TODO: Process arguments …` gap (Verb-Dispatch Finding)
- `src/desktop/app/XerahS.App/Program.cs:820-866` — `ProcessIncomingArguments` (plugin/Send-to/paths only; no verb branch)
- `src/desktop/core/XerahS.Common/SingleInstanceManager.cs:66-81` — non-primary forwards argv and exits; `:84-87` re-raise
- `src/desktop/app/XerahS.UI/Services/WorkflowOrchestrator.cs:329-339` — `HotkeyManager_HotkeyTriggered` → trigger sink
- `src/desktop/app/XerahS.UI/Services/WorkflowOrchestrator.cs:353-454` — `ExecuteWorkflowFromTriggerAsync` (the shared sink)
- `src/desktop/app/XerahS.App/XerahS.App.csproj:16` — `<AssemblyName>XerahS</AssemblyName>` (GUI binary name)
- `src/desktop/cli/XerahS.CLI/Commands/CaptureCommand.cs:40-100` — `capture screen|window|region|transparent`
- `src/desktop/cli/XerahS.CLI/Commands/WorkflowCommand.cs:103-104` — workflow lookup by id
- `src/desktop/core/XerahS.Common/AppContracts.cs:49-71` — `AppContracts.Cli` flag constants (verb-contract home)
- `src/desktop/core/XerahS.Common/LinuxXdgDirectories.cs:31-95` — `ConfigHome`/`ConfigDirectory` resolution
- `src/desktop/core/XerahS.Common/SettingsBase.cs:189-224` — atomic temp + `File.Move` write pattern
- `src/platform/XerahS.Platform.Abstractions/Services/IHotkeyService.cs:31-88` — `IHotkeyService` contract
- `src/platform/XerahS.Platform.Linux/LinuxPlatform.cs:53` — `isWayland`; `:72-77` — hotkey-service ternary
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:310-334` — `GetModifierMask` (X11 bitmasks; NOT reusable)
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:369-422` — `GetCandidateKeysymNames` (xkb names)
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:431` — `SpecialKeyNames` (xkb keysym names)
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:967` — `ShortcutKeyNames` (GTK accel names; do NOT reuse)
- `src/desktop/app/XerahS.UI/ViewModels/HotkeyConverters.cs:40-54` — `HotkeyStatusColorConverter`
- `src/desktop/core/XerahS.Core/Enums.cs:168,171,180,183` — `WorkflowType` `PrintScreen`/`ActiveWindow`/`RectangleRegion`/`RectangleTransparent`
- COSMIC detection + `GlobalShortcutsUnavailable`: see the XIP0077/XIP0078 implementation (`feature/linux-hotkeys-impl` branch), not this tree.

**Upstream (COSMIC):**
- cosmic-settings-daemon `config/src/shortcuts/mod.rs:22,34-49,79-82,103-105,122-135` — store id/version, `custom` key, override `extend`, `Shortcuts` map, RON parse: <https://github.com/pop-os/cosmic-settings-daemon>
- cosmic-settings-daemon `config/src/shortcuts/binding.rs:11-31,65-72` — `Binding`, `deny_unknown_fields`, lowercase single-char keys: <https://github.com/pop-os/cosmic-settings-daemon>
- cosmic-settings-daemon `config/src/shortcuts/sym.rs:39-54` — key serialized via `xkb::keysym_get_name`: <https://github.com/pop-os/cosmic-settings-daemon>
- cosmic-settings-daemon `config/src/shortcuts/modifier.rs:5-11,46-60` — `Modifier` enum `Ctrl/Alt/Shift/Super`, `Super ⇒ logo`: <https://github.com/pop-os/cosmic-settings-daemon>
- cosmic-settings-daemon `config/src/shortcuts/action.rs:7-8,16,120,123` — `Action` enum, `Disable`/`System`/`Spawn(String)`: <https://github.com/pop-os/cosmic-settings-daemon>
- cosmic-comp `src/config/mod.rs:248-272` — watches and live-reloads the Shortcuts config: <https://github.com/pop-os/cosmic-comp>
- cosmic-comp `src/config/key_bindings.rs:40-54` — `logo` modifier comparison: <https://github.com/pop-os/cosmic-comp>
- cosmic-comp `data/keybindings.ron:114` — default `(modifiers: [], key: "Print"): System(Screenshot)`: <https://github.com/pop-os/cosmic-comp>
- cosmic-comp `Cargo.lock` — pins `cosmic-settings-config` rev `defa9f79`: <https://github.com/pop-os/cosmic-comp>

**Related XIPs:**
- `docs/proposals/xip/XIP0077-cosmic-desktop-global-hotkeys-and-detection.md` — COSMIC manual workaround (Phase 1)
- `docs/proposals/xip/XIP0078-wayland-global-hotkey-fallback-hardening.md` — `GlobalShortcutsUnavailable` honest-status stopgap
- XIP0044, XIP0046, XIP0061, XIP0029 — prior Linux/Wayland hotkey and portal work

---

## Changelog

| Date | Author | Description |
|---|---|---|
| 2026-06-09 | ElmoBlatch | Initial proposal — Phase 2 of XIP0077: XerahS writes the COSMIC cosmic-config `custom` RON map (`Spawn("<abs>/XerahS capture <kind> --workflow-id <id>")`) so cosmic-comp dispatches global hotkeys; identifies the verb-dispatch gap (`Program.cs:805` TODO; `ProcessIncomingArguments` drops the verb today), source-verifies the COSMIC RON grammar (`Modifier` `Ctrl/Alt/Shift/Super`, xkb keysym names, `deny_unknown_fields`, override-via-`extend`, restore-by-delete, live-reload), and lays out the 6-step implementation; supersedes XIP0078's amber stopgap on COSMIC (Open). |
