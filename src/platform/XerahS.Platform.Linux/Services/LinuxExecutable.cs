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

using System;
using System.IO;

namespace XerahS.Platform.Linux.Services;

/// <summary>
/// Locates external CLI executables on the user's <c>PATH</c> (grim, slurp, tesseract, …), matching the
/// way the rest of the Linux backend shells out to system tools rather than bundling them.
/// </summary>
internal static class LinuxExecutable
{
    /// <summary>Returns the absolute path of <paramref name="executable"/> on PATH, or null if not found.</summary>
    public static string? ResolveOnPath(string executable)
    {
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="executable"/> is found on PATH.</summary>
    public static bool IsAvailable(string executable) => ResolveOnPath(executable) != null;
}
