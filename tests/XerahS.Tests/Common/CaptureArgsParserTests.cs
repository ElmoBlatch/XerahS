using System;
using NUnit.Framework;
using XerahS.Common;

namespace XerahS.Tests.Common;

[TestFixture]
public class CaptureArgsParserTests
{
    [Test]
    public void TryParse_CaptureWithWorkflowId_ReturnsTrueAndId()
    {
        bool ok = CaptureArgsParser.TryParse(new[] { "capture", "--workflow-id", "abc123" }, out string? id);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(id, Is.EqualTo("abc123"));
        });
    }

    [Test]
    public void TryParse_CaptureWithoutWorkflowId_IsCaptureButNullId()
    {
        bool ok = CaptureArgsParser.TryParse(new[] { "capture" }, out string? id);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(id, Is.Null);
        });
    }

    [Test]
    public void TryParse_FilePath_IsNotCapture()
    {
        Assert.That(CaptureArgsParser.TryParse(new[] { "/home/user/shot.png" }, out string? id), Is.False);
        Assert.That(id, Is.Null);
    }

    [Test]
    public void TryParse_SendToFlag_IsNotCapture()
    {
        Assert.That(CaptureArgsParser.TryParse(new[] { "--send-to", "/home/user/shot.png" }, out _), Is.False);
    }

    [Test]
    public void TryParse_VerbOnlyMatchesLeadingToken()
    {
        // "capture" as a non-leading token (e.g. a forwarded file path or flag value) must not be
        // misread as a capture invocation — the verb is a command and only valid as args[0].
        Assert.Multiple(() =>
        {
            Assert.That(CaptureArgsParser.TryParse(new[] { "/home/user/capture", "--workflow-id", "x" }, out _), Is.False);
            Assert.That(CaptureArgsParser.TryParse(new[] { "--send-to", "capture" }, out _), Is.False);
        });
    }

    [Test]
    public void TryParse_Empty_IsNotCapture()
    {
        Assert.That(CaptureArgsParser.TryParse(Array.Empty<string>(), out _), Is.False);
        Assert.That(CaptureArgsParser.TryParse(null, out _), Is.False);
    }

    [Test]
    public void IsVerb_DetectsForwardedActionVerbs_OnlyAsLeadingToken()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CaptureArgsParser.IsVerb(new[] { "assistant" }, AppContracts.Cli.AssistantVerb), Is.True);
            Assert.That(CaptureArgsParser.IsVerb(new[] { "command-palette" }, AppContracts.Cli.CommandPaletteVerb), Is.True);
            // wrong verb / capture args don't match
            Assert.That(CaptureArgsParser.IsVerb(new[] { "capture", "--workflow-id", "x" }, AppContracts.Cli.AssistantVerb), Is.False);
            Assert.That(CaptureArgsParser.IsVerb(new[] { "assistant" }, AppContracts.Cli.CommandPaletteVerb), Is.False);
            // the verb only counts as the leading token — a later argument that equals it must not match
            Assert.That(CaptureArgsParser.IsVerb(new[] { "--send-to", "assistant" }, AppContracts.Cli.AssistantVerb), Is.False);
            Assert.That(CaptureArgsParser.IsVerb(new[] { "/home/user/assistant", "extra" }, AppContracts.Cli.AssistantVerb), Is.False);
            Assert.That(CaptureArgsParser.IsVerb(Array.Empty<string>(), AppContracts.Cli.AssistantVerb), Is.False);
            Assert.That(CaptureArgsParser.IsVerb(null, AppContracts.Cli.AssistantVerb), Is.False);
        });
    }
}
