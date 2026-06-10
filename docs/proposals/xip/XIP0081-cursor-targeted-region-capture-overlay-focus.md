# XIP0081 Cursor-Targeted Region Capture Overlay Focus

**Status**: Draft
**Created**: 2026-06-10
**Updated**: 2026-06-10
**Area**: Linux, RegionCapture, UI
**Goal**: Make the region-capture overlay take initial focus on the monitor under the cursor instead of the OS-designated primary monitor, so capture targets where the user is working regardless of how the compositor (re)assigns the primary display.
**Related**: XIP0079 (COSMIC compositor-shortcut hotkeys), XIP0080 (COSMIC native Wayland screencopy/toplevel), XIP0058 (Wayland window preselection parity / coordinate mapping), XIP0047 & XIP0051 (region capture)

> **Note**: This is the local backup (`docs/proposals/xip/`). It has **not** been synced to a GitHub issue (the XIP source of truth) per the repo rule against creating issues without an explicit request. Sync via the `sync-xips` skill when ready.

---

## Overview

On COSMIC, region capture intermittently behaves as if it "targets a blank desktop" after resume-from-sleep or login: the user must hunt across monitors before they can select the window they want. Root cause is a system bug bleeding into the app. COSMIC periodically reorders outputs and reassigns (or drops) the XRandR primary flag on resume/login. XerahS does **not** decide which monitor is primary — `MonitorEnumerationService` copies `IsPrimary` verbatim from Avalonia's `screen.IsPrimary` (`MonitorEnumerationService.cs:121`), which on COSMIC's X11/XWayland backend (`isAvaloniaWayland=False`) is exactly the XRandR primary flag that COSMIC scrambles. There is no in-app override.

That inherited primary flag is load-bearing in exactly one place in the capture path: `OverlayManager.ShowOverlaysAsync` scans monitors for `IsPrimary==true` and `Show()/Activate()/Focus()`es that overlay first to give the compositor one clear focus target (`OverlayManager.cs:91-110`). When **no** monitor reports `IsPrimary` (the post-flip state — all `IsPrimary=False`), `primaryIndex` stays `-1`, nothing is proactively focused, and focus falls to whichever overlay was shown last — frequently the empty secondary monitor. The crop itself is unaffected: `WaylandMonitorLayoutNormalizer` rebuilds physical layout deterministically from logical-X order (`WaylandMonitorLayoutNormalizer.cs:49-93`), so completed captures still produce correct images — only *targeting/focus* is broken.

The fix is to focus the overlay on the **monitor under the cursor** and treat primary as a fallback only. This dissolves the bug by construction (the primary flag stops mattering for region capture) and mirrors an idiom already shipping in this codebase: `CaptureCommandPaletteCoordinator.cs:174` and `AssistantOverlayCoordinator.cs:135` already position their overlays with `GetScreenFromPoint(GetCursorPosition())`. Region capture is the one path still keyed off `IsPrimary`.

## Prerequisites

- A readable cursor position. On the affected sessions `InputCapture` is absent, so `LinuxPlatform.cs:126-128` registers `LinuxInputService`, whose `GetCursorPosition()` shells `xdotool getmouselocation` and returns **global X11 pointer coordinates** — the same coordinate space as Avalonia's screens. No new dependency is introduced; `xdotool` is the existing mechanism behind `GetActiveScreenBounds()`/`GetScreenFromPoint()`.
- No new packages, no new platform protocol code.

## Implementation Phases

### Phase 1 — Carry a preferred focus point into the overlay layer

The caller already on the UI thread (`OverlayRegionCaptureSession`) owns `PlatformServices` access and builds `RegionCaptureOptions`, which already flows unchanged to `OverlayManager.ShowOverlaysAsync(options)`. Add a nullable focus hint to that options object instead of threading a new parameter through the public `RegionCaptureService.CaptureRegionAsync` signature.

**Key files:**
- `src/desktop/app/XerahS.RegionCapture/RegionCaptureOptions.cs` — add `PixelPoint? PreferredFocusPoint`.
- `src/desktop/app/XerahS.UI/Services/Capture/OverlayRegionCaptureSession.cs` — resolve the cursor and set the hint.

