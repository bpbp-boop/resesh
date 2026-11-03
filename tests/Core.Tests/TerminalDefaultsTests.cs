using Resesh.Core.Storage;
using Resesh.Terminal.Native;

namespace Resesh.Core.Tests;

/// <summary>The terminal library cannot reference Core, so it keeps its own copy of the
/// settings defaults it starts with. They must stay the app's defaults.</summary>
public class TerminalDefaultsTests
{
    [Fact]
    public void TerminalStartsWithTheAppDefaults()
    {
        var settings = new AppSettings();
        Assert.Equal(TerminalDefaults.FontFamily, FontFamilies.Default);
        Assert.Equal(TerminalDefaults.FontFamily, settings.FontFamily);
        Assert.Equal(TerminalDefaults.FontSize, settings.FontSize);
        Assert.Equal(TerminalDefaults.Scrollback, settings.Scrollback);
    }

    [Theory]
    [InlineData("'Fira Code', Consolas", "Fira Code")]
    [InlineData(" JetBrains Mono ", "JetBrains Mono")]
    [InlineData(", Fira Code", "Fira Code")] // a blank first entry
    [InlineData("monospace, Fira Code", "Fira Code")]
    [InlineData("", "Cascadia Mono")]
    [InlineData(null, "Cascadia Mono")]
    [InlineData("monospace", "Cascadia Mono")]
    public void PanelsUseTheFirstFamilyOrTheDefault(string? list, string expected) =>
        Assert.Equal(expected, TerminalDefaults.PrimaryFamily(list, _ => true));

    [Theory]
    [InlineData("Not Installed, Fira Code", "Fira Code")]
    [InlineData("Not Installed", "Cascadia Mono")]
    [InlineData("Not Installed, Cascadia Mono", "Cascadia Mono")]
    public void PanelsSkipFontsThatAreNotInstalled_AsTheRendererDoes(string list, string expected) =>
        Assert.Equal(expected, TerminalDefaults.PrimaryFamily(list, family => family != "Not Installed"));

    [Fact]
    public void WithoutTheDefaultFontsPanelsUseConsolas() =>
        Assert.Equal("Consolas", TerminalDefaults.PrimaryFamily("Fira Code", family => family == "Consolas"));

    /// <summary>The settings page shows FontFamilies.Primary as the font; with every font
    /// installed the terminal must draw that same one.</summary>
    [Theory]
    [InlineData("'Fira Code', Consolas")]
    [InlineData(", Fira Code")]
    [InlineData("monospace, Fira Code, Consolas")]
    [InlineData(FontFamilies.Default)]
    [InlineData(" \"JetBrains Mono\" ,, consolas")]
    public void SettingsAndTerminalReadTheSameFont(string list)
    {
        Assert.Equal(FontFamilies.Primary(list), TerminalDefaults.PrimaryFamily(list, _ => true));
        Assert.Equal(FontFamilies.Split(list).Where(name => !FontFamilies.IsGeneric(name)), TerminalDefaults.FamilyNames(list));
    }
}
