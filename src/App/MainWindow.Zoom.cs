using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Layout;
using Resesh.Core.Storage;

namespace Resesh.App;

// Tab zoom: one tab fills the window in a single pane holding every tab, and the split
// layout comes back when the zoom ends. The panes' tabs, selections and sizes are
// snapshotted; anything that changes the layout itself ends the zoom first.
public sealed partial class MainWindow
{
    /// <summary>The split layout a zoom replaced. <see cref="Group"/> holds every tab while
    /// zoomed and was pane <see cref="HomeIndex"/> of the snapshot.</summary>
    private sealed record ZoomState(
        TabGroupViewModel Group,
        int HomeIndex,
        TabZoomSnapshot<TabViewModel> Tabs,
        WorkspaceLayoutNode Layout);

    private ZoomState? _zoom;

    public bool IsZoomed => _zoom is not null;

    public void ToggleZoom(TabViewModel tab)
    {
        if (_zoom is null)
            ZoomTab(tab);
        else
            Unzoom();
    }

    /// <summary>Gathers every pane's tabs into the tab's own pane and shows it alone.</summary>
    public void ZoomTab(TabViewModel tab)
    {
        if (_zoom is not null || !ViewModel.IsSplit || !ViewModel.AllTabs.Contains(tab))
            return;

        var groups = _groupLayout.Values.ToList();
        var indices = groups.Select((group, index) => (group, index))
            .ToDictionary(item => item.group, item => item.index);
        var target = ViewModel.GroupOf(tab);
        var snapshot = new TabZoomSnapshot<TabViewModel>(
            [.. groups.Select(group => (IReadOnlyList<TabViewModel>)[.. group.Tabs])],
            [.. groups.Select(group => group.SelectedTab)],
            groups.IndexOf(ViewModel.FocusedGroup));
        // Sizes are read from the live grids, before the rebuild discards them.
        var layout = CaptureWorkspaceLayoutNode(
            _groupLayout.Root, indices, GroupArea.Children.FirstOrDefault() as FrameworkElement);

        foreach (var groupView in _groupViews.Values)
            groupView.SuppressTabMotion();
        var merged = TabZoom.Merge(snapshot.Groups, candidate => candidate.IsPinned);
        for (var index = 0; index < merged.Count; index++)
            PlaceZoomTab(merged[index], target, index);
        target.SelectedTab = tab;

        foreach (var group in groups.Where(group => group != target))
            DropGroupView(group);
        _groupLayout = new SplitLayout<TabGroupViewModel>(target);
        _zoom = new ZoomState(target, indices[target], snapshot, layout);
        ViewModel.IsZoomed = true;

        FinishLayoutChange(target, tab);
    }

    /// <summary>Puts every tab back into the pane it came from, at the sizes it had.</summary>
    public void Unzoom()
    {
        if (_zoom is not { } zoom)
            return;
        EndZoomState();

        var zoomed = zoom.Group;
        var restore = TabZoom.Plan(
            zoom.Tabs, [.. zoomed.Tabs], zoomed.SelectedTab, zoom.HomeIndex, tab => tab.IsPinned);
        var surviving = Enumerable.Range(0, restore.Groups.Count)
            .Where(index => restore.Groups[index].Count > 0)
            .ToList();
        foreach (var groupView in _groupViews.Values)
            groupView.SuppressTabMotion();
        if (surviving.Count <= 1)
        {
            // Only one pane still has tabs: the single pane is its whole layout.
            if (surviving.Count == 1)
            {
                var tabs = restore.Groups[surviving[0]];
                for (var position = 0; position < tabs.Count; position++)
                    PlaceZoomTab(tabs[position], zoomed, position);
                zoomed.SelectedTab = restore.Selected[surviving[0]];
            }
            FinishLayoutChange(zoomed, zoomed.SelectedTab);
            return;
        }

        // The zoomed pane returns to its own place, or stands in for the first survivor.
        var zoomedIndex = surviving.Contains(zoom.HomeIndex) ? zoom.HomeIndex : surviving[0];
        var groupFor = new Dictionary<int, TabGroupViewModel>();
        foreach (var index in surviving)
        {
            if (index == zoomedIndex)
            {
                groupFor[index] = zoomed;
                continue;
            }
            var group = new TabGroupViewModel();
            ViewModel.Groups.Add(group);
            AttachGroupView(group);
            groupFor[index] = group;
        }

        foreach (var index in surviving)
        {
            var tabs = restore.Groups[index];
            for (var position = 0; position < tabs.Count; position++)
                PlaceZoomTab(tabs[position], groupFor[index], position);
        }
        foreach (var index in surviving)
            groupFor[index].SelectedTab = restore.Selected[index];

        var remap = surviving.Select((original, position) => (original, position))
            .ToDictionary(item => item.original, item => item.position);
        var layout = WorkspaceLayoutTransform.Remap(zoom.Layout, remap)!;
        RebuildWorkspaceSplitLayout([.. surviving.Select(index => groupFor[index])], layout);

        var focused = groupFor.GetValueOrDefault(restore.FocusedGroup) ?? groupFor[surviving[0]];
        FinishLayoutChange(focused, focused.SelectedTab);
    }

