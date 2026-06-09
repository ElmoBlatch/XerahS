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

using Avalonia.Controls.ApplicationLifetimes;

namespace XerahS.UI.Services;

public interface IWorkflowOrchestrator
{
    Core.Hotkeys.WorkflowManager? WorkflowManager { get; }
    void Start(IClassicDesktopStyleApplicationLifetime desktop, string baseTitle);

    /// <summary>
    /// Resolve a workflow by its stable id and run it through the same path as a hotkey trigger.
    /// Used by the COSMIC compositor-shortcut dispatch (XIP0079).
    /// </summary>
    System.Threading.Tasks.Task TriggerWorkflowByIdAsync(string workflowId);

    /// <summary>Open the AI Assistant overlay. Used by the COSMIC compositor-shortcut dispatch (XIP0079).</summary>
    void ShowAssistant();

    /// <summary>Toggle the Capture Command Palette. Used by the COSMIC compositor-shortcut dispatch (XIP0079).</summary>
    void ToggleCommandPalette();
}
