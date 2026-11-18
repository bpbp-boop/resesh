using Resesh.App.ViewModels;

namespace Resesh.AppLogic.Tests;

public sealed class PaneFrameGeometryTests
{
    // A 1000 x 600 pane area; tab strips are 36 px, so content starts at 35 (the strip's last line).
    private const double Width = 1000, Height = 600, StripEdge = 35;

    private static PaneFrame Frame(FrameRect pane, bool tagged, bool allSides) =>
        PaneFrameGeometry.For(pane, Width, Height, StripEdge, PaneFrameGeometry.Thickness(tagged), allSides);

    private static PaneInset Reserved(FrameRect pane) => PaneFrameGeometry.ReservedInset(pane, Width, Height);

    [Fact]
    public void ALonePaneIsFramedOnlyWhenThemedOrTagged()
    {
        var lone = new FrameRect(0, 0, Width, Height);

        var plain = Frame(lone, tagged: false, allSides: false);
        Assert.Equal(new PaneFrame(null, null, null), plain);

        var themed = Frame(lone, tagged: false, allSides: true);
        Assert.Equal(new FrameRect(0, 35, 1, 565), themed.Left);
        Assert.Equal(new FrameRect(999, 35, 1, 565), themed.Right);
        Assert.Equal(new FrameRect(0, 599, 1000, 1), themed.Bottom);
    }

    [Fact]
    public void ATaggedLonePaneIsTwoPixelsInside()
    {
        var frame = Frame(new FrameRect(0, 0, Width, Height), tagged: true, allSides: true);

        // Sides start a pixel higher, meeting the two-pixel strip line.
        Assert.Equal(new FrameRect(0, 34, 2, 566), frame.Left);
        Assert.Equal(new FrameRect(998, 34, 2, 566), frame.Right);
        Assert.Equal(new FrameRect(0, 598, 1000, 2), frame.Bottom);
    }

    [Fact]
    public void ASplitPaneSitsOnItsGuttersAndDrawsOnlyThoseUnlessThemed()
    {
        // The right half of a side-by-side split: a gutter on its left at x = 500.
        var right = new FrameRect(501, 0, 499, Height);

        var plain = Frame(right, tagged: false, allSides: false);
        Assert.Equal(new FrameRect(500, 35, 1, 565), plain.Left);
        Assert.Null(plain.Right);
        Assert.Null(plain.Bottom);

        var tagged = Frame(right, tagged: true, allSides: true);
        Assert.Equal(new FrameRect(500, 34, 2, 566), tagged.Left); // gutter pixel plus one inside
        Assert.Equal(new FrameRect(998, 34, 2, 566), tagged.Right);
        Assert.Equal(new FrameRect(500, 598, 500, 2), tagged.Bottom); // spans corner to corner
    }

    [Fact]
    public void APaneAboveAnotherSitsOnTheGutterBelowIt()
    {
        var top = new FrameRect(0, 0, Width, 299); // gutter at y = 299

        var frame = Frame(top, tagged: true, allSides: true);

        Assert.Equal(new FrameRect(0, 298, 1000, 2), frame.Bottom);
    }

    [Fact]
    public void APaneTooSmallForItsSidesDrawsNone()
    {
        var frame = Frame(new FrameRect(0, 0, Width, 20), tagged: true, allSides: true);

        Assert.Null(frame.Left);
        Assert.Null(frame.Right);
        Assert.NotNull(frame.Bottom);
    }

    [Fact]
    public void EveryPaneReservesTheWidestFrameByPositionAlone()
    {
        Assert.Equal(new PaneInset(2, 2, 2), Reserved(new FrameRect(0, 0, Width, Height)));
        // Left half of a split: gutter on the right, which carries the frame's outer pixel.
        Assert.Equal(new PaneInset(2, 1, 2), Reserved(new FrameRect(0, 0, 500, Height)));
        Assert.Equal(new PaneInset(1, 2, 2), Reserved(new FrameRect(501, 0, 499, Height)));
        Assert.Equal(new PaneInset(2, 2, 1), Reserved(new FrameRect(0, 0, Width, 299)));
    }

    public static TheoryData<double, double, double, double> Panes => new()
    {
        { 0, 0, Width, Height },     // lone
        { 0, 0, 500, Height },       // left of a split
        { 501, 0, 499, Height },     // right of a split
        { 0, 0, Width, 299 },        // above another
        { 0, 300, Width, 300 },      // below another
        { 334, 0, 332, 299 },        // middle of a grid
    };

    [Theory]
    [MemberData(nameof(Panes))]
    public void NoFrameEverReachesPastTheReservedSpace(double x, double y, double w, double h)
    {
        var pane = new FrameRect(x, y, w, h);
        var reserved = Reserved(pane);
        foreach (var tagged in new[] { false, true })
        foreach (var allSides in new[] { false, true })
        {
            var frame = Frame(pane, tagged, allSides);
            if (frame.Left is { } left)
                Assert.True(left.Right <= pane.X + reserved.Left, $"left {tagged}/{allSides}");
            if (frame.Right is { } right)
                Assert.True(right.X >= pane.Right - reserved.Right, $"right {tagged}/{allSides}");
            if (frame.Bottom is { } bottom)
                Assert.True(bottom.Y >= pane.Bottom - reserved.Bottom, $"bottom {tagged}/{allSides}");
        }
    }
}
