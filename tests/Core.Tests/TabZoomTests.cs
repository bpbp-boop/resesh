using Resesh.Core.Layout;

namespace Resesh.Core.Tests;

public class TabZoomTests
{
    private sealed class Tab(string name, bool pinned = false)
    {
        public string Name { get; } = name;
        public bool Pinned { get; } = pinned;
        public override string ToString() => Name;
    }

    private static readonly Tab A = new("a"), B = new("b"), C = new("c"), D = new("d"), E = new("e");

    private static TabZoomSnapshot<Tab> Snapshot(int focused, params (Tab[] Tabs, Tab? Selected)[] groups) =>
        new([.. groups.Select(group => (IReadOnlyList<Tab>)group.Tabs)], [.. groups.Select(group => group.Selected)], focused);

    private static TabZoomRestore<Tab> Plan(TabZoomSnapshot<Tab> snapshot, Tab[] current, Tab? active, int home = 0) =>
        TabZoom.Plan(snapshot, current, active, home, tab => tab.Pinned);

    private static List<string[]> Names(TabZoomRestore<Tab> restore) =>
        [.. restore.Groups.Select(group => group.Select(tab => tab.Name).ToArray())];

    [Fact]
    public void MergeKeepsPaneOrderWithPinnedTabsFirst()
    {
        var pinned = new Tab("p", pinned: true);
        var merged = TabZoom.Merge<Tab>([[A, B], [pinned, C]], tab => tab.Pinned);

        Assert.Equal([pinned, A, B, C], merged);
    }

    [Fact]
    public void UnchangedZoomRestoresEveryPane()
    {
        var snapshot = Snapshot(1, ([A, B], B), ([C], C), ([D, E], D));

        var restore = Plan(snapshot, [A, B, C, D, E], active: C, home: 1);

        Assert.Equal([["a", "b"], ["c"], ["d", "e"]], Names(restore));
        Assert.Equal([B, C, D], restore.Selected);
        Assert.Equal(1, restore.FocusedGroup);
    }

    [Fact]
    public void ReorderingWhileZoomedDoesNotReorderPanes()
    {
        var snapshot = Snapshot(0, ([A, B], A), ([C, D], C));

        var restore = Plan(snapshot, [D, B, C, A], active: A);

        Assert.Equal([["a", "b"], ["c", "d"]], Names(restore));
    }

    [Fact]
    public void ActiveTabAtUnzoomIsSelectedAndItsPaneFocused()
    {
        var snapshot = Snapshot(0, ([A, B], A), ([C, D], C));

        var restore = Plan(snapshot, [A, B, C, D], active: D);

        Assert.Equal(D, restore.Selected[1]);
        Assert.Equal(A, restore.Selected[0]);
        Assert.Equal(1, restore.FocusedGroup);
    }

    [Fact]
    public void ClosedTabsDropOutAndAPaneCanEmpty()
    {
        var snapshot = Snapshot(0, ([A, B], B), ([C], C), ([D], D));

        var restore = Plan(snapshot, [A, D], active: A);

        Assert.Equal([["a"], [], ["d"]], Names(restore));
        Assert.Equal([A, null, D], restore.Selected);
    }

    [Fact]
    public void ClosedSelectionFallsToTheNextSurvivorThenThePrevious()
    {
        var snapshot = Snapshot(0, ([A, B, C], B), ([D, E], E));

        var restore = Plan(snapshot, [A, C, D], active: null);

        Assert.Equal(C, restore.Selected[0]);
        Assert.Equal(D, restore.Selected[1]);
    }

    [Fact]
    public void NewTabJoinsThePaneOfTheTabBeforeIt()
    {
        var clone = new Tab("clone");
        var snapshot = Snapshot(0, ([A, B], A), ([C, D], C));

        var restore = Plan(snapshot, [A, B, C, clone, D], active: clone);

        Assert.Equal([["a", "b"], ["c", "clone", "d"]], Names(restore));
        Assert.Equal(clone, restore.Selected[1]);
        Assert.Equal(1, restore.FocusedGroup);
    }

    [Fact]
    public void RunOfNewTabsStaysTogetherInOrder()
    {
        var first = new Tab("n1");
        var second = new Tab("n2");
        var snapshot = Snapshot(0, ([A], A), ([C], C));

        var restore = Plan(snapshot, [A, first, second, C], active: C);

        Assert.Equal([["a", "n1", "n2"], ["c"]], Names(restore));
    }

    [Fact]
    public void LeadingNewTabJoinsThePaneOfTheTabAfterIt()
    {
        var added = new Tab("new");
        var snapshot = Snapshot(0, ([A], A), ([C], C));

        var restore = Plan(snapshot, [added, C, A], active: A);

        Assert.Equal([["a"], ["new", "c"]], Names(restore));
    }

    [Fact]
    public void NewTabsWithNoSurvivorsGoToTheHomePane()
    {
        var added = new Tab("new");
        var snapshot = Snapshot(0, ([A], A), ([C], C));

        var restore = Plan(snapshot, [added], active: added, home: 1);

        Assert.Equal([[], ["new"]], Names(restore));
        Assert.Equal(1, restore.FocusedGroup);
    }

    [Fact]
    public void TabPinnedWhileZoomedLeadsItsPane()
    {
        var pinnedLater = new Tab("c", pinned: true);
        var snapshot = Snapshot(0, ([A], A), ([D, pinnedLater], D));

        // Pin state is read from the tab now, so a record standing in for "c" after pinning.
        var restore = TabZoom.Plan(snapshot, [A, pinnedLater, D], A, 0, tab => tab.Pinned);

        Assert.Equal([["a"], ["c", "d"]], Names(restore));
    }

    [Fact]
    public void EmptyStripFocusesTheHomePane()
    {
        var snapshot = Snapshot(1, ([A], A), ([C], C));

        var restore = Plan(snapshot, [], active: null, home: 0);

        Assert.All(restore.Groups, group => Assert.Empty(group));
        Assert.Equal(0, restore.FocusedGroup);
    }

    [Fact]
    public void FocusFallsToAPaneWithTabsWhenTheFocusedPaneEmptied()
    {
        var snapshot = Snapshot(0, ([A], A), ([C], C));

        var restore = Plan(snapshot, [C], active: null);

        Assert.Equal(1, restore.FocusedGroup);
    }
}
