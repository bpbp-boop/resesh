using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class WindowLayoutTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void AMissingOrTinyPlacementIsNotRestored()
    {
        Assert.Null(WindowLayout.Requested(null));
        Assert.Null(WindowLayout.Requested(new WindowPlacement(10, 10, 319, 600)));
        Assert.Null(WindowLayout.Requested(new WindowPlacement(10, 10, 800, 239)));
        Assert.Equal(new PixelRect(10, 20, 320, 240), WindowLayout.Requested(new WindowPlacement(10, 20, 320, 240)));
    }

    [Fact]
    public void AWindowThatFitsReopensWhereItWas()
    {
        var saved = new PixelRect(100, 50, 1200, 800);
        Assert.Equal(saved, WindowLayout.FitInto(saved, Screen));
    }

    [Fact]
    public void AWindowOffTheScreenIsPulledBackWhole()
    {
        // Saved on a monitor to the right that has since been unplugged.
        Assert.Equal(new PixelRect(720, 240, 1200, 800), WindowLayout.FitInto(new PixelRect(2500, 900, 1200, 800), Screen));
        Assert.Equal(new PixelRect(0, 0, 1200, 800), WindowLayout.FitInto(new PixelRect(-400, -50, 1200, 800), Screen));
    }

    [Fact]
    public void AWindowLargerThanTheScreenShrinksToIt()
    {
        Assert.Equal(Screen, WindowLayout.FitInto(new PixelRect(-10, -10, 2560, 1440), Screen));
        // A work area that does not start at the origin: a taskbar on the left, a second monitor.
        var offset = new PixelRect(1920, 0, 1280, 984);
        Assert.Equal(new PixelRect(1920, 0, 1280, 984), WindowLayout.FitInto(new PixelRect(1900, 0, 1600, 1200), offset));
    }

    [Fact]
    public void ATinyWindowIsNotSaved()
    {
        Assert.Null(WindowLayout.ToSave(new PixelRect(0, 0, 160, 28), maximized: false)); // mid-minimize
        Assert.Equal(new WindowPlacement(5, 6, 900, 700, true), WindowLayout.ToSave(new PixelRect(5, 6, 900, 700), maximized: true));
    }

    [Theory]
    [InlineData("recent", "recent")]
    [InlineData("recordings", "recordings")]
    [InlineData("workspaces", "workspaces")]
    [InlineData("sessions", "sessions")]
    [InlineData("history", "sessions")] // a later version's tab, or a hand edit
    [InlineData(null, "sessions")]
    public void UnknownRailTabsOpenSessions(string? saved, string shown) =>
        Assert.Equal(shown, WindowLayout.NormalizeRailTab(saved));

    [Theory]
    [InlineData(null, 280)]
    [InlineData(50.0, 180)]
    [InlineData(5000.0, 800)]
    [InlineData(333.0, 333)]
    public void TheSavedPaneWidthIsKeptToWhatThePaneCanBe(double? saved, double width) =>
        Assert.Equal(width, WindowLayout.PaneWidth(saved));

    [Theory]
    [InlineData(SessionsPaneLayouts.Float, 2400, false, true)]
    [InlineData(SessionsPaneLayouts.Dock, 600, true, false)]
    [InlineData(SessionsPaneLayouts.Automatic, 999, false, true)]
    [InlineData(SessionsPaneLayouts.Automatic, 1000, false, false)]
    // Between the two bounds the pane keeps whatever it was doing, so no flicker at the edge.
    [InlineData(SessionsPaneLayouts.Automatic, 1030, true, true)]
    [InlineData(SessionsPaneLayouts.Automatic, 1030, false, false)]
    [InlineData(SessionsPaneLayouts.Automatic, 1060, true, false)]
    [InlineData("sideways", 900, false, true)] // unknown means Automatic
    public void ThePaneFloatsByTheSettingAndWindowWidth(string layout, double width, bool floatingNow, bool floats) =>
        Assert.Equal(floats, WindowLayout.Floats(layout, width, floatingNow));

    [Theory]
    [InlineData(280, 1600, 48, 280)]
    [InlineData(500, 1600, 48, 360)] // a wide docked pane floats at most 360 wide
    [InlineData(300, 400, 48, 256)] // leaves the tabs in view
    [InlineData(300, 250, 48, 200)] // but is never too narrow to use
    public void AFloatingPaneLeavesTheTabsInView(double paneWidth, double windowWidth, double railWidth, double width) =>
        Assert.Equal(width, WindowLayout.FloatingWidth(paneWidth, windowWidth, railWidth));

    [Fact]
    public void ADockedPaneLeavesTheTabsTheirMinimum()
    {
        Assert.Equal(1600 - 54 - 320, WindowLayout.DockedMaxWidth(1600, 54));
        Assert.Equal(180, WindowLayout.DockedMaxWidth(500, 54));
    }

    [Theory]
    [InlineData(280, 1000, true)]
    [InlineData(179, 1000, false)]
    [InlineData(999.6, 1000, false)] // squeezed against the limit by a narrow window
    public void OnlyAWidthTheUserChoseIsRemembered(double actual, double max, bool chosen) =>
        Assert.Equal(chosen, WindowLayout.IsChosenWidth(actual, max));
}
