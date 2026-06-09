using Avalonia.Input;
using NUnit.Framework;
using XerahS.Platform.Linux.Services;

namespace XerahS.Tests.Platform.Linux;

[TestFixture]
public class CosmicKeysymMapperTests
{
    [TestCase(Key.PrintScreen, "Print")]
    [TestCase(Key.A, "a")]      // cosmic stores single-letter keysyms lowercase
    [TestCase(Key.Z, "z")]
    [TestCase(Key.D4, "4")]
    [TestCase(Key.Space, "space")]
    [TestCase(Key.Return, "Return")]
    public void MapKey_ProducesXkbKeysymNames(Key key, string expected)
    {
        Assert.That(CosmicKeysymMapper.MapKey(key), Is.EqualTo(expected));
    }

    [Test]
    public void MapKey_None_ReturnsNull()
    {
        Assert.That(CosmicKeysymMapper.MapKey(Key.None), Is.Null);
    }

    [Test]
    public void MapModifiers_TranslatesAvaloniaToCosmicTokens()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CosmicKeysymMapper.MapModifiers(KeyModifiers.Control | KeyModifiers.Shift),
                Is.EquivalentTo(new[] { "Ctrl", "Shift" }));
            Assert.That(CosmicKeysymMapper.MapModifiers(KeyModifiers.Meta), Is.EquivalentTo(new[] { "Super" }));
            Assert.That(CosmicKeysymMapper.MapModifiers(KeyModifiers.Alt), Is.EquivalentTo(new[] { "Alt" }));
            Assert.That(CosmicKeysymMapper.MapModifiers(KeyModifiers.None), Is.Empty);
        });
    }

    [Test]
    public void TryMap_PrintScreenNoModifiers_BuildsBareBinding()
    {
        bool ok = CosmicKeysymMapper.TryMap(Key.PrintScreen, KeyModifiers.None, out var binding);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(binding!.Key, Is.EqualTo("Print"));
            Assert.That(binding.Modifiers, Is.Empty);
        });
    }

    [Test]
    public void TryMap_KeyNone_Fails()
    {
        Assert.That(CosmicKeysymMapper.TryMap(Key.None, KeyModifiers.Control, out var binding), Is.False);
        Assert.That(binding, Is.Null);
    }
}
