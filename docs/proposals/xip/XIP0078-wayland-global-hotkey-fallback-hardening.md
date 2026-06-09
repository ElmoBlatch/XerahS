# XIP0078 Wayland Global-Hotkey Fallback Hardening (honest HotkeyStatus; no silent dead X11 grab)

**Status**: Open
**Version**: v0.23.0

**Area**: Linux | Wayland | Hotkeys
**Related**: XIP0029 (Core DBus signal/interface stability for portal services), XIP0044 (Portal hotkey registration, app ID mismatch, parentWindow startup race, packaging symlink), XIP0046 (Print key mapping, region-selection CLI fallbacks, portal UI variance across DEs), XIP0061 (KDE-specific portal backend behavior; `ConfigureShortcuts` not implemented, version dependency on `xdg-desktop-portal-kde`), XIP0075 (XDG / Flathub readiness and Linux packaging standards), XIP0077 (COSMIC custom-shortcut user guidance)

---

## Overview

This proposal addresses a **distinct, more general** Linux hotkey failure mode than the ones
resolved in XIP0044 and XIP0046. Those XIPs fixed failures on compositors that *do* implement
the `org.freedesktop.portal.GlobalShortcuts` portal — app-ID mismatch (`Application.Name = "xerahs"`),
the `parentWindow=<empty>` startup race, `Key.Print` enum mapping, and rebind debounce. Those
fixes assume the portal exists and is reachable.

XIP0078 covers what happens when **no GlobalShortcuts portal exists at all** on a Wayland
session — for example COSMIC, or a wlroots compositor that has not shipped the GlobalShortcuts
backend. In that situation `LinuxPlatform.Initialize` silently falls back to the X11
`LinuxHotkeyService`, which **cannot** deliver global hotkeys on a native Wayland session.
Worse, under XWayland the X11 grab *succeeds* and the service reports `HotkeyStatus.Registered`
— a false positive. The UI then paints the hotkey green while nothing ever fires. Without
XWayland present the path reports `UnsupportedPlatform` and disables hotkeys outright.

The goal of this XIP is **honesty**: when XerahS cannot deliver a global hotkey on the current
compositor, it must say so (a dedicated status), surface that to the user with actionable
guidance, and stop reporting `Registered` for a grab whose events will never arrive.

> [!NOTE]
> This XIP does not propose removing the X11 backend. The X11 grab is correct and desirable on
> a real X11 session. The problem is *silently* using it on Wayland and *mislabeling* the result.

---

## Problem Statement

On a Wayland session **without** the GlobalShortcuts portal, two independent code paths reach
the X11 grab backend, and neither communicates the consequence to the user:

- **Issue A — Startup-time silent fallback.** `LinuxPlatform.Initialize` computes
  `hasGlobalShortcuts` from a one-shot portal probe and, when it is `false`, constructs a
  `LinuxHotkeyService` with no Wayland-specific override and no log line noting the degraded path
  (`src/platform/XerahS.Platform.Linux/LinuxPlatform.cs:72-77`).

- **Issue B — Runtime fallback after `response=2`.** When the portal *is* selected but
  `BindShortcuts` returns the non-recoverable `response=2`, `WaylandPortalHotkeyService` activates
  the same X11 backend via `ActivateFallbackHotkeys`, again with no display-server viability check
  (`src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:340-343`,
  `:650-683`).

- **Issue C — False-positive `Registered`.** `LinuxHotkeyService.RegisterHotkey` sets
  `HotkeyStatus.Registered` whenever `TryGrab` succeeds. `TryGrab` only inspects `BadAccess`
  (a grab *conflict*); it has no concept of "the compositor will consume this key and never
  deliver `KeyPress` to the X11 server" (`LinuxHotkeyService.cs:179-189`, `:261-300`). On
  XWayland the grab succeeds, so the status is green while the hotkey is dead.

- **Issue D — No event-delivery health signal.** The X11 event loop simply sleeps when
  `XPending` returns `0`; there is no timeout, no "events have stopped arriving" detection, and no
  way for any backend to report delivery health up through the contract
  (`LinuxHotkeyService.cs:81-110`; `IHotkeyService` exposes no such hook,
  `src/platform/XerahS.Platform.Abstractions/Services/IHotkeyService.cs:31-88`).