**Behavior:**
- Read the cursor once at capture start via `PlatformServices.Input.GetCursorPosition()`.
- If the read **succeeds** (a real point), set `PreferredFocusPoint`.
- If it **fails** (see Rules — `Point.Empty`/exception), leave `PreferredFocusPoint = null`. Never coerce a failed read into `(0,0)`.

**Rules:**
- `GetCursorPosition()` returns `Point.Empty` (`0,0`) on failure (e.g. `xdotool` missing). `(0,0)` is a *valid* coordinate, so a failed read must be detected as "unknown" and dropped — not passed down as a real point.
- The caller does not pick the overlay; it only supplies the point. Monitor matching lives in one place (Phase 2).

### Phase 2 — Focus the cursor's monitor, with a fallback chain

Replace the `IsPrimary`-only scan in `OverlayManager.ShowOverlaysAsync` with an ordered selection of the overlay to focus first:

1. **Cursor's monitor** — the overlay whose monitor contains `options.PreferredFocusPoint`.
2. **Primary** — first monitor with `IsPrimary==true` (if any still report it).
3. **First/leftmost overlay** — guarantees something is always focused, fixing today's "nothing focused" state even when both cursor and primary are unknown.

**Key files:**
- `src/desktop/app/XerahS.RegionCapture/Services/OverlayManager.cs` — selection + focus logic.

**Rules:**
- Match the point against each monitor's **`OverlayBounds`** (the real desktop layout in logical coordinates) — **not** `PhysicalBounds`, which is the normalizer's synthetic left-to-right stitched-bitmap space and does not describe the actual desktop arrangement under mixed-DPI. Use containment of the rectangle, not raw per-axis arithmetic. The originally reported rig is scale `1.0`, where X11 physical == logical and `OverlayBounds` == `PhysicalBounds`, so matching is exact there; specifying `OverlayBounds` keeps it correct on mixed-DPI too.
- `PreferredFocusPoint` is therefore defined in **logical desktop coordinates**. The caller resolves the raw cursor through the same `GetScreenFromPoint`/`GetActiveScreenBounds` path the command palette uses so DPI conversion is handled consistently in one place.
- Keep the "show focused overlay first, then the rest" ordering — it is what gives the compositor a single clear focus target on Wayland.

### Phase 3 — Regression tests

The selection in Phase 2 is a pure function of `(monitors, preferredFocusPoint)` and needs no live display. Add coverage the suite currently lacks (the existing `WaylandMonitorLayoutNormalizerTests` only cover single-primary, stable-order layouts).

**Key files:**
- `tests/XerahS.Tests/...` — new tests.

**Cases:**
- No monitor reports `IsPrimary` + cursor on each monitor → that monitor is chosen.
- Swapped enumeration order → choice follows the cursor, not the index.
- Cursor unknown (`null`) + no primary → first/leftmost chosen (never none).
- Cursor unknown + primary present → primary chosen.
- Single monitor → the only overlay chosen.

## Non-Negotiable Rules

