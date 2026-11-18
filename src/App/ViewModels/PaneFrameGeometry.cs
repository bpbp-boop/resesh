namespace Resesh.App.ViewModels;

/// <summary>A rectangle in device-independent pixels, in the coordinates of the pane area.</summary>
public readonly record struct FrameRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Where the focused pane's frame is drawn, and how far the pane's content steps in
/// to stay clear of it. A null edge is not drawn.</summary>
public sealed record PaneFrame(
    FrameRect? Left, FrameRect? Right, FrameRect? Bottom,
    double InsetLeft, double InsetRight, double InsetBottom);

/// <summary>
/// The geometry of the focused pane's outline, drawn like a folder: its sides rise from the
/// bottom to the top of its content, and the line under its tab strip (the pane's own) runs
/// along the top to the selected tab. The window measures and draws; the rules live here so
/// they are tested without one.
/// </summary>
public static class PaneFrameGeometry
{
    /// <summary>A colour tag's frame is wider than a theme's, so the session about to take a
    /// risky command is unmistakable.</summary>
    public static int Thickness(bool tagged) => tagged ? 2 : 1;

    /// <summary>
    /// The frame of <paramref name="pane"/>, laid out in an area <paramref name="areaWidth"/>
    /// by <paramref name="areaHeight"/>. A side with a neighbour has a one-pixel gutter, and
    /// the edge sits on it so a seam never doubles; a wider edge keeps its outer pixel on the
    /// gutter and grows into the pane. A side with no neighbour is drawn only when
    /// <paramref name="allSides"/> (a themed or tagged session), on the pane's own outermost
    /// pixels. The content steps in by as much of each drawn edge as lies inside the pane, so
    /// the frame never covers its last row or column.
    /// </summary>
    public static PaneFrame For(FrameRect pane, double areaWidth, double areaHeight,
        double contentEdgeTop, int thickness, bool allSides)
    {
        var t = thickness;
        var gutterLeft = pane.X > 0.5;
        var gutterRight = pane.Right < areaWidth - 0.5;
        var gutterBelow = pane.Bottom < areaHeight - 0.5;
        var drawLeft = gutterLeft || allSides;
        var drawRight = gutterRight || allSides;
        var drawBottom = gutterBelow || allSides;

        var leftX = gutterLeft ? pane.X - 1 : pane.X;
        var rightX = gutterRight ? pane.Right + 1 - t : pane.Right - t;
        var bottomY = gutterBelow ? pane.Bottom + 1 - t : pane.Bottom - t;
        // The sides meet the tab strip's bottom line, which is as wide as the frame.
        var top = pane.Y + contentEdgeTop + 1 - t;
        // The bottom spans to whichever sides are drawn so the corners join.
        var bottomLeft = drawLeft ? leftX : pane.X;
        var bottomRight = drawRight ? rightX + t : pane.Right;

        double Inset(bool drawn, bool gutter) => drawn ? (gutter ? t - 1 : t) : 0;
        return new PaneFrame(
            Edge(drawLeft, new FrameRect(leftX, top, t, bottomY + t - top)),
            Edge(drawRight, new FrameRect(rightX, top, t, bottomY + t - top)),
            Edge(drawBottom, new FrameRect(bottomLeft, bottomY, bottomRight - bottomLeft, t)),
            Inset(drawLeft, gutterLeft),
            Inset(drawRight, gutterRight),
            Inset(drawBottom, gutterBelow));
    }

    private static FrameRect? Edge(bool drawn, FrameRect rect) =>
        drawn && rect.Width > 0 && rect.Height > 0 ? rect : null;
}
