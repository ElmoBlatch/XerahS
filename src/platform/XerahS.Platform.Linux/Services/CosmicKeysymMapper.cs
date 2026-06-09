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

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;

namespace XerahS.Platform.Linux.Services;

/// <summary>
/// Maps Avalonia <see cref="Key"/>/<see cref="KeyModifiers"/> to COSMIC shortcut RON tokens.
/// COSMIC uses xkb keysym names for the key (letters lowercase) and the Modifier enum tokens
/// Ctrl/Alt/Shift/Super — NOT GTK accelerator names. Reuses the xkb keysym table from
/// <see cref="LinuxHotkeyService"/> so the two stay aligned. See XIP0079.
/// </summary>
internal static class CosmicKeysymMapper
{
    /// <summary>Returns the xkb keysym name for a key (e.g. "Print", "space", "a", "4"), or null if unmappable.</summary>
    public static string? MapKey(Key key)
    {
        if (key == Key.None) return null;

        // Only emit keysyms cosmic-comp's xkb name lookup will recognize. The X11 backend tolerates
        // unknown names (XStringToKeysym returns 0 and the grab simply fails), but the COSMIC writer
        // would persist a dead binding (e.g. key: "MediaNextTrack") and falsely report Registered, so
        // reject anything outside the known-keysym categories and let TryMap fail honestly. See XIP0079.
        if (!LinuxHotkeyService.HasKnownKeysymName(key)) return null;

        var candidates = LinuxHotkeyService.GetCandidateKeysymNames(key);
        string? name = candidates.Count > 0 ? candidates[0] : null;
        if (string.IsNullOrEmpty(name)) return null;

        // COSMIC stores single-letter keysyms lowercase (Keysym::from_char(lowercased)).
        if (name!.Length == 1 && name[0] >= 'A' && name[0] <= 'Z')
        {
            name = char.ToLowerInvariant(name[0]).ToString();
        }
        return name;
    }

    /// <summary>Translates Avalonia modifiers to COSMIC Modifier enum tokens (Super, Ctrl, Alt, Shift).</summary>
    public static IReadOnlyList<string> MapModifiers(KeyModifiers modifiers)
    {
        var list = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Meta)) list.Add("Super");
        if (modifiers.HasFlag(KeyModifiers.Control)) list.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) list.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) list.Add("Shift");
        return list;
    }

    /// <summary>Builds a <see cref="CosmicBinding"/> for the hotkey, or returns false if the key cannot be mapped.</summary>
    public static bool TryMap(Key key, KeyModifiers modifiers, [NotNullWhen(true)] out CosmicBinding? binding)
    {
        binding = null;
        string? keysym = MapKey(key);
        if (string.IsNullOrEmpty(keysym)) return false;

        binding = new CosmicBinding(MapModifiers(modifiers), keysym!);
        return true;
    }
}
