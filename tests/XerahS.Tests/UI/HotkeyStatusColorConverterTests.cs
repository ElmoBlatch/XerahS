using System.Globalization;
using Avalonia.Media;
using NUnit.Framework;
using XerahS.Platform.Abstractions;
using XerahS.UI.ViewModels;

namespace XerahS.Tests.UI;

[TestFixture]
public class HotkeyStatusColorConverterTests
{
    private static Color ColorFor(HotkeyStatus status)
    {
        var result = HotkeyStatusColorConverter.Instance.Convert(
            status, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.InstanceOf<SolidColorBrush>());
        return ((SolidColorBrush)result!).Color;
    }

    [Test]
    public void Convert_GlobalShortcutsUnavailable_IsDistinctWarningColour()
    {
        var unavailable = ColorFor(HotkeyStatus.GlobalShortcutsUnavailable);

        Assert.Multiple(() =>
        {
            // Must not read as success (green), a hard failure (red), or an unknown fall-through (gray).
            Assert.That(unavailable, Is.Not.EqualTo(Colors.LimeGreen));
            Assert.That(unavailable, Is.Not.EqualTo(Colors.Red));
            Assert.That(unavailable, Is.Not.EqualTo(Colors.Gray));
            Assert.That(unavailable, Is.EqualTo(Colors.Goldenrod));
        });
    }
}
