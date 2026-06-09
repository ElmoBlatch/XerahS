# XIP0077 COSMIC Desktop: Global Hotkeys Unsupported (no GlobalShortcuts portal, compositor-reserved PrintScreen, zero detection)

**Status**: Open
**Version**: v0.23.0

**Area**: Linux | Wayland | Hotkeys
**Affected platform**: COSMIC (Wayland) on CachyOS
**Related**: XIP0044 (X11 fallback hotkey failures, app ID mismatch, startup race, portal rebind debounce), XIP0046 (Print key mapping, GlobalShortcuts silent failure, InputCapture session failure, Spectacle launch on cancel), XIP0061 (KDE-specific portal backend behavior — ConfigureShortcuts/InputCapture gaps), XIP0078 (general "global hotkeys unsupported on this compositor" status mechanism), XIP0029 (Core DBus signal/interface stability for portal services)

---

## Overview

On the COSMIC desktop (System76's Rust/Wayland compositor, default on Pop!_OS 24.04 and installable on CachyOS), **PrintScreen and every other XerahS global hotkey silently do nothing** — yet the app still reports the hotkey as `Registered`. This is not a single bug. It is **three independent, stacked blockers**, any one of which alone is sufficient to break background hotkeys:

1. **No GlobalShortcuts portal on COSMIC.** `xdg-desktop-portal-cosmic` does not implement `org.freedesktop.portal.GlobalShortcuts`, so `LinuxPlatform` falls back to the X11-only `LinuxHotkeyService` instead of `WaylandPortalHotkeyService`.
2. **The compositor reserves bare `Print`.** `cosmic-comp` binds bare `Print` to its own `System(Screenshot)` action and consumes the key before any client (X11/XWayland *or* portal) can ever see it.
3. **COSMIC is invisible to XerahS detection.** It is not recognized by `CompositorDetector`, `DesktopEnvironmentDetector`, or `PortalBackendDetector` (`grep -ri 'cosmic' src/` returns **0** matches), so the app emits no warning or guidance and falls through to the wrong code path silently.

Crucially, **capture itself works fine on COSMIC** — `org.freedesktop.portal.Screenshot` is implemented, and raw capture works via `cosmic-screencopy` / the upstream `ext-image-copy-capture-v1` protocols (the path `grim` uses). The capture subsystem and the hotkey subsystem are independent; only the hotkey trigger is broken. A working **CLI workaround exists today** via `xerahscli` bound to a COSMIC custom shortcut.

This XIP documents the root cause with file:line evidence, the user workaround that works right now, and the maintainer-side fixes (first-class COSMIC detection plus an explicit "hotkeys unsupported" status, deferring the general status mechanism to XIP0078).

---

## Problem Statement

On COSMIC (Wayland), a user configures a global hotkey in XerahS — for example `Print` for region capture, or `Ctrl+Shift+4` for a workflow — and:

- The hotkey **never fires** when XerahS is backgrounded or another Wayland window has focus.
- Bare `Print` **never fires at all**, even when XerahS is the focused window, because the compositor swallows it upstream.
- XerahS's hotkey UI reports the binding as **successfully registered** (`HotkeyStatus.Registered`) — a **false positive**. There is no error, no warning, and no hint that the platform is unsupported.

The result is a silent, confusing failure: the user did everything right, the app says it worked, and nothing happens. This mirrors the general Linux limitation already documented in `KNOWN_ISSUES.md` (`KNOWN_ISSUES.md:14-17` — "global hotkeys currently only trigger when XerahS is the active window"), but COSMIC is strictly worse than the average Wayland case because **even the focused-window path fails for bare `Print`**, and because XerahS has **zero** awareness that it is running on COSMIC.

---

## Root Cause Analysis

Three independent blockers stack. Each is sufficient on its own to break background hotkeys; together they guarantee silent failure with a misleading "registered" status.

### Issue A — COSMIC has no GlobalShortcuts portal, so XerahS selects the X11 fallback

The hotkey backend is chosen by a single ternary in `LinuxPlatform`. It pivots entirely on whether `org.freedesktop.portal.GlobalShortcuts` is advertised on the D-Bus session:

`src/platform/XerahS.Platform.Linux/LinuxPlatform.cs:72-77`
```csharp
bool hasGlobalShortcuts = usePortalServices && PortalInterfaceChecker.HasInterface("org.freedesktop.portal.GlobalShortcuts");
bool hasInputCapture = usePortalServices && PortalInterfaceChecker.HasInterface("org.freedesktop.portal.InputCapture");

IHotkeyService hotkeyService = hasGlobalShortcuts
    ? new WaylandPortalHotkeyService()
    : new LinuxHotkeyService();
```

`HasInterface` introspects `org.freedesktop.portal.Desktop` over D-Bus; the interface only returns `true` if a running portal backend advertises it:

`src/platform/XerahS.Platform.Linux/Services/PortalInterfaceChecker.cs:43-51`
```csharp
public static bool HasInterface(string interfaceName)
{
    if (string.IsNullOrWhiteSpace(interfaceName))
    {
        return false;
    }

    return Cache.GetOrAdd(interfaceName, _ => CheckInterface(interfaceName));
}
```

On COSMIC, no backend advertises `GlobalShortcuts`. **`xdg-desktop-portal-cosmic` does not implement `org.freedesktop.portal.GlobalShortcuts`** — its portal manifest exposes only `Access`, `FileChooser`, `Screenshot`, `Settings`, and `ScreenCast`, and there is no `global_shortcuts.rs` in its source tree. The feature request ([pop-os/xdg-desktop-portal-cosmic#4](https://github.com/pop-os/xdg-desktop-portal-cosmic/issues/4)) has been open since 2023-05, and a maintainer declined a community D-Bus implementation in 2026-03 (manifest: <https://raw.githubusercontent.com/pop-os/xdg-desktop-portal-cosmic/master/data/cosmic.portal>).

So `hasGlobalShortcuts` is `false`, and `LinuxPlatform` constructs `LinuxHotkeyService` — the **X11-only** path. That service connects to X11 and grabs keys on the root window:

`src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:61-68`
```csharp
_display = NativeMethods.XOpenDisplay(null);
if (_display == IntPtr.Zero)
{
    DebugHelper.WriteLine("LinuxHotkeyService: Unable to open X display; hotkeys disabled.");
    return;
}

_rootWindow = NativeMethods.XDefaultRootWindow(_display);
```

The grab succeeds at the X server level, so `RegisterHotkey` sets the status to `Registered`:

`src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:155-190` (status set after a successful `TryGrab`)
```csharp
hotkeyInfo.Status = HotkeyStatus.Registered;
return true;
```

`TryGrab` only treats `BadAccess` (an already-grabbed combination) as failure — nothing else is checked, and event-delivery capability is never validated:

`src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:251-300`
```csharp
private static int HandleXError(IntPtr display, ref XErrorEvent error)
{
    if (error.error_code == NativeMethods.BadAccess)
    {
        _grabError = true;
    }

    return 0;
}

private (bool success, string? error) TryGrab(HotkeyRegistration registration)
{
    var grabbed = new List<uint>();

    // Install our error handler to catch BadAccess errors
    IntPtr previousHandler = NativeMethods.XSetErrorHandler(_errorHandler);

    try
    {
        foreach (var mask in registration.GrabMasks)
        {
            _grabError = false;

            _ = NativeMethods.XGrabKey(_display, registration.Keycode, mask, _rootWindow, false, NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync);

            // Sync to ensure any errors are processed before we check
            NativeMethods.XSync(_display, false);

            if (_grabError)
            {
                return (false, "Key combination is already grabbed by another application");
            }

            grabbed.Add(mask);
        }

        return (true, null);
    }
    finally
    {
        NativeMethods.XSetErrorHandlerPtr(previousHandler);
    }
}
```

> [!IMPORTANT]
> **This is the false-positive.** The X11 grab succeeds on XWayland (no `BadAccess`), so `RegisterHotkey` returns `true` and the UI shows `HotkeyStatus.Registered`. But `XGrabKey` + `XNextEvent` only deliver key events when an **X11/XWayland window has keyboard focus**. On a Wayland session, when a *Wayland-native* window (or the desktop) has focus, the events are routed by the Wayland compositor and never reach the X server — so the grab is real at the X level but **dead** for background use. The status truthfully reports "the X grab succeeded," which is misleadingly interpreted by the user as "the hotkey works."

### Issue B — `cosmic-comp` reserves bare `Print` at the compositor level

Even if Issue A were fixed (e.g. via a future portal, or via XWayland focus), bare `Print` would still fail on COSMIC because **the compositor itself binds it**. `cosmic-comp`'s default keybindings include `(modifiers: [], key: "Print"): System(Screenshot)`, and the compositor consumes the bound key before any client sees it (demonstrated with `wev` in [pop-os/cosmic-epoch#2481](https://github.com/pop-os/cosmic-epoch/issues/2481)). System76's own documentation confirms "**Print** — Take a screenshot." (keybindings: <https://raw.githubusercontent.com/pop-os/cosmic-comp/master/data/keybindings.ron>; shortcut docs: <https://system76.com/support/articles/pop-cosmic-keyboard-shortcuts/>).

This is a compositor-level grab. It is **above** both the X11 layer and any future portal layer: a client cannot register `Print` while the compositor has reserved it, regardless of which XerahS backend is active. The user must **unbind** the default `Print → System(Screenshot)` action in `cosmic-settings` before any application — XerahS included — can use bare `Print`. This is independent of Issue A and is not fixable in XerahS code; it must be handled by user configuration (see User Workaround) and by guidance text (see Proposed Fixes).

### Issue C — COSMIC is unrecognized across all three detectors

XerahS has **no awareness** that it is running on COSMIC, so it cannot route to a correct path or emit any warning. There are three detection gaps, plus a repository-wide confirmation.

**C1 — `DesktopEnvironmentDetector.NormalizeHint` has no COSMIC branch.** It maps GNOME/Ubuntu/Unity/Budgie/Pantheon, KDE/Plasma, Hyprland, Sway, XFCE, MATE, Cinnamon, LXQt, LXDE — and falls through to `null` for everything else:

`src/platform/XerahS.Platform.Linux/Capture/Detection/DesktopEnvironmentDetector.cs:49-111`
```csharp
internal static string? NormalizeHint(string? hint)
{
    if (string.IsNullOrWhiteSpace(hint))
    {
        return null;
    }

    foreach (string token in hint.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string normalized = token.ToUpperInvariant();

        if (normalized.Contains("GNOME") ||
            normalized.Contains("UBUNTU") ||
            normalized.Contains("UNITY") ||
            normalized.Contains("BUDGIE") ||
            normalized.Contains("PANTHEON"))
        {
            return "GNOME";
        }

        if (normalized.Contains("KDE") || normalized.Contains("PLASMA"))
        {
            return "KDE";
        }

        if (normalized.Contains("HYPRLAND"))
        {
            return "HYPRLAND";
        }

        if (normalized.Contains("SWAY"))
        {
            return "SWAY";
        }

        if (normalized.Contains("XFCE"))
        {
            return "XFCE";
        }

        if (normalized.Contains("MATE"))
        {
            return "MATE";
        }

        if (normalized.Contains("CINNAMON"))
        {
            return "CINNAMON";
        }

        if (normalized.Contains("LXQT"))
        {
            return "LXQT";
        }

        if (normalized.Contains("LXDE"))
        {
            return "LXDE";
        }
    }

    return null;
}
```

`XDG_CURRENT_DESKTOP=COSMIC` therefore normalizes to `null`, so `LinuxRuntimeEnvironment.Desktop` is `null` and no COSMIC-specific routing hint is ever available.

**C2 — `PortalBackendDetector.ProbeRunningBackends` does not probe `xdg-desktop-portal-cosmic`, and `PortalBackendKind` has no COSMIC variant.**

`src/platform/XerahS.Platform.Linux/Capture/Detection/PortalBackendDetector.cs:108-118`
```csharp
private static PortalBackendSnapshot ProbeRunningBackends()
{
    return new PortalBackendSnapshot(
        HasKde: IsProcessRunning("xdg-desktop-portal-kde"),
        HasGnome: IsProcessRunning("xdg-desktop-portal-gnome"),
        HasGtk: IsProcessRunning("xdg-desktop-portal-gtk"),
        HasWlr: IsProcessRunning("xdg-desktop-portal-wlr"),
        HasHyprland: IsProcessRunning("xdg-desktop-portal-hyprland"),
        HasLxqt: IsProcessRunning("xdg-desktop-portal-lxqt"),
        HasXapp: IsProcessRunning("xdg-desktop-portal-xapp"));
}
```

`src/platform/XerahS.Platform.Linux/Capture/Detection/PortalBackendDetector.cs:210-218`
```csharp
internal enum PortalBackendKind
{
    Unknown,
    Gtk,
    Gnome,
    Kde,
    Lxqt,
    Xapp
}
```

`xdg-desktop-portal-cosmic` is never probed, and there is no `Cosmic` enum value to represent it — so even a running COSMIC portal is classified `Unknown`.

**C3 — `CompositorDetector` has no COSMIC case.** Per the cross-reference inventory, `CompositorDetector.cs` distinguishes Hyprland, Sway, and a generic `WAYLAND` fallback but has no COSMIC branch (`src/platform/XerahS.Platform.Linux/Capture/Detection/CompositorDetector.cs`).

**C4 — Repository-wide confirmation.** `grep -ri 'cosmic' src/` returns **0 matches** across the entire source tree, confirming there are zero COSMIC-specific code paths anywhere in XerahS. COSMIC is treated as an unknown environment and falls through to generic Wayland / X11 paths that are insufficient for its hotkey requirements.

The combined effect: XerahS cannot tell it is on COSMIC, picks the X11 fallback (Issue A), reports a false `Registered` status, and emits no warning — while the compositor (Issue B) guarantees bare `Print` never arrives.

---

## What Works on COSMIC

The hotkey breakage is narrowly scoped. **Screen capture itself works on COSMIC** and is independent of the hotkey subsystem:

- COSMIC **does** implement `org.freedesktop.portal.Screenshot`. For raw capture it uses `cosmic-screencopy`, and it later added the upstream `ext-image-capture-source-v1` / `ext-image-copy-capture-v1` protocols — the same path `grim` uses.
- COSMIC does **not** provide `wlr-screencopy` ([pop-os/cosmic-comp#560](https://github.com/pop-os/cosmic-comp/issues/560), closed), which is why screenshot tools that hard-depend on `wlr-screencopy` (e.g. Flameshot) fail on COSMIC ([flameshot/flameshot#4260](https://github.com/flameshot-org/flameshot/issues/4260), [pop-os/cosmic-comp#710](https://github.com/pop-os/cosmic-comp/issues/710)). XerahS's portal Screenshot path and the `grim`/`ext-image-copy-capture` path are unaffected.

In other words: **only the hotkey trigger is broken on COSMIC.** Once a capture is triggered by any means (including the CLI), it completes normally. This is what makes the CLI workaround below fully functional.

---

## User Workaround (works today)

Because capture works and only the *global-hotkey trigger* is broken, the reliable path on COSMIC is to bind a **COSMIC custom shortcut** directly to the XerahS CLI. This bypasses both Issue A (X11 fallback) and the in-app hotkey system entirely — COSMIC itself becomes the hotkey dispatcher.

The CLI binary is named **`xerahscli`** (`src/desktop/cli/XerahS.CLI/XerahS.CLI.csproj:14` — `<AssemblyName>xerahscli</AssemblyName>`) and exposes capture and workflow subcommands suitable for shortcut bindings (`src/desktop/cli/XerahS.CLI/Commands/CaptureCommand.cs:42-99` registers `screen`, `window`, `region`, and `transparent`):

```text
xerahscli capture screen        # full screen
xerahscli capture window        # active window
xerahscli capture region --region 0,0,400,400
xerahscli capture transparent   # transparent region
xerahscli workflow run <id>     # run a configured workflow by id
```

**Steps:**

1. **Ensure capture deps are present.** Install `grim` and `slurp` so the `ext-image-copy-capture` / region path works on COSMIC Wayland.
2. **Unbind the compositor's default `Print` (Issue B).** In `cosmic-settings`: **Settings > Input Devices > Keyboard > Keyboard shortcuts**, remove or change the built-in `Print → Screenshot` binding. Until this is done, the compositor swallows `Print` and no app can use it.
3. **Create a custom shortcut.** In `cosmic-settings`: **Settings > Input Devices > Keyboard > Keyboard shortcuts > Custom**, add a new shortcut whose command is e.g. `xerahscli capture region` (or `xerahscli workflow run <id>`), and assign your key.

> [!WARNING]
> **Bare/non-modifier key caveat.** Binding a *bare* key (like `Print`, with no modifier) through the COSMIC GUI historically does not stick — you must manually edit `~/.config/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom` and set `modifiers` to `[]` for that entry ([pop-os/cosmic-settings#597](https://github.com/pop-os/cosmic-settings/issues/597), open since 2024-09). Modifier-based shortcuts (e.g. `Super+Print`, `Ctrl+Shift+4`) bind fine through the GUI and avoid this caveat entirely; prefer them.

This workaround is robust because it does not depend on any XerahS in-process key grab — the capture runs through the working Screenshot portal / `grim` path described in **What Works on COSMIC**.

---

## Implementation Guidance

### Proposed Fixes (maintainer)

These fixes make XerahS COSMIC-aware so it stops silently picking the wrong path and instead tells the user the truth and points them at the working workaround. The **general** "global hotkeys unsupported on this compositor" status mechanism is deferred to XIP0078; this XIP wires COSMIC into that mechanism and adds COSMIC detection.

#### Fix 1 — First-class COSMIC detection across all three detectors

Add COSMIC recognition so the rest of the codebase can route and warn correctly.

- **`DesktopEnvironmentDetector.NormalizeHint`** (`src/platform/XerahS.Platform.Linux/Capture/Detection/DesktopEnvironmentDetector.cs:49-111`): add a branch `if (normalized.Contains("COSMIC")) return "COSMIC";` so `XDG_CURRENT_DESKTOP=COSMIC` no longer normalizes to `null`.
- **`PortalBackendDetector`** (`src/platform/XerahS.Platform.Linux/Capture/Detection/PortalBackendDetector.cs:108-118` and `:210-218`): add `HasCosmic: IsProcessRunning("xdg-desktop-portal-cosmic")` to `ProbeRunningBackends` / `PortalBackendSnapshot`, and add a `Cosmic` member to `PortalBackendKind`.
- **`CompositorDetector`** (`src/platform/XerahS.Platform.Linux/Capture/Detection/CompositorDetector.cs`): add a COSMIC branch alongside the existing Hyprland/Sway/generic-Wayland cases.

#### Fix 2 — Surface an explicit "global hotkeys unsupported on this compositor" status with COSMIC guidance

Today the X11 fallback reports `HotkeyStatus.Registered` even when delivery is impossible (`src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:155-190`). When XerahS detects COSMIC + Wayland and `org.freedesktop.portal.GlobalShortcuts` is absent (`LinuxPlatform.cs:72-77`), it should **not** advertise a working grab. Instead it should expose an explicit "unsupported" status and a guidance string pointing to the CLI custom-shortcut workaround above. The general status enum/plumbing is defined in **XIP0078**; this fix supplies the COSMIC detection inputs (Fix 1) and the COSMIC-specific guidance copy. The guidance must mention: unbinding the compositor's `Print` (Issue B) and binding a custom shortcut to `xerahscli`.

#### Fix 3 — Document a stable CLI entry point for DE custom shortcuts

The workaround depends on `xerahscli` being a stable, documented contract. Confirm and document `xerahscli capture {screen|window|region|transparent}` and `xerahscli workflow run <id>` (`src/desktop/cli/XerahS.CLI/Commands/CaptureCommand.cs:42-99`; binary name `src/desktop/cli/XerahS.CLI/XerahS.CLI.csproj:14`) as the supported integration point for COSMIC (and other DE) custom shortcuts, and reference it from `KNOWN_ISSUES.md` alongside the existing PrintScreen + folder-watch note (`KNOWN_ISSUES.md:14-17`).

#### Fix 4 — Re-probe GlobalShortcuts at runtime so XerahS adopts it if COSMIC ships it

The backend choice is computed once at startup (`LinuxPlatform.cs:72-77`) and `PortalInterfaceChecker` caches the result (`PortalInterfaceChecker.cs:43-51`). If COSMIC later implements `org.freedesktop.portal.GlobalShortcuts` (tracking [pop-os/xdg-desktop-portal-cosmic#4](https://github.com/pop-os/xdg-desktop-portal-cosmic/issues/4)), XerahS should detect it without a code change: add a path to invalidate the `PortalInterfaceChecker` cache and re-select the hotkey service (preferring `WaylandPortalHotkeyService` when `GlobalShortcuts` becomes available), e.g. on D-Bus `NameOwnerChanged` for the portal or on a manual "re-detect" action. This keeps the fix future-proof and avoids hard-coding COSMIC as permanently unsupported.

---

## Verification Steps

1. **Reproduce the false positive.** On a COSMIC Wayland session (Pop!_OS 24.04 or CachyOS + COSMIC), set a global hotkey in XerahS (e.g. region capture on `Ctrl+Shift+4`). Confirm the UI shows it as registered, then background XerahS and press the key — capture does not fire. Confirm via `wev` that bare `Print` is consumed by the compositor and never reaches a client (Issue B).
2. **Confirm backend selection.** With logging on, verify `LinuxPlatform` constructed `LinuxHotkeyService` (not `WaylandPortalHotkeyService`) because `HasInterface("org.freedesktop.portal.GlobalShortcuts")` returned `false` (`LinuxPlatform.cs:72-77`).
3. **Confirm detection gap (pre-fix).** Run `grep -ri 'cosmic' src/` and confirm 0 matches; confirm `XDG_CURRENT_DESKTOP=COSMIC` yields `LinuxRuntimeEnvironment.Desktop == null` via `NormalizeHint` (`DesktopEnvironmentDetector.cs:49-111`).
4. **Verify the workaround.** Install `grim`+`slurp`, unbind the compositor `Print`, add a COSMIC custom shortcut to `xerahscli capture region`, and confirm a region capture completes while XerahS is backgrounded. Repeat for a bare-key binding to validate the `modifiers: []` config-file caveat ([cosmic-settings#597](https://github.com/pop-os/cosmic-settings/issues/597)).
5. **Verify Fix 1.** After adding COSMIC branches, confirm `DesktopEnvironmentDetector` returns `"COSMIC"`, `PortalBackendDetector` reports `Cosmic`/`HasCosmic`, and `CompositorDetector` reports COSMIC on a COSMIC session.
6. **Verify Fix 2.** Confirm the hotkey UI no longer reports a working/`Registered` grab on COSMIC-without-GlobalShortcuts and instead shows the "unsupported on this compositor" status with the `xerahscli` custom-shortcut guidance.
7. **Verify Fix 4 (forward-compat).** Simulate `org.freedesktop.portal.GlobalShortcuts` becoming available (e.g. mock backend) and confirm XerahS re-selects `WaylandPortalHotkeyService` without restart after cache invalidation.
8. **Build integrity.** `dotnet build` passes with 0 errors and no new warnings (per AGENTS.md) before any push.

---

## Open Questions

1. **Should the X11 fallback ever set `HotkeyStatus.Registered` on a Wayland session?** Reporting a real-but-undeliverable grab as `Registered` is the core of the false positive. Should the status be conditioned on `IsWayland` + delivery viability globally (not just on COSMIC), and does that belong here or in XIP0078?
2. **Detection precedence.** Is `XDG_CURRENT_DESKTOP` reliable enough on COSMIC, or should detection also key on `xdg-desktop-portal-cosmic` running and/or a COSMIC-specific Wayland global, to avoid misclassifying remixes that set unusual `XDG_CURRENT_DESKTOP` values?
3. **Bare-`Print` guidance scope.** Should XerahS detect that the compositor still holds `Print` (Issue B) and warn specifically, or only document the unbind step? Detecting compositor keybindings programmatically on COSMIC is non-trivial.
4. **Forward-compat trigger.** For Fix 4, is D-Bus `NameOwnerChanged` watching worth the complexity, or is a manual "re-detect portals" action sufficient until/unless [#4](https://github.com/pop-os/xdg-desktop-portal-cosmic/issues/4) lands?

---

## References

**Code (this repo):**
- `src/platform/XerahS.Platform.Linux/LinuxPlatform.cs:72-77` — hotkey backend ternary on `GlobalShortcuts`
- `src/platform/XerahS.Platform.Linux/Services/PortalInterfaceChecker.cs:43-51` — D-Bus interface introspection + cache
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:61-68, 155-190, 251-300` — X11 `XOpenDisplay`/`XGrabKey`, `HotkeyStatus.Registered`, `TryGrab`/`HandleXError`
- `src/platform/XerahS.Platform.Linux/Capture/Detection/DesktopEnvironmentDetector.cs:49-111` — `NormalizeHint` (no COSMIC branch)
- `src/platform/XerahS.Platform.Linux/Capture/Detection/PortalBackendDetector.cs:108-118, 210-218` — `ProbeRunningBackends`, `PortalBackendKind` (no COSMIC)
- `src/platform/XerahS.Platform.Linux/Capture/Detection/CompositorDetector.cs` — Wayland/X11 detection (no COSMIC case)
- `src/desktop/cli/XerahS.CLI/Commands/CaptureCommand.cs:42-99` — `capture screen|window|region|transparent`
- `src/desktop/cli/XerahS.CLI/XerahS.CLI.csproj:14` — `<AssemblyName>xerahscli</AssemblyName>`
- `KNOWN_ISSUES.md:14-17` — Linux global hotkeys limitation + PrintScreen/folder-watch workaround

**Related XIPs:**
- `docs/proposals/xip/XIP0044-linux-global-hotkeys-not-firing-when-app-is-backgrounded.md` — X11 fallback hotkey failures, app ID mismatch, startup race, portal rebind debounce
- `docs/proposals/xip/XIP0046-linux-portal-hotkey-issues.md` — Print key mapping; GlobalShortcuts silent failure; InputCapture session failure; Spectacle launch on cancel
- `docs/proposals/xip/XIP0061-kde-plasma-nobara-portal-screenshot-issues.md` — KDE-specific portal backend behavior (ConfigureShortcuts/InputCapture gaps)
- XIP0078 — general "global hotkeys unsupported on this compositor" status mechanism (deferred from this XIP)
- `docs/proposals/xip/XIP0029-fix-wayland-portal-dbus-errors.md` — core DBus signal/interface stability for portal services

**Upstream (COSMIC):**
- No GlobalShortcuts portal: <https://github.com/pop-os/xdg-desktop-portal-cosmic/issues/4> ; manifest: <https://raw.githubusercontent.com/pop-os/xdg-desktop-portal-cosmic/master/data/cosmic.portal>
- `cosmic-comp` reserves bare `Print`: <https://raw.githubusercontent.com/pop-os/cosmic-comp/master/data/keybindings.ron> ; demo: <https://github.com/pop-os/cosmic-epoch/issues/2481> ; docs: <https://system76.com/support/articles/pop-cosmic-keyboard-shortcuts/>
- Screenshot works / no `wlr-screencopy`: <https://github.com/pop-os/cosmic-comp/issues/560> ; Flameshot fails: <https://github.com/flameshot-org/flameshot/issues/4260> , <https://github.com/pop-os/cosmic-comp/issues/710>
- Bare-key custom shortcut config-file caveat: <https://github.com/pop-os/cosmic-settings/issues/597>

---

## Changelog

| Date | Author | Description |
|---|---|---|
| 2026-06-08 | ElmoBlatch | Initial proposal — COSMIC global hotkeys unsupported: three stacked blockers (no GlobalShortcuts portal, compositor-reserved Print, zero detection), CLI workaround, and maintainer fixes documented (Open). |
