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
    [InlineData("", "Cascadia Mono")]
    [InlineData(null, "Cascadia Mono")]
    public void PanelsUseTheFirstFamilyOrTheDefault(string? list, string expected) =>
        Assert.Equal(expected, TerminalDefaults.PrimaryFamily(list));
}
