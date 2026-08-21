using Resesh.Core.Layout;

namespace Resesh.Core.Tests;

public class SplitLayoutTests
{
    [Fact]
    public void RepeatedColumnSplitsShareOneOrderedBranch()
    {
        var layout = new SplitLayout<string>("a");

        layout.Split("a", "b", SplitDirection.Right);
        layout.Split("b", "c", SplitDirection.Right);
        layout.Split("a", "left", SplitDirection.Left);

        var root = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        Assert.Equal(SplitOrientation.Columns, root.Orientation);
        Assert.Equal(["left", "a", "b", "c"], layout.Values);
        Assert.Equal(4, root.Children.Count);
    }

    [Fact]
    public void RowSplitNestsInsideAColumn()
    {
        var layout = new SplitLayout<string>("left");
        layout.Split("left", "right", SplitDirection.Right);

        layout.Split("right", "below", SplitDirection.Down);

        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        var rows = Assert.IsType<SplitLayoutBranch<string>>(columns.Children[1]);
        Assert.Equal(SplitOrientation.Rows, rows.Orientation);
        Assert.Equal(["right", "below"], rows.Values);
    }

    [Fact]
    public void RemovingALeafCollapsesAndFlattensRedundantBranches()
    {
        var layout = new SplitLayout<string>("a");
        layout.Split("a", "b", SplitDirection.Right);
        layout.Split("b", "below", SplitDirection.Down);
        layout.Split("below", "c", SplitDirection.Right);

        Assert.True(layout.Remove("b"));
        Assert.True(layout.Remove("below"));

        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        Assert.Equal(["a", "c"], layout.Values);
        Assert.Equal(2, columns.Children.Count);
        Assert.False(layout.Remove("missing"));
    }

    [Theory]
    [InlineData(1, 50, SplitDirection.Left)]
    [InlineData(99, 50, SplitDirection.Right)]
    [InlineData(50, 1, SplitDirection.Up)]
    [InlineData(50, 99, SplitDirection.Down)]
    public void DropTargetUsesTheNearestEdge(double x, double y, SplitDirection expected) =>
        Assert.Equal(expected, SplitDropTarget.Resolve(x, y, 100, 100));
}
