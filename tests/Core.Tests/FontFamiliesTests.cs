using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

/// <summary>The terminal font list: a font, then its fallbacks, kept apart when either changes.</summary>
public class FontFamiliesTests
{
    [Fact]
    public void SplitsTrimsUnquotesAndDropsDuplicates()
    {
        Assert.Equal(["Fira Code", "Consolas"], FontFamilies.Split(" 'Fira Code' ,, Consolas , consolas"));
        Assert.Empty(FontFamilies.Split(null));
    }

    [Fact]
    public void PrimaryAndFallbacksPartTheList()
    {
        Assert.Equal("Cascadia Mono", FontFamilies.Primary(FontFamilies.Default));
        Assert.Equal("Consolas, monospace", FontFamilies.Fallbacks(FontFamilies.Default));
        Assert.Equal("", FontFamilies.Fallbacks("Consolas"));
    }

    [Fact]
    public void WithPrimaryKeepsFallbacks_AndMovesAFallbackToTheFront()
    {
        Assert.Equal("Geist Mono, Consolas, monospace", FontFamilies.WithPrimary(FontFamilies.Default, "Geist Mono"));
        Assert.Equal("Consolas, monospace", FontFamilies.WithPrimary(FontFamilies.Default, "Consolas"));
    }

    [Fact]
    public void WithFallbacksKeepsTheFont()
    {
        Assert.Equal("Cascadia Mono, Symbols Nerd Font Mono",
            FontFamilies.WithFallbacks(FontFamilies.Default, "Symbols Nerd Font Mono"));
        Assert.Equal("Cascadia Mono", FontFamilies.WithFallbacks(FontFamilies.Default, " "));
    }

    [Fact]
    public void SessionFontGoesInFrontOfTheAppFallbacks()
    {
        var app = new AppSettings { FontFamily = "Cascadia Mono, JetBrainsMono Nerd Font Mono" };

        var effective = app.WithOverrides(new TerminalOverrides { FontFamily = "Consolas" });

        Assert.Equal("Consolas, Cascadia Mono, JetBrainsMono Nerd Font Mono", effective.FontFamily);
    }
}
