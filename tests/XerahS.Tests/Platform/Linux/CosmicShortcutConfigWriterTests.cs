using System;
using System.IO;
using NUnit.Framework;
using XerahS.Platform.Linux.Services;

namespace XerahS.Tests.Platform.Linux;

[TestFixture]
public class CosmicShortcutConfigWriterTests
{
    private string _dir = null!;
    private string _file = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xerahs-cosmic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "custom");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private CosmicShortcutConfigWriter NewWriter() => new(_file);

    [Test]
    public void Upsert_IntoAbsentFile_WritesSingleSpawnEntry()
    {
        NewWriter().Upsert(
            new CosmicBinding(new[] { "Ctrl", "Shift" }, "4"),
            "/usr/bin/XerahS capture region --workflow-id abc");

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(ron, Does.Contain("key: \"4\""));
            Assert.That(ron, Does.Contain("Spawn(\"/usr/bin/XerahS capture region --workflow-id abc\")"));
            Assert.That(ron, Does.Contain("Ctrl"));
            Assert.That(ron, Does.Contain("Shift"));
            Assert.That(CosmicShortcutsRon.Parse(ron), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Upsert_PreservesForeignEntries_IncludingCommasInStrings()
    {
        File.WriteAllText(_file, "{\n    (modifiers: [Super], key: \"q\"): Spawn(\"foo --bar, baz\"),\n}\n");

        NewWriter().Upsert(
            new CosmicBinding(Array.Empty<string>(), "Print"),
            "/usr/bin/XerahS capture region --workflow-id x");

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(CosmicShortcutsRon.Parse(ron), Has.Count.EqualTo(2));
            Assert.That(ron, Does.Contain("Spawn(\"foo --bar, baz\")"));
            Assert.That(ron, Does.Contain("key: \"q\""));
            Assert.That(ron, Does.Contain("key: \"Print\""));
        });
    }

    [Test]
    public void Upsert_SameBinding_ReplacesNotDuplicates()
    {
        var writer = NewWriter();
        var binding = new CosmicBinding(new[] { "Super" }, "Print");
        writer.Upsert(binding, "/usr/bin/XerahS capture region --workflow-id one");
        writer.Upsert(binding, "/usr/bin/XerahS capture region --workflow-id two");

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(CosmicShortcutsRon.Parse(ron), Has.Count.EqualTo(1));
            Assert.That(ron, Does.Contain("--workflow-id two"));
            Assert.That(ron, Does.Not.Contain("--workflow-id one"));
        });
    }

    [Test]
    public void Remove_DropsTargetBinding_KeepsForeign()
    {
        File.WriteAllText(_file, "{ (modifiers: [Super], key: \"q\"): Spawn(\"user-thing\"), }");
        var writer = NewWriter();
        var binding = new CosmicBinding(Array.Empty<string>(), "Print");
        writer.Upsert(binding, "/usr/bin/XerahS capture region --workflow-id x");
        writer.Remove(binding);

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(ron, Does.Contain("user-thing"));
            Assert.That(ron, Does.Not.Contain("XerahS capture"));
        });
    }

    [Test]
    public void RemoveAllXerahsOwned_DropsOnlyXerahsEntries()
    {
        File.WriteAllText(_file, "{ (modifiers: [Super], key: \"q\"): Spawn(\"user-thing\"), }");
        var writer = NewWriter();
        writer.Upsert(new CosmicBinding(Array.Empty<string>(), "Print"), "/usr/bin/XerahS capture region --workflow-id x");
        writer.Upsert(new CosmicBinding(new[] { "Ctrl" }, "p"), "/usr/bin/XerahS capture screen --workflow-id y");

        writer.RemoveAllXerahsOwned();

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(ron, Does.Contain("user-thing"));
            Assert.That(ron, Does.Not.Contain("XerahS"));
            Assert.That(CosmicShortcutsRon.Parse(ron), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Upsert_OnComboHeldByForeignEntry_ReplacesWithOwnedSingleEntry()
    {
        // COSMIC's custom map holds one action per (modifiers,key), so taking a combo a user already
        // bound necessarily replaces it. Pin that behavior: exactly one XerahS-owned entry remains.
        File.WriteAllText(_file, "{ (modifiers: [Ctrl, Shift], key: \"4\"): Spawn(\"user-thing\"), }");

        NewWriter().Upsert(new CosmicBinding(new[] { "Ctrl", "Shift" }, "4"),
            "/usr/bin/XerahS capture --workflow-id z");

        string ron = File.ReadAllText(_file);
        var entries = CosmicShortcutsRon.Parse(ron);
        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Binding.IsXerahsOwned, Is.True);
            Assert.That(ron, Does.Not.Contain("user-thing"));
        });
    }

    [Test]
    public void Parse_IgnoresLineAndBlockComments()
    {
        // A user (or the XIP examples) may hand-add comments; a brace inside a comment must not
        // corrupt map extraction, and entries must still parse. See XIP0079.
        string ron =
            "// top comment with a brace { not real\n" +
            "{\n" +
            "    // a line comment\n" +
            "    (modifiers: [Super], key: \"q\"): Spawn(\"foo\"), /* inline */\n" +
            "    /* block\n  comment */\n" +
            "    (modifiers: [], key: \"Print\"): Spawn(\"bar\"),\n" +
            "}\n";

        Assert.That(CosmicShortcutsRon.Parse(ron), Has.Count.EqualTo(2));
    }

    [Test]
    public void Upsert_OnUnparseableFile_LeavesFileUntouched()
    {
        // Fail-closed: an unparseable custom file must never be overwritten (would destroy foreign data).
        string garbage = "{ this is (not valid RON at all ][ }";
        File.WriteAllText(_file, garbage);

        Assert.Throws<FormatException>(() =>
            NewWriter().Upsert(new CosmicBinding(Array.Empty<string>(), "Print"),
                "/usr/bin/XerahS capture --workflow-id z"));

        Assert.That(File.ReadAllText(_file), Is.EqualTo(garbage));
    }

    [Test]
    public void Upsert_WritesDescriptionAsRonSome()
    {
        // cosmic's Binding.description is Option<String> with no implicit_some, so RON requires
        // Some("..."); a bare string makes cosmic reject the entire custom file (ExpectedOption) and
        // fall back to defaults — silently killing every XerahS hotkey. See XIP0079 / binding.rs.
        NewWriter().Upsert(new CosmicBinding(new[] { "Super" }, "Print"),
            "/usr/bin/XerahS capture --workflow-id x");

        string ron = File.ReadAllText(_file);
        Assert.Multiple(() =>
        {
            Assert.That(ron, Does.Contain("description: Some(\"Managed by XerahS\")"));
            Assert.That(ron, Does.Not.Contain("description: \"Managed by XerahS\""));
        });
    }

    [Test]
    public void Parse_ReadsSomeWrappedDescription_AsOwned()
    {
        var entries = CosmicShortcutsRon.Parse(
            "{ (modifiers: [Super], key: \"p\", description: Some(\"Managed by XerahS\")): Spawn(\"x\"), }");
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Binding.Description, Is.EqualTo("Managed by XerahS"));
            Assert.That(entries[0].Binding.IsXerahsOwned, Is.True);
        });
    }

    [Test]
    public void Upsert_OwnedEntry_RoundTripsThroughSomeAsOwned()
    {
        // Reader must unwrap the Some(...) the writer now emits, or it can't re-identify its own
        // entries for dedup/removal on the next run.
        var writer = NewWriter();
        var binding = new CosmicBinding(new[] { "Ctrl" }, "p");
        writer.Upsert(binding, "/usr/bin/XerahS capture --workflow-id z");
        Assert.That(writer.ContainsXerahsBinding(binding), Is.True);
    }

    [Test]
    public void Parse_RoundTrips_PreservingForeignValuesAndEscapes()
    {
        string original =
            "{\n" +
            "    (modifiers: [Super, Ctrl], key: \"space\"): System(Screenshot),\n" +
            "    (modifiers: [], key: \"Print\"): Spawn(\"a, b \\\"c\\\"\"),\n" +
            "}\n";

        var entries = CosmicShortcutsRon.Parse(original);
        Assert.That(entries, Has.Count.EqualTo(2));

        string round = CosmicShortcutsRon.Serialize(entries);
        Assert.Multiple(() =>
        {
            Assert.That(CosmicShortcutsRon.Parse(round), Has.Count.EqualTo(2));
            Assert.That(round, Does.Contain("System(Screenshot)"));
            Assert.That(round, Does.Contain("Spawn(\"a, b \\\"c\\\"\")"));
        });
    }

    [Test]
    public void ParseBinding_ReadsModifiersKeyAndDescription()
    {
        var entries = CosmicShortcutsRon.Parse(
            "{ (modifiers: [Super, Shift], key: \"bracketleft\", description: \"Managed by XerahS\"): Spawn(\"x\"), }");
        Assert.That(entries, Has.Count.EqualTo(1));
        var b = entries[0].Binding;
        Assert.Multiple(() =>
        {
            Assert.That(b.Key, Is.EqualTo("bracketleft"));
            Assert.That(b.Modifiers, Is.EquivalentTo(new[] { "Super", "Shift" }));
            Assert.That(b.IsXerahsOwned, Is.True);
        });
    }

    [Test]
    public void ResolveDefaultPath_HonorsXdgConfigHome()
    {
        string? prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/xdgtest");
            string path = CosmicShortcutConfigWriter.ResolveDefaultPath();
            Assert.That(path, Is.EqualTo("/tmp/xdgtest/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior);
        }
    }
}
