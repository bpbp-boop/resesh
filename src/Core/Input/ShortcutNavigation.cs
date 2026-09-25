namespace Resesh.Core.Input;

public enum NavigationDirection
{
    Left,
    Right,
    Up,
    Down,
}

public readonly record struct LayoutBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>Index arithmetic behind the tab shortcuts, kept free of UI types so it is testable.</summary>
public static class TabNavigation
{
    /// <summary>The tab <paramref name="delta"/> steps away, wrapping at both ends. -1 when empty.</summary>
    public static int Cycle(int index, int count, int delta)
    {
        if (count <= 0)
            return -1;
        return ((index + delta) % count + count) % count;
    }

    /// <summary>Browser rule: Ctrl+1..8 select that tab when it exists; Ctrl+9 is always the last tab.</summary>
    public static int GoTo(int number, int count) =>
        number switch
        {
            9 => count - 1,
            >= 1 and <= 8 when number <= count => number - 1,
            _ => -1,
        };

    /// <summary>Where a tab moves one step left or right. Pinned tabs stay in the pinned block
    /// at the front and unpinned tabs stay after it. -1 when the tab is already at its edge.</summary>
    public static int MoveTarget(int index, int delta, int count, int pinnedCount, bool isPinned)
    {
        if (index < 0 || index >= count)
            return -1;
        var first = isPinned ? 0 : pinnedCount;
        var last = isPinned ? pinnedCount - 1 : count - 1;
        var target = index + Math.Sign(delta);
        return target >= first && target <= last ? target : -1;
    }
}

/// <summary>Finds the tab group next to the current one on screen, like pane focus in Windows Terminal.</summary>
public static class GroupNavigation
{
    private const double Tolerance = 2;

    /// <summary>The nearest candidate on the <paramref name="direction"/> side that shares some
    /// of the current group's edge. Ties go to the candidate most centered on that edge.</summary>
    public static T? FindNeighbor<T>(
        IReadOnlyList<(T Item, LayoutBounds Bounds)> groups,
        LayoutBounds current,
        NavigationDirection direction) where T : class
    {
        T? best = null;
        var bestDistance = double.MaxValue;
        var bestOffset = double.MaxValue;
        foreach (var (item, bounds) in groups)
        {
            var (distance, overlaps, offset) = direction switch
            {
                NavigationDirection.Left => (current.X - bounds.Right,
                    Overlaps(bounds.Y, bounds.Bottom, current.Y, current.Bottom),
                    Math.Abs(bounds.CenterY - current.CenterY)),
                NavigationDirection.Right => (bounds.X - current.Right,
                    Overlaps(bounds.Y, bounds.Bottom, current.Y, current.Bottom),
                    Math.Abs(bounds.CenterY - current.CenterY)),
                NavigationDirection.Up => (current.Y - bounds.Bottom,
                    Overlaps(bounds.X, bounds.Right, current.X, current.Right),
                    Math.Abs(bounds.CenterX - current.CenterX)),
                _ => (bounds.Y - current.Bottom,
                    Overlaps(bounds.X, bounds.Right, current.X, current.Right),
                    Math.Abs(bounds.CenterX - current.CenterX)),
            };
            if (distance < -Tolerance || !overlaps)
                continue;
            if (distance < bestDistance - Tolerance
                || (Math.Abs(distance - bestDistance) <= Tolerance && offset < bestOffset))
            {
                best = item;
                bestDistance = distance;
                bestOffset = offset;
            }
        }
        return best;
    }

    private static bool Overlaps(double start, double end, double otherStart, double otherEnd) =>
        Math.Min(end, otherEnd) - Math.Max(start, otherStart) > Tolerance;
}
