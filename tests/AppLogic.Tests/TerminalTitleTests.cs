using Resesh.App.ViewModels;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

public sealed class TerminalTitleTests
{
    private static TabViewModel Tab()
    {
        var tab = new TabViewModel(
            new Session { Name = "test", Host = "server" },
            new ViewModelEnvironment
            {
                CurrentTheme = () => "dark", ResolveTheme = theme => theme,
                ShowAgentIcons = () => true, IsSessionVisible = _ => true,
                ApplySessionSettings = _ => { }, ReportError = _ => { },
            });
        tab.MarkConnected(remoteSessionSlot: null);
        return tab;
    }

    [Theory]
    [InlineData("deploy\r\nlog", "deploy log")]
    [InlineData("root@prod\u202Eved", "root@prodved")]
    [InlineData("\u001b[31mred\u0007", "[31mred")]
    [InlineData("a\u009Bb", "a b")]
    [InlineData("🚀 prod", "🚀 prod")]
    public void TitlesFromTheHostAreSanitized(string raw, string expected)
    {
        var tab = Tab();
        tab.ApplyTerminalTitle(raw);
        Assert.Equal(expected, tab.TerminalTitle);
    }

    [Fact]
    public void ATitleOfOnlyControlCharactersIsNoTitle()
    {
        var tab = Tab();
        tab.ApplyTerminalTitle("\u0007\u202E\r\n");
        Assert.Null(tab.TerminalTitle);
    }
}