- **Issue E — Nothing surfaces to the user.** `HotkeyStatus` has no value for "registered but
  non-functional" (`HotkeyInfo.cs:174-181`), the UI color converter has no case for one
  (`HotkeyConverters.cs:40-54`), and `WorkflowManager.ShowFailedHotkeys` only logs `Failed`
  hotkeys to `Debug` output, never to a user-facing dialog
  (`src/desktop/core/XerahS.Core/Hotkeys/WorkflowManager.cs:258-277`).

Net effect: on COSMIC / portal-less wlroots, a user assigns a hotkey, sees it marked green
(under XWayland) or silently disabled (no XWayland), minimizes the window, presses the key, and
nothing happens — with no explanation.

---

## Root Cause Analysis

### Issue A — Selection-time fallback in `LinuxPlatform` is silent and Wayland-blind

`LinuxPlatform.Initialize` already knows it is on Wayland — `bool isWayland = environment.IsWayland;`
is computed at `LinuxPlatform.cs:53`. But the hotkey-service selection a few lines later ignores
it entirely:

```csharp
bool hasGlobalShortcuts = usePortalServices && PortalInterfaceChecker.HasInterface("org.freedesktop.portal.GlobalShortcuts");
bool hasInputCapture = usePortalServices && PortalInterfaceChecker.HasInterface("org.freedesktop.portal.InputCapture");

IHotkeyService hotkeyService = hasGlobalShortcuts
    ? new WaylandPortalHotkeyService()
    : new LinuxHotkeyService();
```

(`LinuxPlatform.cs:72-77`)

When `hasGlobalShortcuts` is `false` on a Wayland session, the ternary unconditionally constructs
the X11 `LinuxHotkeyService`. There is no `isWayland && !hasGlobalShortcuts` branch, no warning
log, and no marker carried forward that the resulting service is a Wayland-incompatible fallback.
This is the **startup-time** origin of the false-positive.

### Issue B — Runtime `response=2` fallback re-enters the same dead X11 path

The runtime variant lives in `WaylandPortalHotkeyService`. When the portal is selected but the
bind fails non-recoverably:

```csharp
catch (PortalBindFailedException ex) when (ex.ResponseCode == 2)
{
    DebugHelper.WriteException(ex, "WaylandPortalHotkeyService: Portal bind failed with non-recoverable response (2); enabling X11 fallback");
    ActivateFallbackHotkeys("portal BindShortcuts failed with response=2");
}
```

(`WaylandPortalHotkeyService.cs:340-343`)

`ActivateFallbackHotkeys` then constructs/uses a `LinuxHotkeyService` and re-registers every
hotkey, stamping each result purely from grab success:

```csharp
foreach (var hotkey in snapshot)
{
    bool ok = _fallbackHotkeyService.RegisterHotkey(hotkey);
    hotkey.Status = ok ? PlatformHotkeyStatus.Registered : PlatformHotkeyStatus.Failed;
    if (!ok)
    {
        DebugHelper.WriteLine($"WaylandPortalHotkeyService: X11 fallback failed to register {hotkey}");
    }
}
```

(`WaylandPortalHotkeyService.cs:650-683`)

This is the same problem as Issue A but reached at runtime: the fallback is taken without checking
whether X11 event delivery is even possible on this display server, and the status is derived from
the same grab-only signal.

> [!IMPORTANT]
> Issues A and B are *different triggers for the same defect*. Any fix that only patches
> `LinuxPlatform` selection still leaves the runtime `response=2` path reporting false positives,
> and vice versa. Both call sites must be corrected, ideally by funneling them through a single
> "is X11 event delivery viable here?" decision.

### Issue C — `Registered` is set from a `BadAccess`-only grab

`LinuxHotkeyService.RegisterHotkey` promotes the hotkey to `Registered` immediately after a
successful grab:

```csharp
var (success, message) = TryGrab(registration);
if (!success)
{
    hotkeyInfo.Status = HotkeyStatus.Failed;
    DebugHelper.WriteLine($"LinuxHotkeyService: Failed to grab {hotkeyInfo}. {message}");
    return false;
}

_registrations[hotkeyInfo.Id] = registration;
hotkeyInfo.Status = HotkeyStatus.Registered;
return true;
```

(`LinuxHotkeyService.cs:179-189`)

`TryGrab` only distinguishes "another app already grabbed this combo" from "grab installed":

