using Resesh.App.ViewModels;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

public sealed class TabThemeTests
{
    private static TabViewModel Tab(TerminalOverrides? overrides) => new(
        new Session { Overrides = overrides },
        new ViewModelEnvironment
        {
            CurrentTheme = () => "dark",
            ResolveTheme = theme => theme,
            ShowAgentIcons = () => true,
            IsSessionVisible = _ => true,
            ApplySessionSettings = _ => { },
            ReportError = _ => { },
        });

    [Fact]
    public void TabUsesTheSessionThemeOverride_OtherwiseTheAppTheme()
    {
        Assert.Equal("dark", Tab(null).TabTheme);
        Assert.Equal("dark", Tab(new TerminalOverrides { FontSize = 18 }).TabTheme);
        Assert.Equal("crimson", Tab(new TerminalOverrides { Theme = "crimson" }).TabTheme);
    }

    [Fact]
    public void SavingTheSessionOrChangingTheAppTheme_RepaintsTheTab()
    {
        var tab = Tab(null);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        tab.Session = tab.Session with { Overrides = new TerminalOverrides { Theme = "crimson" } };
        Assert.Contains(nameof(TabViewModel.TabTheme), changed);
        Assert.Equal("crimson", tab.TabTheme);

        changed.Clear();
        tab.ApplyAppTheme("light");
        Assert.Contains(nameof(TabViewModel.TabTheme), changed);
        Assert.Equal("crimson", tab.TabTheme);

        tab.Session = tab.Session with { Overrides = null };
        Assert.Equal("light", tab.TabTheme);
    }
}
