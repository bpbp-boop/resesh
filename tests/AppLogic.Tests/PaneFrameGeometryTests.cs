using Resesh.App.ViewModels;

namespace Resesh.AppLogic.Tests;

public sealed class PaneFrameGeometryTests
{
    // A 1000 x 600 pane area; tab strips are 36 px, so content starts at 35 (the strip's last line).
    private const double Width = 1000, Height = 600, StripEdge = 35;

    private static PaneFrame Frame(FrameRect pane, bool tagged, bool allSides) =>
        PaneFrameGeometry.For(pane, Width, Height, StripEdge, PaneFrameGeometry.Thickness(tagged), allSides);

    [Fact]
    public void ALonePaneIsFramedOnlyWhenThemedOrTagged()
    {
        var lone = new FrameRect(0, 0, Width, Height);

        var plain = Frame(lone, tagged: false, allSides: false);
        Assert.Equal(new PaneFrame(null, null, null, 0, 0, 0), plain);

        var themed = Frame(lone, tagged: false, allSides: true);
        Assert.Equal(new FrameRect(0, 35, 1, 565), themed.Left);
        Assert.Equal(new FrameRect(999, 35, 1, 565), themed.Right);
        Assert.Equal(new FrameRect(0, 599, 1000, 1), themed.Bottom);
        Assert.Equal((1, 1, 1), (themed.InsetLeft, themed.InsetRight, themed.InsetBottom));
    }

    [Fact]
    public void ATaggedLonePaneIsTwoPixelsInsideAndTheContentClearsIt()
    {
        var frame = Frame(new FrameRect(0, 0, Width, Height), tagged: true, allSides: true);

        // Sides start a pixel higher, meeting the two-pixel strip line.
        Assert.Equal(new FrameRect(0, 34, 2, 566), frame.Left);
        Assert.Equal(new FrameRect(998, 34, 2, 566), frame.Right);
        Assert.Equal(new FrameRect(0, 598, 1000, 2), frame.Bottom);
        // The last row and column are never under the frame.
        Assert.Equal((2, 2, 2), (frame.InsetLeft, frame.InsetRight, frame.InsetBottom));
        Assert.True(frame.Bottom!.Value.Y >= Height - frame.InsetBottom);
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
        Assert.Equal((0, 0, 0), (plain.InsetLeft, plain.InsetRight, plain.InsetBottom));

        var tagged = Frame(right, tagged: true, allSides: true);
        Assert.Equal(new FrameRect(500, 34, 2, 566), tagged.Left); // gutter pixel plus one inside
        Assert.Equal(new FrameRect(998, 34, 2, 566), tagged.Right);
        Assert.Equal(new FrameRect(500, 598, 500, 2), tagged.Bottom); // spans corner to corner
        Assert.Equal((1, 2, 2), (tagged.InsetLeft, tagged.InsetRight, tagged.InsetBottom));
    }

    [Fact]
    public void APaneAboveAnotherSitsOnTheGutterBelowIt()
    {
        var top = new FrameRect(0, 0, Width, 299); // gutter at y = 299

        var frame = Frame(top, tagged: true, allSides: true);

        Assert.Equal(new FrameRect(0, 298, 1000, 2), frame.Bottom);
        Assert.Equal(1, frame.InsetBottom);
    }

    [Fact]
    public void APaneTooSmallForItsSidesDrawsNone()
    {
        var frame = Frame(new FrameRect(0, 0, Width, 20), tagged: true, allSides: true);

        Assert.Null(frame.Left);
        Assert.Null(frame.Right);
        Assert.NotNull(frame.Bottom);
    }
}
