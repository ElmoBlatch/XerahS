using NUnit.Framework;
using XerahS.Platform.Linux;

namespace XerahS.Tests.Platform.Linux;

[TestFixture]
public class LinuxHotkeyBackendSelectionTests
{
    [Test]
    public void Portal_WhenGlobalShortcutsAvailable()
    {
        Assert.That(LinuxPlatform.SelectHotkeyBackend(isWayland: true, hasGlobalShortcuts: true, desktop: "COSMIC"),
            Is.EqualTo(LinuxHotkeyBackend.Portal));
    }

    [Test]
    public void Cosmic_WhenWaylandWithoutPortalOnCosmic()
    {
        Assert.That(LinuxPlatform.SelectHotkeyBackend(isWayland: true, hasGlobalShortcuts: false, desktop: "COSMIC"),
            Is.EqualTo(LinuxHotkeyBackend.Cosmic));
    }

    [Test]
    public void X11Unavailable_WhenWaylandWithoutPortalOnNonCosmic()
    {
        Assert.That(LinuxPlatform.SelectHotkeyBackend(isWayland: true, hasGlobalShortcuts: false, desktop: "GNOME"),
            Is.EqualTo(LinuxHotkeyBackend.X11Unavailable));
    }

    [Test]
    public void X11_WhenNotWayland()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LinuxPlatform.SelectHotkeyBackend(isWayland: false, hasGlobalShortcuts: false, desktop: null),
                Is.EqualTo(LinuxHotkeyBackend.X11));
            // COSMIC token on a (hypothetical) X11 session still uses the working X11 grab.
            Assert.That(LinuxPlatform.SelectHotkeyBackend(isWayland: false, hasGlobalShortcuts: false, desktop: "COSMIC"),
                Is.EqualTo(LinuxHotkeyBackend.X11));
        });
    }
}
