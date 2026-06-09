using NUnit.Framework;
using XerahS.Platform.Linux.Capture.Wayland;

namespace XerahS.Tests.Platform.Linux;

[TestFixture]
public class WaylandCliCaptureTests
{
    // grim / ext-image-copy-capture-capable Wayland desktops route through the grim+slurp
    // CLI fallback for region and active-window capture. COSMIC supports this path, so it must
    // be treated like the other wlroots-style desktops (regression guard: once COSMIC is a
    // *named* desktop it no longer falls through the historical `desktop == null` branch).
    [TestCase("HYPRLAND", true)]
    [TestCase("SWAY", true)]
    [TestCase("COSMIC", true)]
    [TestCase("GNOME", false)]
    [TestCase("KDE", false)]
    public void IsWlrootsDesktop_TreatsGrimCapableWaylandDesktopsAsWlroots(string desktop, bool expected)
    {
        Assert.That(WaylandCliCapture.IsWlrootsDesktop(desktop), Is.EqualTo(expected));
    }
}
