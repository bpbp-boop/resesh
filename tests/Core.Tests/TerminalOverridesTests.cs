using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class TerminalOverridesTests
{
    private static readonly AppSettings Defaults = new()
    {
        Theme = "dark",
        FontFamily = "Cascadia Mono",
        FontSize = 14,
        Scrollback = 10000,
    };

    [Fact]
    public void WithOverrides_Null_ReturnsSameSettings()
    {
        Assert.Same(Defaults, Defaults.WithOverrides(null));
    }

    [Fact]
    public void WithOverrides_EmptyOverrides_ChangesNothing()
    {
        Assert.Equal(Defaults, Defaults.WithOverrides(new TerminalOverrides()));
    }

    [Fact]
    public void WithOverrides_SetMembersWin_NullMembersInherit()
    {
        var effective = Defaults.WithOverrides(new TerminalOverrides { Theme = "light", FontSize = 18 });

        Assert.Equal("light", effective.Theme);
        Assert.Equal(18, effective.FontSize);
        Assert.Equal(Defaults.FontFamily, effective.FontFamily);
        Assert.Equal(Defaults.Scrollback, effective.Scrollback);
    }

    [Fact]
    public void IsEmpty_TracksMembers()
    {
        Assert.True(new TerminalOverrides().IsEmpty);
        Assert.False(new TerminalOverrides { Scrollback = 50000 }.IsEmpty);
    }
}
