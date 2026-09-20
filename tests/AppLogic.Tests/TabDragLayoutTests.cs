using Resesh.App.Controls;

namespace Resesh.AppLogic.Tests;

public class TabDragLayoutTests
{
    private static readonly TabDragSlot[] Slots = [new(0, 220), new(220, 220), new(440, 220)];

    [Theory]
    [InlineData(100, 30, 88)]
    [InlineData(100, 470, 112)]
    [InlineData(100, 250, 100)]
    [InlineData(0, 0, 0)]
    [InlineData(400, 500, 400)]
    public void EdgeScrollingStopsAtBothEnds(double offset, double pointer, double expected)
    {
        Assert.Equal(expected, TabDragLayout.ScrollOffset(offset, 400, pointer, 40, 460));
    }

    [Fact]
    public void ScrollingReachesTabsBeyondTheOriginalViewport()
    {
        var slots = Enumerable.Range(0, 10).Select(i => new TabDragSlot(i * 100, 100)).ToArray();
        // A pointer held at the right edge of a 300px viewport follows the content
        // as the viewport scrolls 700px, reaching the final tab.
        Assert.Equal(9, TabDragLayout.Resolve(slots, 0, 0, 200 + 700).Index);
        Assert.Equal(0, TabDragLayout.Resolve(slots, 9, 0, 0).Index);
    }

    [Theory]
    [InlineData(-1000, 0, 0)]
    [InlineData(220, 220, 1)]
    [InlineData(1000, 440, 2)]
    public void PreviewStaysWithinOccupiedTabs(double requestedLeft, double left, int target)
    {
        Assert.Equal(new TabDragPlacement(left, target), TabDragLayout.Resolve(Slots, 1, 0, requestedLeft));
    }

    [Fact]
    public void PinnedTabsKeepTheirSpace()
    {
        Assert.Equal(new TabDragPlacement(220, 1), TabDragLayout.Resolve(Slots, 2, 1, -1000));
        Assert.Equal(0, TabDragLayout.Offset(0, 2, 1, 220));
    }

    [Fact]
    public void SingleTabCannotLeaveItsSlot()
    {
        TabDragSlot[] slots = [new(12, 180)];
        Assert.Equal(new TabDragPlacement(12, 0), TabDragLayout.Resolve(slots, 0, 0, 999));
    }

    [Fact]
    public void DpiRoundingDoesNotPreventMovingToTheLastSlot()
    {
        TabDragSlot[] slots = [new(0, 220.075485), new(220.075485, 220.075485), new(440.150970, 220.075470)];
        Assert.Equal(2, TabDragLayout.Resolve(slots, 1, 0, 1000).Index);
    }

    [Theory]
    [InlineData(0, 440, 2)]
    [InlineData(2, 0, 0)]
    public void SettledPreviewAndNeighboursFillEachSlotExactlyOnce(int source, double left, int target)
    {
        var placement = TabDragLayout.Resolve(Slots, source, 0, left);
        Assert.Equal(target, placement.Index);
        var occupied = Slots.Select((slot, index) => index == source
            ? placement.Left
            : slot.Left + TabDragLayout.Offset(index, source, placement.Index, Slots[source].Width))
            .Order().ToArray();
        Assert.Equal(new double[] { 0, 220, 440 }, occupied);
    }
}