1. **`XerahS.RegionCapture` stays platform-agnostic.** It must not call `PlatformServices.Input`/`Screen`. The cursor is resolved by the UI caller and passed in via `RegionCaptureOptions`.
2. **Primary becomes a fallback, not a requirement.** Never assume a primary monitor exists; never leave focus undetermined.
3. **A failed cursor read is "unknown," never `(0,0)`.** Do not silently target the origin monitor on failure.
4. **Do not change the crop/coordinate math.** This XIP only changes which overlay is focused first; `CropFromPreCapture`, `WaylandMonitorLayoutNormalizer`, and per-monitor background extraction are untouched.
5. **Reuse the existing cursor→screen idiom** (`GetCursorPosition` / `GetScreenFromPoint` / `GetActiveScreenBounds`); do not introduce a second cursor-reading path.
6. **No new dependency** and no native Wayland protocol code (that is XIP0080's scope).

## Deliverables

1. `PixelPoint? PreferredFocusPoint` on `RegionCaptureOptions`.
2. Cursor resolution in `OverlayRegionCaptureSession` with explicit unknown-handling.
3. Cursor → primary → first/leftmost focus fallback chain in `OverlayManager`.
4. Pure-function regression tests for the selection logic.
5. This XIP as documentation of the behavior change.

## Affected Components

- `XerahS.RegionCapture`: `RegionCaptureOptions` (new field), `OverlayManager` (focus selection).
- `XerahS.UI`: `OverlayRegionCaptureSession` (cursor resolution).
- `XerahS.Tests`: new selection regression tests.

## Out of Scope (follow-ups)

- `LinuxScreenService.GetPrimaryScreenBounds()`/`GetPrimaryScreenWorkingArea()` use the same `FirstOrDefault(IsPrimary)` pattern (`LinuxScreenService.cs:78-80`) and return `Rectangle.Empty` when no monitor is primary, which would degrade **full-screen "primary monitor"** capture and main-window placement after a flip. Hardening those with an equivalent leftmost/cursor fallback is a separate, optional change — intentionally not bundled here.
- An in-app "pin my primary monitor" override (would require a stable hardware monitor identity; the current `DeviceName` is index-based `"Display {i+1}"`). Cursor targeting makes this unnecessary for region capture.
- The underlying COSMIC monitor-flip is a system-side issue (resume/login output reassignment); it is mitigated, not fixed, by this app-side change.

## Architecture Summary

```
Hotkey -> capture verb -> WorkflowOrchestrator -> ScreenCaptureService
        |
OverlayRegionCaptureSession (UI thread, has PlatformServices)
        |  reads PlatformServices.Input.GetCursorPosition()  (xdotool, X11 global coords)
        |  success -> RegionCaptureOptions.PreferredFocusPoint = point
        |  failure -> PreferredFocusPoint = null   (NOT (0,0))
        v
RegionCaptureService.CaptureRegionAsync -> OverlayManager.ShowOverlaysAsync(options)
        |
   select focus overlay:
        cursor's monitor  ->  primary (if any)  ->  first/leftmost      <-- NEW
        |
   Show()/Activate()/Focus() that overlay first, then the rest
```

## Evolution History

| Date | Change | Rationale |
|------|--------|-----------|
| 2026-06-10 | Initial implementation: `OverlayFocusSelector` picks the cursor's monitor; `PreferredFocusPoint` plumbed via `RegionCaptureOptions`. | Cursor-target the overlay; primary becomes a fallback. |
| 2026-06-10 | Loop fix + pointer-enter refinement, after focus-theft was traced. | The selector picked the right overlay, but `OverlayManager`'s remaining-overlays loop then called `Activate()` on each, handing the active window to the **last** overlay shown — a ~16-23 ms programmatic focus theft seen via an `xprop -spy _NET_ACTIVE_WINDOW` trace. Fix: `ShowActivated="False"` on `OverlayWindow`, drop the loop's `Activate()`, and re-assert the cursor overlay's activation **last**. Refinement: each overlay claims the active window on its first pointer event (`PointerEntered`/`PointerMoved`), making targeting cursor-perfect even if the pre-capture `xdotool` read was stale — the post-map pointer position is always live. (Confirmed on this COSMIC session that `xdotool getmouselocation` does return real coordinates; the pointer-enter claim is the compositor-agnostic guarantee on top of that.) |

## Watch-items

- `OverlayWindow.OnOpened` calls `this.Focus()` + `ScheduleDelayedFocusRetries()` on **every** overlay. These use `Focus()` (input focus), not `Activate()`, and the trace showed `_NET_ACTIVE_WINDOW` flipping only on `Activate()` calls — so they were not the active-window theft. If a future trace shows residual focus drift, gate these per-overlay retries to the owning overlay (e.g. a shared focus arbiter that the pointer-enter claim can hand off).

## Open Questions

- DPI: resolved in Phase 2 — `PreferredFocusPoint` is in logical desktop coordinates and matched against `OverlayBounds`. The only remaining implementation detail is confirming the caller's physical→logical conversion reuses `GetScreenFromPoint` cleanly; the affected rig is scale `1.0` where the distinction is moot.
- Should the same cursor-first focus policy be applied on Windows/macOS for consistency, or scoped to Linux where the bug manifests? (Lean: make the selection logic cross-platform since it is pure; it is simply a no-op improvement where a primary is always reliably reported.)
