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

namespace XerahS.RegionCapture.Services;

/// <summary>
/// Decides when a pointer event on an overlay represents real user input rather than the synthetic
/// pointer-move Avalonia raises as each overlay window opens. On COSMIC both overlays receive such a
/// synthetic event within ~1 ms of mapping, so "first pointer event claims focus" hands the active
/// window to whichever overlay opened last — the wrong-monitor regression in XIP0081. Only motion
/// beyond a threshold from the first observed position, or a button press, may claim focus.
/// </summary>
internal sealed class PointerFocusClaimGate
{
    private const double DefaultMovementThresholdPx = 4.0;

    private readonly double _movementThresholdPx;
    private double _baselineX;
    private double _baselineY;
    private bool _hasBaseline;
    private bool _claimed;

    public PointerFocusClaimGate(double movementThresholdPx = DefaultMovementThresholdPx)
    {
        _movementThresholdPx = movementThresholdPx;
    }

    /// <summary>
    /// Observes a pointer-move position (window-local coordinates). Returns true exactly once: when
    /// the position has moved beyond the threshold from the first observed position. Synthetic
    /// open-time events repeat the same coordinates, so they only ever establish the baseline.
    /// </summary>
    public bool ObserveMove(double x, double y)
    {
        if (_claimed)
            return false;

        if (!_hasBaseline)
        {
            _baselineX = x;
            _baselineY = y;
            _hasBaseline = true;
            return false;
        }

        double dx = x - _baselineX;
        double dy = y - _baselineY;
        if ((dx * dx) + (dy * dy) <= _movementThresholdPx * _movementThresholdPx)
            return false;

        _claimed = true;
        return true;
    }

    /// <summary>
    /// Observes a pointer press. A button press is always real user input, never synthetic, so it
    /// claims immediately. Returns true exactly once across both observe methods.
    /// </summary>
    public bool ObservePress()
    {
        if (_claimed)
            return false;

        _claimed = true;
        return true;
    }
}
