using System;
using System.IO;
using Avalonia.Input;
using NUnit.Framework;
using XerahS.Platform.Abstractions;
using XerahS.Platform.Linux.Services;

namespace XerahS.Tests.Platform.Linux;

[TestFixture]
public class CosmicHotkeyServiceTests
{
    private string _dir = null!;
    private string _file = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xerahs-cosmicsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "custom");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private CosmicHotkeyService NewService() =>
        new(new CosmicShortcutConfigWriter(_file), () => "/usr/bin/XerahS");

    [Test]
    public void RegisterHotkey_WritesSpawnWithWorkflowId_AndReportsRegistered()
    {
        var service = NewService();
        var info = new HotkeyInfo(Key.PrintScreen, KeyModifiers.None) { CommandIdentifier = "abc123" };

        bool ok = service.RegisterHotkey(info);
        string ron = File.ReadAllText(_file);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(info.Status, Is.EqualTo(HotkeyStatus.Registered));
            Assert.That(ron, Does.Contain("key: \"Print\""));
            Assert.That(ron, Does.Contain("Spawn(\"'/usr/bin/XerahS' capture --workflow-id abc123\")"));
            Assert.That(service.IsRegistered(info), Is.True);
        });
    }

    [Test]
    public void RegisterHotkey_WithModifiers_EmitsCosmicModifierTokens()
    {
        var service = NewService();
        var info = new HotkeyInfo(Key.S, KeyModifiers.Meta | KeyModifiers.Shift) { CommandIdentifier = "wf" };

        service.RegisterHotkey(info);
        string ron = File.ReadAllText(_file);

        Assert.Multiple(() =>
        {
            Assert.That(ron, Does.Contain("modifiers: [Super, Shift]"));
            Assert.That(ron, Does.Contain("key: \"s\""));
        });
    }

    [Test]
    public void UnregisterHotkey_RemovesEntry()
    {
        var service = NewService();
        var info = new HotkeyInfo(Key.PrintScreen, KeyModifiers.None) { CommandIdentifier = "abc" };
        service.RegisterHotkey(info);
        Assert.That(service.IsRegistered(info), Is.True);

        service.UnregisterHotkey(info);

        Assert.That(service.IsRegistered(new HotkeyInfo(Key.PrintScreen, KeyModifiers.None)), Is.False);
    }

    [Test]
    public void RegisterHotkey_InvalidKey_FailsWithoutWriting()
    {
        var service = NewService();
        var info = new HotkeyInfo(Key.None, KeyModifiers.Control);

        Assert.Multiple(() =>
        {
            Assert.That(service.RegisterHotkey(info), Is.False);
            Assert.That(info.Status, Is.EqualTo(HotkeyStatus.NotConfigured));
            Assert.That(File.Exists(_file), Is.False);
        });
    }

    [Test]
    public void UnregisterAll_ClearsXerahsEntries_PreservesForeign()
    {
        File.WriteAllText(_file, "{ (modifiers: [Super], key: \"q\"): Spawn(\"user-thing\"), }");
        var service = NewService();
        service.RegisterHotkey(new HotkeyInfo(Key.PrintScreen, KeyModifiers.None) { CommandIdentifier = "a" });

        service.UnregisterAll();

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(ron, Does.Contain("user-thing"));
            Assert.That(ron, Does.Not.Contain("XerahS"));
        });
    }

    [Test]
    public void RegisterHotkey_ShellQuotesProcessPathContainingSpaces()
    {
        // cosmic-comp runs Spawn(String) via `/bin/sh -c`, so an install path with spaces must be
        // shell-quoted or it is word-split and the capture launch fails. See XIP0079.
        var service = new CosmicHotkeyService(
            new CosmicShortcutConfigWriter(_file), () => "/home/a b/My Apps/XerahS");
        var info = new HotkeyInfo(Key.PrintScreen, KeyModifiers.None) { CommandIdentifier = "id1" };

        service.RegisterHotkey(info);
        string ron = File.ReadAllText(_file);

        Assert.That(ron, Does.Contain("Spawn(\"'/home/a b/My Apps/XerahS' capture --workflow-id id1\")"));
    }

    [Test]
    public void RegisterHotkey_UnmappableKey_FailsWithoutWriting()
    {
        // A real but non-xkb key (e.g. media key) must report Failed honestly, not write a dead
        // binding while claiming Registered. See XIP0079.
        var service = NewService();
        var info = new HotkeyInfo(Key.MediaNextTrack, KeyModifiers.Control);

        Assert.Multiple(() =>
        {
            Assert.That(service.RegisterHotkey(info), Is.False);
            Assert.That(info.Status, Is.EqualTo(HotkeyStatus.Failed));
            Assert.That(File.Exists(_file), Is.False);
        });
    }
}
