using NUnit.Framework;
using XerahS.Platform.Linux;

namespace XerahS.Tests.Platform.Linux;

[TestFixture]
public class LinuxOcrServiceTests
{
    [TestCase("en", "eng")]
    [TestCase("eng", "eng")]
    [TestCase("EN", "eng")]
    [TestCase("en-US", "eng")]
    [TestCase("de", "deu")]
    [TestCase("zh", "chi_sim")]
    [TestCase("chi_sim", "chi_sim")]
    [TestCase("", "eng")]
    [TestCase(null, "eng")]
    public void MapLanguageToTesseractCode_MapsCommonTags(string? input, string expected)
    {
        Assert.That(LinuxOcrService.MapLanguageToTesseractCode(input), Is.EqualTo(expected));
    }

    // Chinese disambiguation: Tesseract has separate chi_sim / chi_tra packs, and the Simplified vs
    // Traditional distinction lives in the BCP-47 script/region subtag, not the "zh" primary tag.
    [TestCase("zh", "chi_sim")]
    [TestCase("zh-Hans", "chi_sim")]
    [TestCase("zh-CN", "chi_sim")]
    [TestCase("zh-SG", "chi_sim")]
    [TestCase("zh-Hant", "chi_tra")]
    [TestCase("zh-TW", "chi_tra")]
    [TestCase("zh_TW", "chi_tra")]
    [TestCase("zh-HK", "chi_tra")]
    [TestCase("zh-MO", "chi_tra")]
    [TestCase("zh-Hant-TW", "chi_tra")]
    [TestCase("chi_tra", "chi_tra")]
    public void MapLanguageToTesseractCode_DisambiguatesChineseScript(string input, string expected)
    {
        Assert.That(LinuxOcrService.MapLanguageToTesseractCode(input), Is.EqualTo(expected));
    }

    [Test]
    public void BuildTesseractArguments_DefaultAndSingleLine()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LinuxOcrService.BuildTesseractArguments("/tmp/x.png", "eng", singleLine: false),
                Is.EqualTo(new[] { "/tmp/x.png", "stdout", "-l", "eng" }));
            Assert.That(LinuxOcrService.BuildTesseractArguments("/tmp/x.png", "eng", singleLine: true),
                Is.EqualTo(new[] { "/tmp/x.png", "stdout", "-l", "eng", "--psm", "7" }));
        });
    }

    [Test]
    public void ParseListLangs_ExtractsCodes_SkipsHeaderAndOsd()
    {
        // Real `tesseract --list-langs` output shape.
        string output = "List of available languages in \"/usr/share/tessdata/\" (2):\neng\nosd\n";
        Assert.That(LinuxOcrService.ParseListLangs(output), Is.EqualTo(new[] { "eng" }));
    }

    [Test]
    public void ParseListLangs_Empty_ReturnsEmpty()
    {
        Assert.That(LinuxOcrService.ParseListLangs(string.Empty), Is.Empty);
    }

    [Test]
    public void MapCodeToLanguage_UsesFriendlyDisplayName()
    {
        var lang = LinuxOcrService.MapCodeToLanguage("eng");
        Assert.Multiple(() =>
        {
            Assert.That(lang.LanguageTag, Is.EqualTo("eng"));
            Assert.That(lang.DisplayName, Is.EqualTo("English"));
        });
    }

    [Test]
    public void MapCodeToLanguage_UnknownCode_FallsBackToCode()
    {
        var lang = LinuxOcrService.MapCodeToLanguage("xyz");
        Assert.That(lang.DisplayName, Is.EqualTo("xyz"));
    }
}
