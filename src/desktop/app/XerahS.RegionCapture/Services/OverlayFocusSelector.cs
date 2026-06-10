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

namespace XerahS.RegionCapture.Services;

/// <summary>
/// Decides which monitor's overlay should receive initial focus when a region capture starts.
/// Targeting the monitor under the cursor keeps capture correct even when the compositor reorders
/// displays or drops the primary-monitor flag after resume/login (e.g. COSMIC), which previously
/// left no overlay focused. See XIP0081.
/// </summary>
internal static class OverlayFocusSelector
{
    /// <summary>
    /// Returns the index of the monitor whose overlay should be focused first.
    /// Priority: the monitor containing <paramref name="preferredFocusPoint"/> (the cursor), then the
    /// primary monitor, then the leftmost (topmost as tie-break) monitor. The leftmost fallback is
    /// chosen by geometry so it stays stable even when the enumeration order changes between sessions.
    /// Returns -1 only when <paramref name="monitors"/> is empty.
    /// </summary>
    /// <param name="monitors">The enumerated monitors, in overlay order.</param>
    /// <param name="preferredFocusPoint">
    /// The cursor position in logical desktop coordinates, matched against each monitor's
    /// <see cref="MonitorInfo.OverlayBounds"/> (the real desktop layout). Null means "unknown" — a
    /// failed cursor read must arrive here as null, never as (0,0).
    /// </param>
    public static int SelectInitialFocusIndex(
        IReadOnlyList<MonitorInfo> monitors,
        PixelPoint? preferredFocusPoint)
    {
        if (monitors is null || monitors.Count == 0)
            return -1;

        // 1) The monitor under the cursor — matched against the real desktop layout (OverlayBounds),
        //    not the synthetic stitched PhysicalBounds, so mixed-DPI layouts resolve correctly.
        if (preferredFocusPoint is { } point)
        {
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].OverlayBounds.Contains(point))
                    return i;
            }
        }

        // 2) The primary monitor, if any monitor still reports it.
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
                return i;
        }

        // 3) The leftmost (then topmost) monitor — geometric, so it is independent of enumeration order.
        int leftmostIndex = 0;
        PixelRect leftmost = monitors[0].OverlayBounds;
        for (int i = 1; i < monitors.Count; i++)
        {
            PixelRect bounds = monitors[i].OverlayBounds;
            if (bounds.X < leftmost.X || (bounds.X == leftmost.X && bounds.Y < leftmost.Y))
            {
                leftmost = bounds;
                leftmostIndex = i;
            }
        }

        return leftmostIndex;
    }
}
