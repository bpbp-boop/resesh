namespace Resesh.App.Controls;

internal readonly record struct TabDragSlot(double Left, double Width)
{
    public double Right => Left + Width;
    public double Center => Left + Width / 2;
}

internal readonly record struct TabDragPlacement(double Left, int Index);

internal static class TabDragLayout
{
    public static TabDragPlacement Resolve(
        IReadOnlyList<TabDragSlot> slots, int source, int firstMovable, double left)
    {
        var width = slots[source].Width;
        var minimum = slots[firstMovable].Left;
        var maximum = Math.Max(minimum, slots[^1].Right - width);
        left = Math.Clamp(left, minimum, maximum);
        // DPI rounding makes nominally equal tab widths differ by a few millionths.
        // At either limit, choose the end slot without comparing rounded centers.
        if (left <= minimum)
            return new(left, firstMovable);
        if (left >= maximum)
            return new(left, slots.Count - 1);
        var center = left + width / 2;
        var target = source;
        for (var i = firstMovable; i < source; i++)
        {
            if (center <= slots[i].Center)
            {
                target = i;
                break;
            }
        }
        for (var i = source + 1; i < slots.Count; i++)
        {
            if (center >= slots[i].Center)
                target = i;
        }
        return new(left, target);
    }

    public static double Offset(int index, int source, int target, double width) =>
        index >= target && index < source ? width :
        index > source && index <= target ? -width : 0;
}
