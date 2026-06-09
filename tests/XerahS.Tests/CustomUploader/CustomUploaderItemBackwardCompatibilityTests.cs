using NUnit.Framework;
using XerahS.Uploaders;

namespace XerahS.Tests.CustomUploader;

[TestFixture]
public class CustomUploaderItemBackwardCompatibilityTests
{
    [Test]
    public void CheckBackwardCompatibility_MigratesLegacyDollarSyntax_EvenWhenXerahSVersionStamped()
    {
        // Regression: a service-exported .sxcu using the legacy ShareX $func:arg$ syntax, imported and
        // re-saved by XerahS, keeps the legacy syntax but gets a 0.x version stamp. The version gate
        // skipped migration, so $json:url$ was passed through verbatim (copied to the clipboard as the
        // literal "$json:url$" instead of the parsed URL).
        var item = CustomUploaderItem.Init();
        item.Version = "0.22.255";
        item.RequestURL = "https://h0udini.com/api/upload";
        item.URL = "$json:url$";
        item.DeletionURL = "$json:delete_url$";
        item.ErrorMessage = "$json:message$";

        item.CheckBackwardCompatibility();

        Assert.Multiple(() =>
        {
            Assert.That(item.URL, Is.EqualTo("{json:url}"));
            Assert.That(item.DeletionURL, Is.EqualTo("{json:delete_url}"));
            Assert.That(item.ErrorMessage, Is.EqualTo("{json:message}"));
        });
    }

    [Test]
    public void CheckBackwardCompatibility_LeavesModernSyntaxUntouched()
    {
        // Modern {func:arg} fields must never be re-migrated — MigrateOldSyntax would escape the braces.
        var item = CustomUploaderItem.Init();
        item.Version = "0.22.255";
        item.RequestURL = "https://h0udini.com/api/upload";
        item.URL = "{json:url}";
        item.DeletionURL = "{json:files[0].delete}";

        item.CheckBackwardCompatibility();

        Assert.Multiple(() =>
        {
            Assert.That(item.URL, Is.EqualTo("{json:url}"));
            Assert.That(item.DeletionURL, Is.EqualTo("{json:files[0].delete}"));
        });
    }

    [Test]
    public void CheckBackwardCompatibility_StillMigratesLegacyShareXVersionedFile()
    {
        // The existing path for genuinely old ShareX files (<= 13.7.1) keeps working.
        var item = CustomUploaderItem.Init();
        item.Version = "13.7.0";
        item.RequestURL = "https://example.com/api";
        item.URL = "$json:url$";

        item.CheckBackwardCompatibility();

        Assert.That(item.URL, Is.EqualTo("{json:url}"));
    }
}
