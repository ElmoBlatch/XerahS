# XIP0087 COSMIC Native Wayland Screencopy and Toplevel Enumeration

**Status**: Draft (Idea / Deferred — not scheduled)
**Created**: 2026-06-09
**Updated**: 2026-06-09
**Area**: Linux, Wayland, RegionCapture, Architecture
**Goal**: Capture the design for talking the relevant Wayland protocols in-process on COSMIC (and other native-Wayland sessions) — `screencopy` for grim-free capture and `foreign-toplevel`/`cosmic-toplevel-info` for window preselection — so the two known COSMIC gaps can be closed later without re-discovering the analysis.
**Related**: XIP0016 (capture architecture), XIP0058 (Wayland window preselection parity), XIP0079 (COSMIC compositor-shortcut hotkeys), XIP0047/XIP0051 (region capture)

> **Note**: This is a deferred *idea* note, written as the local backup (`docs/proposals/xip/`). It has **not** been synced to a GitHub issue (the XIP source of truth) and is **not** scheduled. It exists so the prompted "should we use AvaloniaUI/NWayland / native Wayland protocols?" analysis is not lost.

---

## Overview

XerahS has **zero in-process Wayland protocol code** today. Every Wayland touchpoint is either a CLI shell-out (`grim`, `slurp`, `hyprctl`, `swaymsg`, `kdotool`) or a D-Bus call (XDG portals, GNOME Shell, KDE). That is portable and works, but it leaves two concrete gaps on **COSMIC**, which — unlike the wlroots desktops — ships no drop-in CLI analog for these tasks:

1. **Capture depends on `grim` being on `PATH`.** On COSMIC we deliberately bypass the interactive `cosmic-screenshot` XDG portal and use `grim` instead, but only when `grim` is detected (XIP0079; `ShouldBlockCosmicScreenshotPortal` gated on `grim`-on-`PATH` + non-sandboxed). If `grim` is missing, capture falls back to the interactive portal, which pops `cosmic-screenshot` and stalls silent/overlay grabs. The whole gate is a workaround for not speaking the capture protocol ourselves.

2. **Window preselection is `Unsupported` on COSMIC.** XIP0058 gave hover/click window snapping on GNOME/KDE/Hyprland/Sway via per-compositor helpers, but there is no COSMIC helper, so COSMIC users can only snap X11/XWayland windows.

Both gaps share a root cause and a clean fix: speak the **native Wayland protocols in-process** instead of shelling out. COSMIC exposes the protocols needed for both — we just don't consume them.

This note records the design and explicitly evaluates **AvaloniaUI/NWayland** as the C# binding layer, since the question prompted this proposal.

## Why this is a note, not a task

The natural C# substrate would be **NWayland** (the binding layer Avalonia's own experimental Wayland backend is built on). As of 2026-06 it is **WIP, unreleased on NuGet, and ships no `screencopy`, `foreign-toplevel`, or COSMIC bindings**; its `Wlr` protocol set does not apply because **COSMIC is not wlroots**. So this is a real architectural upgrade but **not turnkey** — adopting it means generating the missing protocol bindings ourselves and/or taking a dependency on an unreleased library. Hence: record the design, build later if/when prioritized.

## Target protocols (feature-detected via `wl_registry`)

| Concern | Protocol(s) | Notes |
|---|---|---|
| Full-output / region capture | `ext-image-copy-capture-v1` + `ext-image-capture-source-v1` (standardized successor); fall back to `cosmic-screencopy-unstable-v1`; then `wlr-screencopy-unstable-v1` | COSMIC is migrating toward the `ext-*` set; `grim` uses `wlr-screencopy`. Capture to an SHM (or DMA-BUF) buffer → `SKBitmap`. |
| Toplevel identity (app-id/title) | `ext-foreign-toplevel-list-v1` | **Standard, but identity only — no geometry / no hit-testing.** Sufficient for naming, *not* for "window under cursor". |
| Toplevel geometry for snapping | `cosmic-toplevel-info-unstable-v1` (and/or compositor-specific geometry) | Needed for hover/click snapping at a point; this is the harder half and the main open question. |

Region selection (the `slurp` role) still uses the **existing XerahS overlay** — no protocol replaces it.

## Approaches

1. **Status quo** — keep shell-outs + portal fallback; COSMIC window snapping stays `Unsupported`; capture stays `grim`-gated. Smallest effort, fails the actual gaps.
2. **Minimal in-process protocol client for COSMIC**, slotted behind the *existing* abstractions (the capture provider pipeline and `ILogicalWindowPointQueryService`), leaving every other desktop on its current path. Feature-detected; always falls back to the current shell-out/portal path when the protocol is absent. **(Recommended if prioritized.)**
3. **Adopt NWayland wholesale** as the C# Wayland base and contribute the missing protocols upstream. Largest scope, hard dependency on a WIP library; only worth it if XerahS wants a broad, maintained Wayland stack.

