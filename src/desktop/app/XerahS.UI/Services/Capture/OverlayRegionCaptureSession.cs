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

using Avalonia.Threading;
using SkiaSharp;
using XerahS.Platform.Abstractions;
using XerahS.RegionCapture;
using XerahS.RegionCapture.Models;
using XerahS.RegionCapture.Services;

namespace XerahS.UI.Services.Capture;

internal static class OverlayRegionCaptureSession
{
    internal readonly record struct OverlayRegionCaptureResult(
        SKRectI Selection,
        SKBitmap? AnnotationLayer,
        PixelPoint AnnotationMonitorOrigin);

    public static async Task<SKRectI> SelectRegionAsync(
        IScreenCaptureService platformImpl,
        CaptureOptions? options,
        bool useFastOverlay)
    {
        SKRectI selection = SKRectI.Empty;
        var preferredFocusPoint = ResolvePreferredFocusPoint();

        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                CursorInfo? cursorInfo = null;
                if (options?.ShowCursor == true)
                {
                    try
                    {
                        cursorInfo = await platformImpl.CaptureCursorAsync();
                    }
                    catch
                    {
                        // Ignore cursor capture errors.
                    }
                }

                SKBitmap? backgroundForMagnifier = null;
                if (!useFastOverlay)
                {
                    try
                    {
                        backgroundForMagnifier = await platformImpl.CaptureFullScreenAsync(new CaptureOptions
                        {
                            ShowCursor = false,
                            UseModernCapture = options?.UseModernCapture ?? true,
                            LinuxRegionSelectorPreference = LinuxCaptureOptionsResolver.GetLinuxRegionSelectorPreference(options),
                            MacOSRegionSelectorPreference = options?.MacOSRegionSelectorPreference ??
                                MacOSInteractiveRegionSelectorPreference.Automatic,
                            MacOSPlayCaptureSound = false
                        });
                    }
                    catch
                    {
                        // Ignore background capture errors.
                    }
                }

                var captureService = new RegionCaptureService
                {
                    Options = new XerahS.RegionCapture.RegionCaptureOptions
                    {
                        ShowCursor = options?.ShowCursor ?? false,
                        BackgroundImage = backgroundForMagnifier,
                        UseTransparentOverlay = useFastOverlay,
                        EditorOptions = RegionCaptureAnnotationOptionsStore.GetEditorOptions(options?.WorkflowId),
                        PreferredFocusPoint = preferredFocusPoint,
                        CursorPointProvider = ResolvePreferredFocusPoint,
                    }
                };

                RegionSelectionResult? result;
                try
                {
                    result = await captureService.CaptureRegionAsync(cursorInfo);
                }
                finally
                {
                    RegionCaptureAnnotationOptionsStore.Persist();
                }

                if (result is not null)
                {
                    var region = result.Value.Region;
                    selection = new SKRectI((int)region.X, (int)region.Y, (int)region.Right, (int)region.Bottom);
                }
            });
        }
        catch
        {
            // Ignore errors to keep capture resilient.
        }

        return selection;
    }

    public static async Task<OverlayRegionCaptureResult> CaptureRegionAsync(
        CaptureOptions? effectiveOptions,
        DateTime sessionStartUtc,
        bool useFastOverlay,
        SKBitmap? fullScreenBitmap,
        CursorInfo? ghostCursor)
    {
        SKRectI selection = SKRectI.Empty;
        SKBitmap? annotationLayer = null;
        PixelPoint annotationMonitorOrigin = default;
        var preferredFocusPoint = ResolvePreferredFocusPoint();

        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var captureService = new RegionCaptureService
                {
                    Options = new XerahS.RegionCapture.RegionCaptureOptions
                    {
                        ShowCursor = effectiveOptions?.ShowCursor ?? false,
                        BackgroundImage = fullScreenBitmap,
                        UseTransparentOverlay = useFastOverlay,
                        EditorOptions = RegionCaptureAnnotationOptionsStore.GetEditorOptions(effectiveOptions?.WorkflowId),
                        SessionStartUtc = sessionStartUtc,
                        PreferredFocusPoint = preferredFocusPoint,
                        CursorPointProvider = ResolvePreferredFocusPoint,
                    }
                };

                RegionSelectionResult? result;
                try
                {
                    result = await captureService.CaptureRegionAsync(ghostCursor);
                }
                finally
                {
                    RegionCaptureAnnotationOptionsStore.Persist();
                }

                if (result is not null)
                {
                    var region = result.Value.Region;
                    selection = new SKRectI((int)region.X, (int)region.Y, (int)region.Right, (int)region.Bottom);
                    annotationLayer = result.Value.AnnotationLayer;
                    annotationMonitorOrigin = result.Value.MonitorOrigin;
                }
            });
        }
        catch
        {
            // Ignore overlay session errors to preserve existing behavior.
        }

        return new OverlayRegionCaptureResult(selection, annotationLayer, annotationMonitorOrigin);
    }

    /// <summary>
    /// Reads the current cursor position so overlay focus can target the monitor the user is working on.
    /// Returns null when the position is unavailable: a failed read surfaces as <c>Point.Empty</c> (0,0),
    /// which must be treated as "unknown" rather than the desktop origin (XIP0081).
    /// </summary>
    private static PixelPoint? ResolvePreferredFocusPoint()
    {
        try
        {
            var cursor = PlatformServices.Input.GetCursorPosition();
            if (cursor.IsEmpty)
                return null;

            return new PixelPoint(cursor.X, cursor.Y);
        }
        catch
        {
            // Cursor position is best-effort; fall back to primary/leftmost focus.
            return null;
        }
    }
}
