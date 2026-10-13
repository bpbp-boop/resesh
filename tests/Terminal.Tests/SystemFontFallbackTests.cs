using Resesh.Terminal.Native;
using Vortice.DirectWrite;

namespace Resesh.Terminal.Tests;

/// <summary>Characters outside the terminal's own font chain resolve to whichever installed
/// font Windows would use, and to nothing when no font has them.</summary>
public class SystemFontFallbackTests
{
    private static string? FamilyFor(uint codepoint)
    {
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory2>(FactoryType.Shared);
        using var family = SystemFontFallback.Map(factory, codepoint, "Consolas");
        return family is null ? null : SystemFontFallback.NameOf(family);
    }

    [Theory]
    [InlineData(0x0E01u)] // Thai
    [InlineData(0x0F40u)] // Tibetan
    [InlineData(0x16A0u)] // Runic
    public void ScriptsTheBaseFontLacksMapToAnotherInstalledFamily(uint codepoint)
    {
        var family = FamilyFor(codepoint);
        Assert.NotNull(family);
        Assert.NotEqual("Consolas", family);
    }

    [Fact]
    public void CharactersTheBaseFontHasStayOnIt() => Assert.Equal("Consolas", FamilyFor('A'));

    [Theory]
    [InlineData(0xF0000u)]  // private use: no system font claims it
    [InlineData(0xD800u)]   // lone surrogate
    [InlineData(0x110000u)] // past the last codepoint
    public void CharactersNoFontHasMapToNothing(uint codepoint) => Assert.Null(FamilyFor(codepoint));
}
