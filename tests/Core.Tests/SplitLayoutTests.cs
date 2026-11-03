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
    public void ConstructingFromTreePreservesNestedRowSplit()
    {
        var root = new SplitLayoutBranch<string>(
            SplitOrientation.Columns,
            [
                new SplitLayoutLeaf<string>("left"),
                new SplitLayoutBranch<string>(
                    SplitOrientation.Rows,
                    [
                        new SplitLayoutLeaf<string>("top-right"),
                        new SplitLayoutLeaf<string>("bottom-right"),
                    ]),
            ]);

        var layout = new SplitLayout<string>(root);

        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        var rows = Assert.IsType<SplitLayoutBranch<string>>(columns.Children[1]);
        Assert.Equal(SplitOrientation.Rows, rows.Orientation);
        Assert.Equal(["left", "top-right", "bottom-right"], layout.Values);
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

    [Fact]
    public void SplittingAPaneDownKeepsTheResizedColumns()
    {
        var layout = new SplitLayout<string>("left");
        layout.Split("left", "right", SplitDirection.Right);
        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        columns.SetSizes([300, 700]);

        layout.Split("right", "below", SplitDirection.Down);

        Assert.Same(columns, layout.Root);
        Assert.Equal([300.0, 700.0], columns.Sizes);
        var rows = Assert.IsType<SplitLayoutBranch<string>>(columns.Children[1]);
        Assert.Empty(rows.Sizes);
    }

    [Fact]
    public void SplittingIntoAResizedBranchSharesItEqually()
    {
        var layout = new SplitLayout<string>("a");
        layout.Split("a", "b", SplitDirection.Right);
        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        columns.SetSizes([300, 700]);

        layout.Split("b", "c", SplitDirection.Right);

        Assert.Empty(columns.Sizes);
    }

    [Fact]
    public void ClosingAPaneKeepsTheOthersInProportion()
    {
        var layout = new SplitLayout<string>("a");
        layout.Split("a", "b", SplitDirection.Right);
        layout.Split("b", "c", SplitDirection.Right);
        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        columns.SetSizes([200, 300, 500]);

        layout.Remove("b");

        Assert.Equal([200.0, 500.0], columns.Sizes);
    }

    [Fact]
    public void ClosingTheLastPaneOfANestedSplitLeavesTheOuterSizes()
    {
        var layout = new SplitLayout<string>("left");
        layout.Split("left", "right", SplitDirection.Right);
        layout.Split("right", "below", SplitDirection.Down);
        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        columns.SetSizes([300, 700]);

        layout.Remove("below");

        Assert.Equal([300.0, 700.0], columns.Sizes);
        Assert.Equal(["left", "right"], layout.Values);
    }

    [Fact]
    public void ABranchFlattenedIntoItsParentKeepsWhereItsPanesWere()
    {
        var layout = new SplitLayout<string>("x");
        layout.Split("x", "a", SplitDirection.Right);
        layout.Split("a", "c", SplitDirection.Down);
        layout.Split("a", "b", SplitDirection.Right);
        var outer = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);
        outer.SetSizes([400, 600]);
        var rows = Assert.IsType<SplitLayoutBranch<string>>(outer.Children[1]);
        var inner = Assert.IsType<SplitLayoutBranch<string>>(rows.Children[0]);
        inner.SetSizes([1, 2]);

        layout.Remove("c");

        Assert.Equal(["x", "a", "b"], layout.Values);
        Assert.Equal([400.0, 200.0, 400.0], outer.Sizes);
    }

    [Fact]
    public void SizesThatDoNotFitTheChildrenMeanEqualShares()
    {
        var layout = new SplitLayout<string>("a");
        layout.Split("a", "b", SplitDirection.Right);
        var columns = Assert.IsType<SplitLayoutBranch<string>>(layout.Root);

        columns.SetSizes([1, 2, 3]);
        Assert.Empty(columns.Sizes);
        columns.SetSizes([1, 0]);
        Assert.Empty(columns.Sizes);
        columns.SetSizes([1, double.NaN]);
        Assert.Empty(columns.Sizes);
    }

    [Theory]
    [InlineData(1, 50, SplitDirection.Left)]
    [InlineData(99, 50, SplitDirection.Right)]
    [InlineData(50, 1, SplitDirection.Up)]
    [InlineData(50, 99, SplitDirection.Down)]
    public void DropTargetUsesTheNearestEdge(double x, double y, SplitDirection expected) =>
        Assert.Equal(expected, SplitDropTarget.Resolve(x, y, 100, 100));
}