## Implementation sketch (Approach 2)

### Phase 1 — Native screencopy capture provider
Add a Wayland-protocol capture provider in the existing `WaylandProtocol` stage that `CanHandle` a COSMIC (or any) session advertising a supported screencopy protocol, ranked **ahead of** the `grim` CLI provider. It connects to `$WAYLAND_DISPLAY`, binds the output/source, copies a frame into an SHM buffer, and decodes to `SKBitmap`. If the protocol is not advertised, it declines and the pipeline falls through to `grim`/portal exactly as today.

**Key files**: new `Capture/Providers/WaylandProtocol/*`, wired through `LinuxCaptureCoordinator` + `WaterfallCapturePolicy`; reuse `LinuxCaptureStage`.

### Phase 2 — COSMIC toplevel point-query helper
Implement `ILogicalWindowPointQueryService` for COSMIC using `cosmic-toplevel-info` (+ `ext-foreign-toplevel-list` for identity), returning the topmost toplevel rectangle at a logical point. Reuse the XIP0058 physical↔logical coordinate mapping and the shared overlay-exclusion identifier. Closes the COSMIC window-preselection gap.

**Key files**: new `Wayland/WindowQuery/CosmicToplevelWindowPointQueryHelper.cs` + registration in `WaylandWindowPointQueryHelperFactory`.

### Phase 3 (optional) — retire the `grim` gate on COSMIC
Once native capture is proven, the `grim`-on-`PATH` gate in `ShouldBlockCosmicScreenshotPortal` can be simplified (native provider first, `grim` as fallback, portal last). Keep `grim` as a fallback — do not remove it.

## Non-Negotiable Rules

- Sit **behind existing abstractions** (capture provider + `ILogicalWindowPointQueryService`). No parallel capture pipeline, no second window-query stack.
- **Feature-detect every protocol** via the registry; never assume presence. Always fall back to the current shell-out/portal path when absent.
- **No hard dependency on the unreleased NWayland NuGet.** If NWayland's scanner is used as tooling, vendor the generated bindings or pin a commit; otherwise a small hand-rolled binding over `libwayland-client` P/Invoke is acceptable.
- Do not regress X11/XWayland/GNOME/KDE/Hyprland/Sway capture or snapping.
- Handle **multi-output, fractional scaling, and buffer formats** (SHM vs DMA-BUF); convert to physical capture pixels consistently (reuse XIP0058 mapping).
- **Sandbox**: in Flatpak the Wayland socket exists but compositor-global capture may still require the portal — keep the portal as the sandbox capture path; do not assume protocol capture works confined.

## Deliverables

1. A native Wayland screencopy capture provider (feature-detected, fallback-safe).
2. A COSMIC toplevel point-query helper implementing `ILogicalWindowPointQueryService`.
3. A small, vendored/pinned Wayland protocol binding layer (or justified NWayland usage).
4. Tests for buffer→`SKBitmap` decode and logical/physical rectangle conversion (mirroring XIP0058's parser/conversion tests).
5. Simplification of the COSMIC `grim` gate (Phase 3, optional).

## Affected Components

- `XerahS.Platform.Linux/Capture/**` — new protocol provider, coordinator/policy wiring, `LinuxScreenCaptureService` gate simplification.
- `XerahS.Platform.Linux/Wayland/WindowQuery/**` — COSMIC helper + factory registration.
- `XerahS.Platform.Abstractions` — `ILogicalWindowPointQueryService` (consumer only; likely unchanged).

## Architecture Summary

```
RegionCapture overlay (selection, unchanged)
        │
LinuxCaptureCoordinator ──> WaterfallCapturePolicy (stage order)
        │
   WaylandProtocol stage
        ├── NEW: native screencopy provider   ← ext-image-copy-capture / cosmic-screencopy / wlr-screencopy
        └── existing grim/slurp CLI provider   (fallback)
        │
   Portal stage (cosmic-screenshot)            (last resort / sandbox)

Window preselection:
   ILogicalWindowPointQueryService
        └── NEW: CosmicToplevelWindowPointQueryHelper  ← cosmic-toplevel-info (+ ext-foreign-toplevel-list)
```

## Open Questions

- Which screencopy protocol does the shipped COSMIC actually advertise today (`cosmic-screencopy` vs the `ext-image-copy-capture` migration), and does `grim` still rely on `wlr-screencopy` compat there?
- `ext-foreign-toplevel-list-v1` gives identity but **no geometry** — confirm `cosmic-toplevel-info` exposes per-toplevel geometry usable for point hit-testing, or whether snapping needs another source.
- Binding strategy: NWayland scanner (vendored output) vs hand-rolled `libwayland-client` P/Invoke vs a managed protocol client. NWayland is WIP/unreleased and lacks these protocols, so it is tooling at best, not a dependency.
- DMA-BUF vs SHM buffer handling and color/format conversion to `SKBitmap`.