```csharp
_ = NativeMethods.XGrabKey(_display, registration.Keycode, mask, _rootWindow, false, NativeMethods.GrabModeAsync, NativeMethods.GrabModeAsync);

// Sync to ensure any errors are processed before we check
NativeMethods.XSync(_display, false);

if (_grabError)
{
    // ... rollback ...
    return (false, "Key combination is already grabbed by another application");
}
```

(`LinuxHotkeyService.cs:261-300`; the X error handler that toggles `_grabError` only checks
`BadAccess`, `LinuxHotkeyService.cs:251-259`)

On XWayland, `XGrabKey` returns no `BadAccess` because nothing on the X side conflicts — the
Wayland compositor owns the physical key and never forwards it to the X server. So
`success == true`, `Status = Registered`, and the hotkey is silently dead.

### Issue D — The event loop has no delivery health check

The X11 loop polls `XNextEvent` and, when nothing is pending, just sleeps:

```csharp
if (NativeMethods.XPending(_display) > 0)
{
    _ = NativeMethods.XNextEvent(_display, out var xevent);
    if (xevent.type == NativeMethods.KeyPress)
    {
        // HandleKeyPress(...)
    }
    continue;
}

Thread.Sleep(10);
```

(`LinuxHotkeyService.cs:81-110`)

There is no signal that distinguishes "no key was pressed yet" from "this compositor will never
deliver keys for our grabs." Nothing in the contract lets a backend report that distinction:
`IHotkeyService` exposes `RegisterHotkey`, `IsRegistered`, `IsSuspended`, `NotifyWindowReady`,
and the `HotkeysChanged` event — but no delivery-health probe
(`IHotkeyService.cs:31-88`).

### Issue E — How status reaches (and fails to reach) the UI today

The status *is* plumbed to the UI. `HotkeyItemViewModel.Status` exposes
`Model.HotkeyInfo.Status` and refreshes it via `OnPropertyChanged`
(`src/desktop/app/XerahS.UI/ViewModels/HotkeyItemViewModel.cs:45-68`), and
`HotkeyStatusColorConverter` maps each status to a brush:

```csharp
return status switch
{
    HotkeyStatus.Registered => new SolidColorBrush(Colors.LimeGreen),
    HotkeyStatus.Failed => new SolidColorBrush(Colors.Red),
    HotkeyStatus.NotConfigured => new SolidColorBrush(Colors.Orange),
    HotkeyStatus.Recording => new SolidColorBrush(Colors.Yellow),
    _ => new SolidColorBrush(Colors.Gray)
};
```

(`HotkeyConverters.cs:40-54`)

But the enum it switches on has only five members and none of them means "registered but
non-functional":

```csharp
public enum HotkeyStatus
{
    NotConfigured,
    Registered,
    Failed,
    UnsupportedPlatform,
    Recording  // User is currently editing this hotkey
}
```

(`HotkeyInfo.cs:174-181`)

And the only escalation path, `WorkflowManager`, looks only for `Failed` and only writes to
`Debug`:

```csharp
public List<WorkflowSettings> GetFailedHotkeys()
{
    return Workflows.Where(h => h.HotkeyInfo.Status == HotkeyStatus.Failed).ToList();
}

private void ShowFailedHotkeys()
{
    var failed = GetFailedHotkeys();
    if (failed.Count > 0)
    {
        Debug.WriteLine($"Warning: {failed.Count} hotkey(s) failed to register:");
        // ...
    }
}
```

(`WorkflowManager.cs:258-277`)

So even if we *did* mark the hotkey honestly, today the UI converter has no color for it and the
WorkflowManager would neither collect nor surface it.

---

## Implementation Guidance

Each fix is grounded in the files above and ordered so that earlier fixes unblock later ones.

### Fix 1 — Add a dedicated `HotkeyStatus` for "registered-but-non-functional" and surface it at selection time

**Status: Proposed.**

Extend the enum with a member that means "the compositor does not provide a working global-hotkey
mechanism for this backend." The current members are `NotConfigured`, `Registered`, `Failed`,
`UnsupportedPlatform`, `Recording` (`HotkeyInfo.cs:174-181`). Add one such as
`GlobalShortcutsUnavailable` (alternatively `UnsupportedOnCompositor`), distinct from both
`UnsupportedPlatform` (the whole platform has no hotkey support) and `Failed` (a real attempt that
errored):

