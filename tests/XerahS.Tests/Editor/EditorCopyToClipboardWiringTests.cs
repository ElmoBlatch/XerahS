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

using NUnit.Framework;
using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Presentation.ViewModels;
using XerahS.UI.Services;

namespace XerahS.Tests.Editor;

[TestFixture]
public class EditorCopyToClipboardWiringTests
{
    /// <summary>
    /// When the host wires its own copy handler, it must also mark the view model so
    /// EditorView's fallback copy handler is suppressed. Otherwise the fallback overwrites
    /// the host's clipboard write with an unrooted DataTransfer whose bitmap can be
    /// garbage-collected before a paste target requests it (X11 serves clipboard lazily),
    /// making "Copy to clipboard" silently produce nothing.
    /// </summary>
    [Test]
    public void WireCopyRequested_MarksHostCopyHandler_SoEditorViewFallbackIsSuppressed()
    {
        var viewModel = new MainViewModel(new ImageEditorOptions());

        MainViewModelHelper.WireCopyRequested(viewModel);

        Assert.That(viewModel.HasHostCopyHandler, Is.True);
    }
}
