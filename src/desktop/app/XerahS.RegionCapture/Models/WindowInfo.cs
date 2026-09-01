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
namespace XerahS.RegionCapture.Models;

/// <summary>
/// Represents information about a visible window for snapping.
/// </summary>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    PixelRect Bounds,
    PixelRect VisualBounds,
    bool IsMinimized,
    int ZOrder,
    bool IsControl = false,
    bool IsClientArea = false)
{
    /// <summary>
    /// The visual bounds (excluding shadow/DWM frame) for accurate snapping.
    /// </summary>
    public PixelRect SnapBounds => VisualBounds.IsEmpty ? Bounds : VisualBounds;

    /// <summary>
    /// Title shown in the hover overlay. Child controls often have empty titles.
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
                return Title;

            if (IsClientArea)
                return "Client area";

            return string.IsNullOrWhiteSpace(ClassName) ? "Control" : ClassName;
        }
    }
}