    /// <summary>Called before anything rebuilds the panes (a split, a workspace): the
    /// snapshot would no longer describe the layout, so the zoom ends first.</summary>
    private void EndZoomForLayoutChange()
    {
        if (_zoom is not null)
            Unzoom();
    }

    /// <summary>The zoomed pane lost its last tab: there is nothing left to put back.</summary>
    private void EndZoomIfEmpty(TabGroupViewModel group)
    {
        if (_zoom is { } zoom && ReferenceEquals(zoom.Group, group) && group.Tabs.Count == 0)
        {
            EndZoomState();
            RefreshTabGroupActions();
        }
    }

    private void EndZoomState()
    {
        _zoom = null;
        ViewModel.IsZoomed = false;
    }

    /// <summary>The layout a save would see: the split the zoom will return to.</summary>
    private WorkspaceLayout CaptureZoomedWorkspaceLayout(ZoomState zoom)
    {
        var zoomed = zoom.Group;
        var restore = TabZoom.Plan(
            zoom.Tabs, [.. zoomed.Tabs], zoomed.SelectedTab, zoom.HomeIndex, tab => tab.IsPinned);
        var surviving = Enumerable.Range(0, restore.Groups.Count)
            .Where(index => restore.Groups[index].Count > 0)
            .ToList();
        var remap = surviving.Select((original, position) => (original, position))
            .ToDictionary(item => item.original, item => item.position);
        return new WorkspaceLayout
        {
            Groups = [.. surviving.Select(index => CaptureWorkspaceGroup(restore.Groups[index], restore.Selected[index]))],
            Layout = WorkspaceLayoutTransform.Remap(zoom.Layout, remap),
        };
    }

    /// <summary>Moves a tab (and its view) to a position in a group, keeping the selection
    /// the caller sets afterwards.</summary>
    private void PlaceZoomTab(TabViewModel tab, TabGroupViewModel target, int index)
    {
        var source = ViewModel.GroupOf(tab);
        if (ReferenceEquals(source, target))
        {
            var current = target.Tabs.IndexOf(tab);
            if (current != index)
                target.Tabs.Move(current, Math.Clamp(index, 0, target.Tabs.Count - 1));
            return;
        }

        source.RemoveTab(tab);
        target.Tabs.Insert(Math.Clamp(index, 0, target.Tabs.Count), tab);
        if (tab.View is UIElement view)
        {
            _groupViews[source].RemoveTerminal(view);
            _groupViews[target].AddTerminal(view);
        }
    }

    private void DropGroupView(TabGroupViewModel group)
    {
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_groupViews[group]) is Panel parent)
            parent.Children.Remove(_groupViews[group]);
        _groupViews.Remove(group);
        ViewModel.Groups.Remove(group);
    }

    private void FinishLayoutChange(TabGroupViewModel focused, TabViewModel? tab)
    {
        SyncGroupOrder();
        ViewModel.OnGroupsChanged();
        RebuildGroupLayout();
        FocusGroup(focused);
        ViewModel.SyncGroupFocus();
        if (tab is not null)
            FocusTabContent(tab);
        // Panes created for the restore select their tab once they load, and a selection
        // change focuses its pane. Take focus back after they settle.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ViewModel.Groups.Contains(focused) || ReferenceEquals(ViewModel.FocusedGroup, focused))
                return;
            FocusGroup(focused);
            ViewModel.SyncGroupFocus();
            if (focused.SelectedTab is { } selected)
                FocusTabContent(selected);
        });
    }

    private void RefreshTabGroupActions()
    {
        foreach (var groupView in _groupViews.Values)
            groupView.RefreshLayoutActions();
    }
}
