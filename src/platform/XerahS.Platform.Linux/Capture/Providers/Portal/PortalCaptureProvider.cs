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

using System.Threading;
using System.Threading.Tasks;
using XerahS.Platform.Linux.Capture.Contracts;
using XerahS.Platform.Linux;

namespace XerahS.Platform.Linux.Capture.Providers;

internal sealed class PortalCaptureProvider : ILinuxCaptureProvider
{
    private readonly ILinuxCaptureRuntime _runtime;

    public PortalCaptureProvider(ILinuxCaptureRuntime runtime)
    {
        _runtime = runtime;
    }

    public string ProviderId => "portal";

    public LinuxCaptureStage Stage => LinuxCaptureStage.Portal;

    public bool CanHandle(LinuxCaptureRequest request, ILinuxCaptureContext context)
    {
        if (request.Kind == LinuxCaptureKind.FullScreen &&
            LinuxScreenCaptureService.ShouldSkipPortalAfterOverlaySelection(request.Options, context))
        {
            return false;
        }

        // COSMIC's Screenshot portal is interactive (it opens cosmic-screenshot) and unreliable here,
        // so a silent full-screen grab — e.g. the background for the XerahS region overlay — must
        // prefer the wlroots/grim provider instead of popping the portal UI. Only decline when grim is
        // actually viable (Wayland, non-sandboxed, modern capture) so a full-screen request is never
        // left without a provider. See XIP0079.
        if (request.Kind == LinuxCaptureKind.FullScreen &&
            request.UseModernCapture &&
            context.IsWayland &&
            !context.IsSandboxed &&
            string.Equals(context.Desktop, "COSMIC", System.StringComparison.Ordinal))
        {
            return false;
        }

        if (context.IsSandboxed)
        {
            return context.ShouldTryPortal;
        }

        // On Wayland, the portal is the only viable capture method.
        // Always allow portal even when UseModernCapture=false,
        // since X11/CLI tools cannot work under Wayland.
        if (context.IsWayland && context.HasScreenshotPortal)
        {
            return true;
        }

        return request.UseModernCapture && context.ShouldTryPortal;
    }

    public async Task<LinuxCaptureResult> TryCaptureAsync(
        LinuxCaptureRequest request,
        ILinuxCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        var (bitmap, response) = await _runtime.TryPortalCaptureAsync(request.Kind, request.Options).ConfigureAwait(false);
        if (bitmap != null)
        {
            return LinuxCaptureResult.Success(ProviderId, bitmap);
        }

        if (response == _runtime.PortalCancelledResponseCode)
        {
            return LinuxCaptureResult.Cancelled(ProviderId);
        }

        return LinuxCaptureResult.Failure(ProviderId);
    }
}
