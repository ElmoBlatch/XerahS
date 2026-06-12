#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)
using XerahS.RegionCapture.Models;
using XerahS.RegionCapture;
using XerahS.RegionCapture.UI;
using XerahS.Common;
using XerahS.RegionCapture.ViewModels;

namespace XerahS.RegionCapture.Services;

/// <summary>
/// Manages the lifecycle and coordination of per-monitor overlay windows.
/// This implements the "Per-Monitor Overlay" pattern (Strategy B) to bypass
/// mixed-DPI scaling artifacts common in single-span windows.
/// </summary>
public sealed class OverlayManager : IDisposable
{
    private readonly List<OverlayWindow> _overlays = [];
    private readonly TaskCompletionSource<RegionSelectionResult?> _completionSource;
    private readonly CoordinateTranslationService _coordinateService;
    private readonly RegionCaptureAnnotationToolCoordinator _annotationToolCoordinator;
    private bool _disposed;

    public OverlayManager()
    {
        _completionSource = new TaskCompletionSource<RegionSelectionResult?>();
        _coordinateService = new CoordinateTranslationService();
        _annotationToolCoordinator = new RegionCaptureAnnotationToolCoordinator();
    }

    /// <summary>
    /// Gets all active overlay windows.
    /// </summary>
    public IReadOnlyList<OverlayWindow> Overlays => _overlays;

    /// <summary>
    /// Gets the coordinate translation service for cross-monitor calculations.
    /// </summary>
    public CoordinateTranslationService CoordinateService => _coordinateService;

    /// <summary>
    /// Creates and shows overlay windows for all monitors.
    /// </summary>
    /// <summary>
    /// Creates and shows overlay windows for all monitors.
    /// </summary>
    public async Task<RegionSelectionResult?> ShowOverlaysAsync(
        Action<PixelRect>? onSelectionChanged = null,
        XerahS.Platform.Abstractions.CursorInfo? initialCursor = null,
        RegionCaptureOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var monitors = _coordinateService.Monitors;

        if (monitors.Count == 0)
            return null;

        try
        {
            // Create one overlay per monitor
            foreach (var monitor in monitors)
            {
                var overlay = new OverlayWindow(monitor, _completionSource, onSelectionChanged, initialCursor, options, _annotationToolCoordinator);
                _overlays.Add(overlay);
            }

            // Choose which overlay to focus first: the monitor under the cursor, else the primary, else
            // the leftmost. Focusing the cursor's monitor keeps region-capture targeting correct even when
            // the compositor reorders displays or drops the primary flag after resume/login, and still
            // gives the compositor one clear focus target sooner on Wayland (XIP0081).
            int focusIndex = OverlayFocusSelector.SelectInitialFocusIndex(monitors, options?.PreferredFocusPoint);

            var focusOverlay = focusIndex >= 0 && focusIndex < _overlays.Count
                ? _overlays[focusIndex]
                : null;

            string cursorText = options?.PreferredFocusPoint is { } cursorPoint
                ? $"({cursorPoint.X},{cursorPoint.Y})"
                : "unknown";
            DebugHelper.WriteLine(
                $"[OverlayFocus] Initial focus -> index {focusIndex} ({(focusIndex >= 0 && focusIndex < monitors.Count ? monitors[focusIndex].DeviceName : "none")}), cursor={cursorText}");

            // Show the focus overlay first and focus it immediately so the compositor has one clear focus target (reduces pointer-event delay on Wayland)
            if (focusOverlay != null)
            {
                focusOverlay.IsInitialFocusTarget = true;
                focusOverlay.Show();
                focusOverlay.Activate();
                focusOverlay.Focus();
                var focusHandle = focusOverlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                WindowDetectionService.ExcludeHandle(focusHandle);
            }

            // Show the remaining overlays WITHOUT activating them. With ShowActivated=false they map
            // unfocused, so Show() cannot steal focus; calling Activate() here would hand the active
            // window to the LAST overlay shown — the ~16-23 ms programmatic focus theft seen in tracing —
            // instead of the cursor's overlay (XIP0081).
            foreach (var overlay in _overlays)
            {
                if (overlay == focusOverlay)
                    continue;
                overlay.Show();
                var handle = overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                WindowDetectionService.ExcludeHandle(handle);
            }

            // Re-assert focus on the cursor's overlay so it is the LAST activation request the
            // compositor sees, guaranteeing the selected monitor keeps the active window.
            if (focusOverlay != null)
            {
                focusOverlay.Activate();
                focusOverlay.Focus();
            }

            // The pre-capture cursor read (xdotool) goes stale under Wayland whenever the pointer sits
            // over a native surface, so the initial pick can be wrong. Once the XWayland overlays cover
            // every monitor the pointer position reads live again — probe it after mapping settles and
            // transfer focus if it disagrees with the initial pick (XIP0081).
            SchedulePostMapFocusCorrection(monitors, focusIndex, options);

            if (options?.SessionStartUtc is { } start)
            {
                double elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
                DebugHelper.WriteLine($"[RegionCapture] Milestone: overlay displayed (+{elapsedMs:F0} ms)");
            }

            // Wait for result
            return await _completionSource.Task;
        }
        finally
        {
            CloseAllOverlays();
        }
    }

    private static readonly int[] PostMapCursorProbeDelaysMs = [250, 700];

    private async void SchedulePostMapFocusCorrection(
        IReadOnlyList<MonitorInfo> monitors,
        int initialFocusIndex,
        RegionCaptureOptions? options)
    {
        var cursorProvider = options?.CursorPointProvider;
        if (cursorProvider is null)
            return;

        foreach (int delayMs in PostMapCursorProbeDelaysMs)
        {
            await Task.Delay(delayMs);

            if (_disposed)
                return;

            PixelPoint? probe = null;
            try
            {
                probe = cursorProvider();
            }
            catch
            {
                // Cursor probe is best-effort; the initial pick and real-pointer claims still apply.
            }

            if (probe is not { } point)
                continue;

            int liveIndex = -1;
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].OverlayBounds.Contains(point))
                {
                    liveIndex = i;
                    break;
                }
            }

            DebugHelper.WriteLine(
                $"[OverlayFocus] Post-map cursor probe: ({point.X},{point.Y}) -> index {liveIndex} (initial {initialFocusIndex})");

            if (liveIndex < 0 || liveIndex >= _overlays.Count)
                continue;

            // Always re-assert the live cursor's overlay, even when it matches the initial pick: a
            // transitional synthetic pointer claim may have moved the active window in the meantime,
            // so agreeing with the initial index is not proof the right overlay is focused.
            int claimIndex = liveIndex;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                    return;

                _overlays[claimIndex].ClaimActiveWindow("post-map cursor probe");
            }, Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    private void CloseAllOverlays()
    {
        foreach (var overlay in _overlays)
        {
            var handle = overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WindowDetectionService.RemoveExcludedHandle(handle);

            try
            {
                overlay.Close();
            }
            catch
            {
                // Ignore close errors
            }
        }

        _overlays.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CloseAllOverlays();
        _completionSource.TrySetCanceled();
    }
}