```csharp
public enum HotkeyStatus
{
    NotConfigured,
    Registered,
    Failed,
    UnsupportedPlatform,
    Recording,             // User is currently editing this hotkey
    GlobalShortcutsUnavailable  // Wayland session without a working GlobalShortcuts portal;
                                // X11 grab would be a no-op, so we do NOT claim Registered
}
```

Then make `LinuxPlatform.Initialize` Wayland-aware. It already has `isWayland`
(`LinuxPlatform.cs:53`) and `hasGlobalShortcuts` (`LinuxPlatform.cs:72`). When
`isWayland && !hasGlobalShortcuts`, emit a clear warning and either (a) construct the X11 service
in an explicit "fallback / delivery-unverified" mode, or (b) construct a thin service that
registers hotkeys as `GlobalShortcutsUnavailable` rather than handing the user a green light. At
minimum, replace the silent ternary at `LinuxPlatform.cs:75-77` with a branch that logs the
degraded path, e.g.:

```csharp
if (isWayland && !hasGlobalShortcuts)
{
    DebugHelper.WriteLine("Linux: Wayland session detected but org.freedesktop.portal.GlobalShortcuts " +
        "is unavailable. Global hotkeys cannot be delivered by the X11 grab backend on this compositor. " +
        "Hotkeys will be marked GlobalShortcutsUnavailable. See XIP0078 / XIP0077 for guidance.");
}
```

> [!NOTE]
> Keep the existing X11 path intact for true X11 sessions (`isWayland == false`). The new branch
> only changes behavior for the Wayland-without-portal case.

### Fix 2 — Do not report `Registered` when event delivery cannot be confirmed under native Wayland

**Status: Proposed.**

`LinuxHotkeyService.RegisterHotkey` currently sets `Status = Registered` purely on grab success
(`LinuxHotkeyService.cs:179-189`), and `TryGrab` validates only `BadAccess`
(`LinuxHotkeyService.cs:261-300`, error handler `:251-259`). Two complementary options:

1. **Status downgrade (cheap, deterministic).** If the service was constructed in the
   Wayland-fallback mode flagged by Fix 1, set
   `hotkeyInfo.Status = HotkeyStatus.GlobalShortcutsUnavailable` after a successful grab instead of
   `Registered`. The grab can still be installed (harmless under XWayland), but the *reported*
   status is honest.

2. **Event-delivery health check (stronger).** Add a delivery probe to close Issue D. The event
   loop at `LinuxHotkeyService.cs:81-110` never distinguishes "idle" from "starved." A health check
   could, on first registration under a Wayland session, verify whether any `KeyPress` is ever
   observed for grabbed combos within a window, or detect the native-Wayland condition up front and
   short-circuit. Because `IHotkeyService` has no health hook today
   (`IHotkeyService.cs:31-88`), expose the result through the new status (Fix 1) rather than adding
   a bespoke API, keeping the contract stable.

Apply the same correction to the runtime fallback so Issue B is covered: in
`ActivateFallbackHotkeys` the per-hotkey stamp
`hotkey.Status = ok ? PlatformHotkeyStatus.Registered : PlatformHotkeyStatus.Failed`
(`WaylandPortalHotkeyService.cs:650-683`) must, on a native Wayland session, resolve to
`GlobalShortcutsUnavailable` rather than `Registered`. Routing both Issue A and Issue B through the
same "is X11 delivery viable here?" helper avoids divergence.

### Fix 3 — Surface the new status in the UI with actionable guidance

**Status: Proposed.**

The status already flows to the UI via `HotkeyItemViewModel.Status`
(`HotkeyItemViewModel.cs:45-68`), so once the new enum member exists the binding propagates
automatically. Two changes are still required:

