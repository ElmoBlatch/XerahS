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

        int verbIndex = Array.FindIndex(args,
            a => string.Equals(a, AppContracts.Cli.CaptureVerb, StringComparison.OrdinalIgnoreCase));
        if (verbIndex < 0)
        {
            return false;
        }

        for (int i = verbIndex + 1; i < args.Length - 1; i++)
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
    /// Returns true when <paramref name="args"/> contain the given action <paramref name="verb"/> as a
    /// token (e.g. the forwarded "assistant" or "command-palette" verb from a COSMIC shortcut). XIP0079.
    /// </summary>
    public static bool ContainsVerb(string[]? args, string verb)
    {
        if (args == null || args.Length == 0 || string.IsNullOrEmpty(verb))
        {
            return false;
        }

        return Array.FindIndex(args,
            a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase)) >= 0;
    }
}
