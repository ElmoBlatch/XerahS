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
using System.Linq;
using System.Threading.Tasks;
using XerahS.Common;
using XerahS.Platform.Abstractions;
using HotkeyStatus = XerahS.Platform.Abstractions.HotkeyStatus;

namespace XerahS.Platform.Linux.Services;

/// <summary>
/// <see cref="IHotkeyService"/> for COSMIC sessions (no GlobalShortcuts portal). Instead of an
/// X11 grab — which cannot deliver global hotkeys on COSMIC — it writes the binding into the
/// COSMIC compositor's custom-shortcut config via <see cref="CosmicShortcutConfigWriter"/>, so
/// cosmic-comp itself spawns XerahS with a capture verb when the key is pressed. The actual
/// trigger arrives as a forwarded launch argument (single-instance), not via
/// <see cref="HotkeyTriggered"/>. See XIP0079.
/// </summary>
internal sealed class CosmicHotkeyService : IHotkeyService
{
    private readonly CosmicShortcutConfigWriter _writer;
    private readonly Func<string?> _processPathProvider;
    private ushort _nextId = 1;

    public CosmicHotkeyService(CosmicShortcutConfigWriter? writer = null, Func<string?>? processPathProvider = null)
    {
        _writer = writer ?? new CosmicShortcutConfigWriter();
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
    }

    // The compositor delivers the hotkey by spawning XerahS, so this backend never raises these.
    public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered { add { } remove { } }
    public event EventHandler? HotkeysChanged { add { } remove { } }

    public bool IsSuspended { get; set; }

    public bool RegisterHotkey(HotkeyInfo hotkeyInfo)
    {
        if (!hotkeyInfo.IsValid)
        {
            hotkeyInfo.Status = HotkeyStatus.NotConfigured;
            return false;
        }

        if (!CosmicKeysymMapper.TryMap(hotkeyInfo.Key, hotkeyInfo.Modifiers, out var binding))
        {
            hotkeyInfo.Status = HotkeyStatus.Failed;
            DebugHelper.WriteLine($"CosmicHotkeyService: no COSMIC keysym mapping for {hotkeyInfo}; cannot write shortcut.");
            return false;
        }

        try
        {
            _writer.Upsert(binding, BuildSpawnCommand(hotkeyInfo));
        }
        catch (Exception ex)
        {
            hotkeyInfo.Status = HotkeyStatus.Failed;
            DebugHelper.WriteException(ex, "CosmicHotkeyService: failed to write COSMIC shortcut config");
            return false;
        }

        if (hotkeyInfo.Id == 0)
        {
            hotkeyInfo.Id = _nextId++;
        }

        // cosmic-comp live-reloads the file and will deliver this binding, so it genuinely works.
        hotkeyInfo.Status = HotkeyStatus.Registered;
        hotkeyInfo.NativeTriggerDescription = string.Join("+", binding.OrderedModifiers().Append(binding.Key));
        return true;
    }

    public bool UnregisterHotkey(HotkeyInfo hotkeyInfo)
    {
        if (CosmicKeysymMapper.TryMap(hotkeyInfo.Key, hotkeyInfo.Modifiers, out var binding))
        {
            try
            {
                _writer.Remove(binding);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "CosmicHotkeyService: failed to remove COSMIC shortcut");
            }
        }

        hotkeyInfo.Id = 0;
        hotkeyInfo.Status = HotkeyStatus.NotConfigured;
        hotkeyInfo.NativeTriggerDescription = null;
        return true;
    }

    public void UnregisterAll()
    {
        try
        {
            _writer.RemoveAllXerahsOwned();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "CosmicHotkeyService: failed to clear COSMIC shortcuts");
        }
    }

    public bool IsRegistered(HotkeyInfo hotkeyInfo)
    {
        return CosmicKeysymMapper.TryMap(hotkeyInfo.Key, hotkeyInfo.Modifiers, out var binding)
            && _writer.ContainsXerahsBinding(binding);
    }

    public Task<bool> ShowInteractiveConfigurationAsync() => Task.FromResult(false);

    private string BuildSpawnCommand(HotkeyInfo hotkeyInfo)
    {
        string quotedPath = ShellQuote(_processPathProvider() ?? "XerahS");

        // App-action hotkeys (Assistant, Capture Command Palette) spawn their own verb; everything else
        // is a capture workflow re-resolved by its id on the next process start. See XIP0079.
        if (!string.IsNullOrEmpty(hotkeyInfo.CommandVerb) &&
            !string.Equals(hotkeyInfo.CommandVerb, AppContracts.Cli.CaptureVerb, StringComparison.Ordinal))
        {
            return $"{quotedPath} {hotkeyInfo.CommandVerb}";
        }

        string command = $"{quotedPath} {AppContracts.Cli.CaptureVerb}";
        if (!string.IsNullOrEmpty(hotkeyInfo.CommandIdentifier))
        {
            command += $" {AppContracts.Cli.WorkflowIdOption} {hotkeyInfo.CommandIdentifier}";
        }
        return command;
    }

    // cosmic-comp executes Spawn(String) via `/bin/sh -c "<command>"` (cosmic-comp
    // src/input/actions.rs), so the executable path must be POSIX shell-quoted or a path containing
    // spaces/metacharacters would be word-split and the capture launch would fail. Wrap in single
    // quotes and escape any embedded single quote as '\''. The workflow id is a GUID so it is safe
    // unquoted. See XIP0079.
    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        // No background threads or unmanaged handles; the compositor owns hotkey delivery.
        GC.SuppressFinalize(this);
    }
}