- Add a converter case so the new status renders distinctly (e.g. amber/orange to read as "warning,
  not error") in `HotkeyStatusColorConverter` (`HotkeyConverters.cs:40-54`), which currently falls
  through to gray for unknown values.
- Escalate it to the user. `WorkflowManager.GetFailedHotkeys`/`ShowFailedHotkeys`
  (`WorkflowManager.cs:258-277`) only collect `Failed` and only write to `Debug`. Extend the filter
  to also include `GlobalShortcutsUnavailable` and route the message to a user-facing surface (a
  notification or the hotkey settings panel) with concrete next steps: explain that the current
  Wayland compositor does not expose the GlobalShortcuts portal and link the COSMIC custom-shortcut
  walkthrough in **XIP0077**.

> [!WARNING]
> Reusing the red `Failed` color for `GlobalShortcutsUnavailable` would be misleading — the
> registration did not *fail*; the platform cannot deliver it. Use a separate visual treatment so
> users understand the cause is the compositor, not their key choice or a conflict.

### Fix 4 — (Optional) Re-probe `GlobalShortcuts` at runtime so XerahS adopts the portal if it appears later

**Status: Proposed (optional).**

`PortalInterfaceChecker.HasInterface` caches its result for the process lifetime via
`Cache.GetOrAdd` (`src/platform/XerahS.Platform.Linux/Services/PortalInterfaceChecker.cs:36-51`):

```csharp
public static bool HasInterface(string interfaceName)
{
    // ...
    return Cache.GetOrAdd(interfaceName, _ => CheckInterface(interfaceName));
}
```

This means the GlobalShortcuts decision is made exactly once. If a compositor gains the portal
during the session (e.g. the user installs `xdg-desktop-portal-cosmic`, or a transient startup
probe failed), XerahS never notices. An optional invalidation/re-probe entry point — invokable from
`WaylandPortalHotkeyService.NotifyWindowReady` (`WaylandPortalHotkeyService.cs:125-147`, which
already carries retry logic for XIP0044's startup race) — would let XerahS upgrade from the
`GlobalShortcutsUnavailable` fallback to the real portal without a restart. This is lower priority
than Fixes 1–3 because the common case (portal genuinely absent on COSMIC/wlroots) is correctly
handled by an honest status alone.

---

## Verification Steps

1. **Reproduce the false positive (before fix).** On an XWayland-capable Wayland session without the
   GlobalShortcuts portal (e.g. COSMIC, or a wlroots compositor lacking it), run XerahS, assign a
   hotkey, and confirm the UI shows it green (`Registered`) while pressing it from another window
   does nothing. Confirm the log has **no** line noting a Wayland-without-portal fallback
   (today there is none — `LinuxPlatform.cs:72-77`).
2. **Selection-time honesty (Fix 1).** With the fix, on the same session confirm the new warning log
   appears and the hotkey is marked `GlobalShortcutsUnavailable`, not `Registered`.
3. **No false green (Fix 2).** Confirm `LinuxHotkeyService.RegisterHotkey`
   (`LinuxHotkeyService.cs:179-189`) no longer yields `Registered` under native Wayland; verify the
   same for the runtime `response=2` path by forcing a portal bind failure and checking
   `ActivateFallbackHotkeys` (`WaylandPortalHotkeyService.cs:650-683`) stamps
   `GlobalShortcutsUnavailable`.
4. **UI surfacing (Fix 3).** Confirm `HotkeyStatusColorConverter` (`HotkeyConverters.cs:40-54`)
   renders the new status with a distinct (non-red, non-green) brush, and that
   `WorkflowManager.ShowFailedHotkeys` (`WorkflowManager.cs:258-277`) now reports it to a
   user-facing surface with XIP0077 guidance.
5. **No regression on real X11.** On a native X11 session (`environment.IsWayland == false`,
   `LinuxPlatform.cs:53`), confirm hotkeys still register, fire, and show `Registered` exactly as
   before.
6. **No regression on portal-capable Wayland.** On GNOME ≥ 45 / KDE Plasma ≥ 6 with the portal
   present, confirm `WaylandPortalHotkeyService` is still selected (`LinuxPlatform.cs:75-77`) and
   that XIP0044's `BindShortcuts response=0` flow is unaffected.
7. **(Optional) Runtime adoption (Fix 4).** Start XerahS without the portal, install the portal
   mid-session, and confirm a re-probe via `NotifyWindowReady`
   (`WaylandPortalHotkeyService.cs:125-147`) upgrades the fallback to the real portal without a
   restart.

---

## Open Questions

1. **Naming.** `GlobalShortcutsUnavailable` vs `UnsupportedOnCompositor` vs a generic
   `Degraded` — which best communicates "registered locally but compositor won't deliver" without
   colliding with the meaning of `UnsupportedPlatform` (`HotkeyInfo.cs:174-181`)?
2. **Should the X11 grab still be installed?** Even when we mark `GlobalShortcutsUnavailable`, is it
   worth keeping the harmless XWayland grab so hotkeys work *while a XerahS/XWayland window has
   focus*, or does that re-introduce confusion? (XIP0044 documents that XWayland grabs only deliver
   while an XWayland surface has focus.)
3. **Health-check cost.** A true event-delivery probe (Fix 2 option 2) needs a time window and a
   reference keypress; can we detect the native-Wayland condition deterministically up front (e.g.
   from `environment.IsWayland` plus session type) and skip the probe entirely?
4. **Pure native Wayland vs XWayland branch.** XIP0044 Open Question 4 notes the `wl_surface` vs
   `XID` descriptor split; the no-portal case should report `GlobalShortcutsUnavailable` in *both*
   sub-cases, but the XWayland sub-case has a (focus-bound) grab while the native sub-case reports
   `UnsupportedPlatform` today — should both converge on the new status?
5. **COSMIC trajectory.** If/when `xdg-desktop-portal-cosmic` ships GlobalShortcuts, Fix 4's
   re-probe becomes the preferred path; until then XIP0077's manual custom-shortcut steps are the
   user-facing answer. Where should that guidance live canonically — in-app, XIP0077, or both?

---

## References

- `src/platform/XerahS.Platform.Linux/LinuxPlatform.cs:34-117` — `Initialize`; platform selection,
  `isWayland` at `:53`, hotkey-service ternary at `:72-77`.
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:61-79` — constructor where the
  X11 display is opened.
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:81-110` — `EventLoop`, no
  delivery health check.
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:179-189` — `RegisterHotkey`
  sets `Registered` on grab success.
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:251-259` — X error handler
  checking only `BadAccess`.
- `src/platform/XerahS.Platform.Linux/Services/LinuxHotkeyService.cs:261-300` — `TryGrab`,
  `BadAccess`-only validation.
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:125-147` —
  `NotifyWindowReady` runtime retry logic (re-probe attach point for Fix 4).
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:201-212` —
  `RegisterHotkey` calling `ShouldUseFallbackHotkeys`.
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:340-343` —
  `response=2` runtime fallback trigger.
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:362-434` —
  `RebindShortcutsAsync`, session persistence and fallback activation.
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:645-648` —
  `ShouldUseFallbackHotkeys` flag.
- `src/platform/XerahS.Platform.Linux/Services/WaylandPortalHotkeyService.cs:650-683` —
  `ActivateFallbackHotkeys`, status stamped from grab success only.
- `src/platform/XerahS.Platform.Linux/Services/PortalInterfaceChecker.cs:36-51` — one-shot
  `Cache.GetOrAdd` probe (no runtime re-probe).
- `src/platform/XerahS.Platform.Abstractions/Models/HotkeyInfo.cs:174-181` — `HotkeyStatus` enum
  (five members, no non-functional state).
- `src/platform/XerahS.Platform.Abstractions/Services/IHotkeyService.cs:31-88` — contract with no
  delivery-health hook; `NotifyWindowReady` default method.
- `src/desktop/core/XerahS.Core/Hotkeys/WorkflowManager.cs:88-100` — `UpdateHotkeys` with
  `showFailedHotkeys` parameter.
- `src/desktop/core/XerahS.Core/Hotkeys/WorkflowManager.cs:258-277` — `GetFailedHotkeys` /
  `ShowFailedHotkeys` (filters `Failed`, logs to `Debug` only).
- `src/desktop/app/XerahS.UI/ViewModels/HotkeyConverters.cs:40-54` — `HotkeyStatusColorConverter`
  (no case for a non-functional status).
- `src/desktop/app/XerahS.UI/ViewModels/HotkeyItemViewModel.cs:45-68` — `Status` binding exposed to
  the UI.
- Related XIPs: XIP0029, XIP0044, XIP0046, XIP0061, XIP0075, XIP0077.

---

## Changelog

| Date | Author | Description |
|---|---|---|
| 2026-06-08 | ElmoBlatch | Initial proposal — Wayland-without-portal silent X11 fallback (Issue A `LinuxPlatform.cs:72-77`; Issue B `WaylandPortalHotkeyService.cs:340-343`/`:650-683`) and false-positive `Registered` (Issue C `LinuxHotkeyService.cs:179-189`); proposes the `GlobalShortcutsUnavailable` status, honest selection-time/runtime reporting, UI surfacing, and optional runtime portal re-probe (Open). |
