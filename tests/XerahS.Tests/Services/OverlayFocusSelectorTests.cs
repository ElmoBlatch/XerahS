using NUnit.Framework;
using XerahS.RegionCapture.Models;
using XerahS.RegionCapture.Services;

namespace XerahS.Tests.Services;

[TestFixture]
public class OverlayFocusSelectorTests
{
    // Builds a monitor at a desktop rectangle. ScaleFactor 1.0 means OverlayBounds == PhysicalBounds,
    // matching the originally reported COSMIC rig where the bug appeared.
    private static MonitorInfo Monitor(double x, double y, double w, double h, bool isPrimary)
        => new(
            DeviceName: $"M@{x}",
            PhysicalBounds: new PixelRect(x, y, w, h),
            WorkArea: new PixelRect(x, y, w, h),
            ScaleFactor: 1.0,
            IsPrimary: isPrimary);

    // Post-resume COSMIC layout: 2560 panel at x=0, ultrawide at x=2560, NO monitor flagged primary.
    private static IReadOnlyList<MonitorInfo> NoPrimary()
        => new[]
        {
            Monitor(0, 0, 2560, 1440, isPrimary: false),    // index 0: 27" (left)
            Monitor(2560, 0, 3440, 1440, isPrimary: false), // index 1: ultrawide (right)
        };

    [Test]
    public void EmptyMonitors_ReturnsMinusOne()
    {
        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(System.Array.Empty<MonitorInfo>(), null),
            Is.EqualTo(-1));
    }

    [Test]
    public void CursorOnLeftMonitor_NoPrimary_SelectsLeftMonitor()
    {
        // (1295,241) is the selection origin from the reported COSMIC log — on the 27" panel.
        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(NoPrimary(), new PixelPoint(1295, 241)),
            Is.EqualTo(0));
    }

    [Test]
    public void CursorOnRightMonitor_NoPrimary_SelectsRightMonitor()
    {
        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(NoPrimary(), new PixelPoint(4000, 700)),
            Is.EqualTo(1));
    }

    [Test]
    public void Cursor_Wins_OverPrimaryFlag()
    {
        var monitors = new[]
        {
            Monitor(0, 0, 2560, 1440, isPrimary: true),     // primary on the left
            Monitor(2560, 0, 3440, 1440, isPrimary: false),
        };

        // Cursor sits on the non-primary right monitor; cursor must win.
        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(monitors, new PixelPoint(4000, 700)),
            Is.EqualTo(1));
    }

    [Test]
    public void CursorUnknown_NoPrimary_SelectsLeftmost_RegardlessOfEnumerationOrder()
    {
        // Enumeration order swapped (the COSMIC resume symptom): the right monitor is index 0,
        // the leftmost (x=0) is index 1. Fallback must pick the geometric leftmost, not index 0.
        var monitors = new[]
        {
            Monitor(2560, 0, 3440, 1440, isPrimary: false), // index 0: right
            Monitor(0, 0, 2560, 1440, isPrimary: false),    // index 1: left (leftmost)
        };

        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(monitors, null), Is.EqualTo(1));
    }

    [Test]
    public void CursorUnknown_PrimaryPresent_SelectsPrimary()
    {
        var monitors = new[]
        {
            Monitor(0, 0, 2560, 1440, isPrimary: false),
            Monitor(2560, 0, 3440, 1440, isPrimary: true),
        };

        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(monitors, null), Is.EqualTo(1));
    }

    [Test]
    public void CursorOffEveryMonitor_FallsBackToPrimary()
    {
        var monitors = new[]
        {
            Monitor(0, 0, 2560, 1440, isPrimary: false),
            Monitor(2560, 0, 3440, 1440, isPrimary: true),
        };

        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(monitors, new PixelPoint(99999, 99999)),
            Is.EqualTo(1));
    }

    [Test]
    public void SingleMonitor_NoPrimary_NoCursor_SelectsTheOnlyMonitor()
    {
        var monitors = new[] { Monitor(0, 0, 2560, 1440, isPrimary: false) };

        Assert.That(OverlayFocusSelector.SelectInitialFocusIndex(monitors, null), Is.EqualTo(0));
    }
}
