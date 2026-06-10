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

namespace XerahS.Common;

/// <summary>
/// Parses the capture verb forwarded to the running instance (e.g. spawned by a COSMIC custom
/// shortcut: <c>XerahS capture --workflow-id &lt;id&gt;</c>). See XIP0079.
/// </summary>
public static class CaptureArgsParser
{
    /// <summary>
    /// Returns true when <paramref name="args"/> represent a capture invocation, with the optional
    /// workflow id from <see cref="AppContracts.Cli.WorkflowIdOption"/>.
    /// </summary>
    public static bool TryParse(string[]? args, out string? workflowId)
    {
        workflowId = null;
        if (args == null || args.Length == 0)
        {
            return false;
        }

        // The verb is a command, so it must be the leading token — not matched anywhere in the array.
        // Otherwise an ordinary argument that happens to equal "capture" (e.g. a forwarded file path or
        // a flag value) would be misread as a capture invocation.
        if (!string.Equals(args[0], AppContracts.Cli.CaptureVerb, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], AppContracts.Cli.WorkflowIdOption, StringComparison.OrdinalIgnoreCase))
            {
                workflowId = args[i + 1];
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="args"/> invoke the given action <paramref name="verb"/> as the
    /// leading token (e.g. the forwarded "assistant" or "command-palette" verb from a COSMIC shortcut).
    /// Anchored to <c>args[0]</c> so a later argument that merely equals the verb does not match. XIP0079.
    /// </summary>
    public static bool IsVerb(string[]? args, string verb)
    {
        if (args == null || args.Length == 0 || string.IsNullOrEmpty(verb))
        {
            return false;
        }

        return string.Equals(args[0], verb, StringComparison.OrdinalIgnoreCase);
    }
}
