using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Resesh.App.Controls;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Layout;
using Resesh.Core.Recording;
using Resesh.Core.Storage;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Resesh.App;

public sealed partial class MainWindow
{
    private WorkspaceCoordinator WorkspaceOperations => new(App.Workspaces, CaptureWorkspaceLayout,
        App.RefreshWorkspaceMenus, (title, message) =>
            App.ReportRecoverableError(new IOException($"{title}: {message}")));

    private WorkspaceLayout CaptureWorkspaceLayout()
    {
        if (_zoom is { } zoom)
            return CaptureZoomedWorkspaceLayout(zoom);

        var groups = _groupLayout.Values.ToList();
        var groupIndices = groups
            .Select((group, index) => (group, index))
            .ToDictionary(item => item.group, item => item.index);
        return new WorkspaceLayout
        {
            Groups = groups.Select(group => CaptureWorkspaceGroup(group.Tabs, group.SelectedTab)).ToList(),
            Layout = CaptureWorkspaceLayoutNode(
                _groupLayout.Root,
                groupIndices,
                GroupArea.Children.FirstOrDefault() as FrameworkElement),
        };
    }

    private static WorkspaceGroup CaptureWorkspaceGroup(IReadOnlyList<TabViewModel> tabs, TabViewModel? selected)
    {
        var originalActiveIndex = selected is null ? -1 : tabs.ToList().IndexOf(selected);
        var captured = tabs
            .Select((tab, originalIndex) => (Tab: tab, OriginalIndex: originalIndex))
            .Where(item => !item.Tab.IsPlayback && App.Store.Find(item.Tab.Session.Id) is not null)
            .ToList();
        var activeIndex = captured.FindIndex(item => item.OriginalIndex == originalActiveIndex);
        if (activeIndex < 0 && captured.Count > 0 && originalActiveIndex >= 0)
        {
            activeIndex = Math.Clamp(
                captured.Count(item => item.OriginalIndex < originalActiveIndex),
                0,
                captured.Count - 1);
        }

        return new WorkspaceGroup
        {
            Tabs = captured.Select(item => new WorkspaceTabReference
            {
                SessionId = item.Tab.Session.Id,
                Pinned = item.Tab.IsPinned,
            }).ToList(),
            ActiveTabIndex = Math.Max(activeIndex, 0),
        };
    }

    private WorkspaceLayoutNode CaptureWorkspaceLayoutNode(
        SplitLayoutNode<TabGroupViewModel> node,
        IReadOnlyDictionary<TabGroupViewModel, int> groupIndices,
        FrameworkElement? element) =>
        node switch
        {
            SplitLayoutLeaf<TabGroupViewModel> leaf => new WorkspaceLayoutNode
            {
                GroupIndex = groupIndices[leaf.Value],
            },
            SplitLayoutBranch<TabGroupViewModel> branch => CaptureWorkspaceLayoutBranch(
                branch, groupIndices, element),
            _ => throw new InvalidOperationException("Unknown split layout node."),
        };

    private WorkspaceLayoutNode CaptureWorkspaceLayoutBranch(
        SplitLayoutBranch<TabGroupViewModel> branch,
        IReadOnlyDictionary<TabGroupViewModel, int> groupIndices,
        FrameworkElement? element)
    {
        var grid = element as Grid;
        var isColumns = branch.Orientation == SplitOrientation.Columns;
        IReadOnlyList<double> sizes = grid is null
            ? []
            : branch.Children.Select((_, index) => isColumns
                ? grid.ColumnDefinitions[index * 2].ActualWidth
                : grid.RowDefinitions[index * 2].ActualHeight).ToList();
        if (sizes.Count != branch.Children.Count || sizes.Any(size => !double.IsFinite(size) || size <= 0))
            sizes = [];

        return new WorkspaceLayoutNode
        {
            Orientation = branch.Orientation,
            Sizes = sizes,
            Children = branch.Children
                .Select((child, index) => CaptureWorkspaceLayoutNode(
                    child,
                    groupIndices,
                    grid is null ? null : (FrameworkElement)grid.Children[index * 3]))
                .ToList(),
        };
    }

    internal void RefreshWorkspaceMenu()
    {
        Workspaces.Clear();
        foreach (var workspace in App.Workspaces.Workspaces)
            Workspaces.Add(WorkspaceItemViewModel.FromWorkspace(workspace));
        NoWorkspacesState.Visibility = Workspaces.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var menuItems = new List<MenuFlyoutItemBase>
        {
            new MenuFlyoutItem { Text = "Save current layout as workspace…", Command = ViewModel.SaveWorkspaceAsCommand },
            new MenuFlyoutSeparator(),
        };

        if (App.Workspaces.Workspaces.Count == 0)
        {
            menuItems.Add(new MenuFlyoutItem
            {
                Text = "No saved workspaces",
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var workspace in App.Workspaces.Workspaces)
            {
                MenuFlyoutItem Item(string text, System.Windows.Input.ICommand command) =>
                    new() { Text = text, Command = command, CommandParameter = workspace };
                var submenu = new MenuFlyoutSubItem
                {
                    Text = workspace.Name,
                    Items =
                    {
                        Item("Show", ViewModel.OpenWorkspaceCommand),
                        Item("Open in new window", ViewModel.OpenWorkspaceInNewWindowCommand),
                        Item("Add to current layout", ViewModel.OpenWorkspaceAdditivelyCommand),
                        new MenuFlyoutSeparator(),
                        Item("Update from current layout…", ViewModel.UpdateWorkspaceCommand),
                        Item("Icon…", ViewModel.CustomizeWorkspaceCommand),
                        Item("Rename…", ViewModel.RenameWorkspaceCommand),
                        Item("Delete…", ViewModel.DeleteWorkspaceCommand),
                    },
                };
                if (IsWorkspaceOpen(workspace.Id))
                {
                    submenu.Items.Insert(3, Item("Close", ViewModel.CloseWorkspaceCommand));
                    foreach (var entry in SaveWorkspaceMenuItems(workspace).Reverse())
                        submenu.Items.Insert(3, entry);
                }
                menuItems.Add(submenu);
            }
        }

        while (WorkspacesMenu.Items.Count > 0)
            WorkspacesMenu.Items.RemoveAt(WorkspacesMenu.Items.Count - 1);
        foreach (var menuItem in menuItems)
            WorkspacesMenu.Items.Add(menuItem);
        RefreshWorkspaceRail();
    }
    private void WorkspaceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WorkspaceItemViewModel item)
            ViewModel.OpenWorkspaceCommand.Execute(item.Workspace);
    }

    private void WorkspaceList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        WorkspaceOperations.Reorder(Workspaces.Select(item => item.Workspace.Id).ToList());
    }

    /// <summary>Runs a workspace row's menu command. The row template cannot bind to the
    /// window's view model, so each item names its command in Tag.</summary>
    private void WorkspaceRowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: WorkspaceItemViewModel item, Tag: string action })
            return;
        System.Windows.Input.ICommand command = action switch
        {
            "open" => ViewModel.OpenWorkspaceCommand,
            "newWindow" => ViewModel.OpenWorkspaceInNewWindowCommand,
            "additive" => ViewModel.OpenWorkspaceAdditivelyCommand,
            "update" => ViewModel.UpdateWorkspaceCommand,
            "customize" => ViewModel.CustomizeWorkspaceCommand,
            "rename" => ViewModel.RenameWorkspaceCommand,
            "delete" => ViewModel.DeleteWorkspaceCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(sender), action, "Unknown workspace action."),
        };
        command.Execute(item.Workspace);
    }


    /// <summary>Home's tabs become the new workspace, as the rail's + button does. A saved
    /// workspace's layout is copied into a new one, which opens its own connections.</summary>
    private async Task SaveCurrentWorkspaceAsAsync()
    {
        var name = (await PromptAsync("Save Workspace", "Workspace name", ""))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        if (_shownWorkspace == HomeWorkspace)
            AdoptShownTabsAsWorkspace(name);
        else
            WorkspaceOperations.SaveAs(name);
    }

    private async Task RenameWorkspaceAsync(Workspace workspace)
    {
        var name = (await PromptAsync("Rename Workspace", "Workspace name", workspace.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == workspace.Name)
            return;

        WorkspaceOperations.Rename(workspace.Id, name);
    }

    private async Task UpdateWorkspaceAsync(Workspace workspace)
    {
        if (!await ConfirmAsync(
            "Update Workspace?",
            $"Replace the saved layout in \"{workspace.Name}\" with the current tab groups?",
            "Update"))
        {
            return;
        }

        WorkspaceOperations.Update(workspace.Id);
    }

    private async Task DeleteWorkspaceAsync(Workspace workspace)
    {
        var open = App.WindowWithWorkspace(workspace.Id, except: null) is not null;
        if (!await ConfirmAsync(
            "Delete Workspace?",
            $"Delete \"{workspace.Name}\"? Saved sessions are not deleted."
                + (open ? " Its open tabs close." : ""),
            "Delete"))
        {
            return;
        }

        App.DiscardWorkspaceInAllWindows(workspace.Id);
        WorkspaceOperations.Delete(workspace.Id);
    }

    /// <summary>Shows the workspace, or adds its tabs to the shown layout. Showing keeps the
    /// current workspace's tabs connected in the background, so nothing needs confirming.</summary>
    private Task OpenWorkspaceAsync(Workspace workspace, bool additive)
    {
        if (!additive)
        {
            ShowWorkspace(workspace.Id);
            return Task.CompletedTask;
        }

        ApplyWorkspaceLayout(new WorkspaceLayout
        {
            Groups = workspace.Groups,
            Layout = workspace.Layout,
        }, additive: true);
        return Task.CompletedTask;
    }

    /// <summary>A workspace already open somewhere is shown where it is, rather than opening
    /// a second copy of its connections.</summary>
    private static void OpenWorkspaceInNewWindow(Workspace workspace)
    {
        if (App.WindowWithWorkspace(workspace.Id, except: null) is { } owner)
        {
            owner.ShowWorkspace(workspace.Id);
            owner.BringToFront();
            return;
        }
        App.OpenNewWindow().ShowWorkspace(workspace.Id);
    }

    /// <summary>Launch-time restoration: the Home layout, then the workspace the window was
    /// showing when it closed. Home's tabs connect when Home is first shown.</summary>
    public void RestoreLastLayout()
    {
        if (!App.Settings.Current.ReopenLastLayoutAtStartup)
            return;
        if (App.Workspaces.LastLayout is { } layout)
            ApplyWorkspaceLayout(layout, additive: false);
        if (App.Workspaces.LastActiveWorkspaceId is { } active)
            ShowWorkspace(active, keepDraft: false, animate: false); // startup: no motion
    }

    private void ApplyWorkspaceLayout(WorkspaceLayout layout, bool additive)
    {
        EndZoomForLayoutChange();
        // Tabs are placed, moved and closed wholesale; the strips snap to the result.
        foreach (var groupView in _groupViews.Values)
            groupView.SuppressTabMotion();
        WorkspaceNotice.IsOpen = false;
        var sourceGroups = layout.Groups.Count == 0
            ? [new WorkspaceGroup()]
            : layout.Groups.ToList();
        var originalLayout = additive ? CaptureWorkspaceLayout().Layout : null;
        var originalGroups = _groupLayout.Values.ToList();
        // Only the shown workspace's tabs are adopted or replaced; parked ones stay put.
        var originalTabs = ViewModel.ShownTabs.ToList();
        var availableTabs = originalTabs
            .Where(tab => !tab.IsPlayback)
            .GroupBy(tab => tab.Session.Id)
            .ToDictionary(group => group.Key, group => new Queue<TabViewModel>(group));
        var usedTabs = new HashSet<TabViewModel>();
        var targetGroups = new List<TabGroupViewModel>(sourceGroups.Count);

        if (additive)
        {
            if (layout.Groups.Count == 0)
                return;
            var after = _groupLayout.Values[^1];
            foreach (var _ in sourceGroups)
            {
                after = CreateWorkspaceGroupAfter(after);
                targetGroups.Add(after);
            }
        }
        else
        {
            var after = _groupLayout.Values[0];
            targetGroups.Add(after);
            for (var index = 1; index < sourceGroups.Count; index++)
            {
                after = CreateWorkspaceGroupAfter(after);
                targetGroups.Add(after);
            }
        }

        var missing = new List<Guid>();
        for (var groupIndex = 0; groupIndex < sourceGroups.Count; groupIndex++)
        {
            var savedGroup = sourceGroups[groupIndex];
            var targetGroup = targetGroups[groupIndex];
            var placed = new List<(TabViewModel Tab, int OriginalIndex)>();

            for (var tabIndex = 0; tabIndex < savedGroup.Tabs.Count; tabIndex++)
            {
                var savedTab = savedGroup.Tabs[tabIndex];
                if (App.Store.Find(savedTab.SessionId) is not { } session)
                {
                    missing.Add(savedTab.SessionId);
                    continue;
                }

                TabViewModel tab;
                if (availableTabs.TryGetValue(savedTab.SessionId, out var candidates)
                    && candidates.Count > 0)
                {
                    tab = candidates.Dequeue();
                    PlaceWorkspaceTab(tab, targetGroup, placed.Count);
                }
                else
                {
                    tab = ConnectSession(session, targetGroup, trackRecent: false);
                }

                tab.IsPinned = savedTab.Pinned;
                usedTabs.Add(tab);
                placed.Add((tab, tabIndex));
            }

            if (placed.Count > 0)
            {
                // Preserve the saved active tab when it survived. If it was deleted, use
                // its repaired position among survivors (the next tab, or the last at end).
                var activeIndex = placed.FindIndex(item => item.OriginalIndex == savedGroup.ActiveTabIndex);
                if (activeIndex < 0)
                {
                    activeIndex = Math.Clamp(
                        placed.Count(item => item.OriginalIndex < savedGroup.ActiveTabIndex),
                        0,
                        placed.Count - 1);
                }
                targetGroup.SelectedTab = placed[activeIndex].Tab;
            }
            else
            {
                targetGroup.SelectedTab = null;
            }
        }

        if (!additive)
        {
            foreach (var tab in originalTabs.Where(tab => !usedTabs.Contains(tab)))
                RemoveWorkspaceTab(tab);
            foreach (var group in originalGroups.Where(group => !targetGroups.Contains(group)))
                RemoveWorkspaceGroup(group);
        }
        else
        {
            foreach (var group in originalGroups.Where(group => group.Tabs.Count == 0).ToList())
                RemoveWorkspaceGroup(group);
        }

        var savedLayout = layout.Layout ?? CreateColumnWorkspaceLayout(sourceGroups.Count);
        if (additive)
        {
            var targetSet = targetGroups.ToHashSet();
            var existingGroups = _groupLayout.Values.Where(group => !targetSet.Contains(group)).ToList();
            var existingIndices = existingGroups
                .Select((group, index) => (group, index))
                .ToDictionary(item => item.group, item => item.index);
            var survivingIndices = originalGroups.Select((group, index) => (group, index))
                .Where(item => existingIndices.ContainsKey(item.group))
                .ToDictionary(item => item.index, item => existingIndices[item.group]);
            var existingLayout = originalLayout is null ? null
                : WorkspaceLayoutTransform.Remap(originalLayout, survivingIndices);
            var combinedGroups = existingGroups.Concat(targetGroups).ToList();
            var addedLayout = WorkspaceLayoutTransform.Offset(savedLayout, existingGroups.Count);
            var combinedLayout = existingLayout is null
                ? addedLayout
                : new WorkspaceLayoutNode
                {
                    Orientation = SplitOrientation.Columns,
                    Children = [existingLayout, addedLayout],
                };
            RebuildWorkspaceSplitLayout(combinedGroups, combinedLayout);
        }
        else
        {
            RebuildWorkspaceSplitLayout(targetGroups, savedLayout);
        }

        SyncGroupOrder();
        ViewModel.OnGroupsChanged();
        RebuildGroupLayout();
        FocusGroup(targetGroups.FirstOrDefault(group => group.Tabs.Count > 0) ?? targetGroups[0]);
        ViewModel.SyncGroupFocus();
        App.SavePinnedSessions();

        if (missing.Count > 0)
        {
            var ids = string.Join(", ", missing.Distinct().Select(id => id.ToString()));
            ShowWorkspaceNotice(
                "Workspace opened with missing sessions",
                $"{missing.Count} saved session reference(s) no longer exist and were skipped: {ids}");
        }
    }

    private TabGroupViewModel CreateWorkspaceGroupAfter(TabGroupViewModel after)
    {
        var group = new TabGroupViewModel();
        _groupLayout.Split(after, group, SplitDirection.Right);
        ViewModel.Groups.Add(group);
        AttachGroupView(group);
        return group;
    }
    private static WorkspaceLayoutNode CreateColumnWorkspaceLayout(int groupCount) =>
        groupCount == 1
            ? new WorkspaceLayoutNode { GroupIndex = 0 }
            : new WorkspaceLayoutNode
            {
                Orientation = SplitOrientation.Columns,
                Children = Enumerable.Range(0, groupCount)
                    .Select(index => new WorkspaceLayoutNode { GroupIndex = index })
                    .ToList(),
            };

    private void RebuildWorkspaceSplitLayout(
        IReadOnlyList<TabGroupViewModel> groups,
        WorkspaceLayoutNode layout)
    {
        _savedPaneSizes.Clear();
        _groupLayout = new SplitLayout<TabGroupViewModel>(
            BuildWorkspaceSplitNode(layout, groups));
    }

    private SplitLayoutNode<TabGroupViewModel> BuildWorkspaceSplitNode(
        WorkspaceLayoutNode node,
        IReadOnlyList<TabGroupViewModel> groups)
    {
        if (node.Orientation is not { } orientation)
            return new SplitLayoutLeaf<TabGroupViewModel>(groups[node.GroupIndex]);

        var branch = new SplitLayoutBranch<TabGroupViewModel>(
            orientation,
            node.Children.Select(child => BuildWorkspaceSplitNode(child, groups)));
        if (node.Sizes.Count == node.Children.Count)
            _savedPaneSizes[branch] = node.Sizes.ToList();
        return branch;
    }


    private void PlaceWorkspaceTab(TabViewModel tab, TabGroupViewModel target, int index)
    {
        var source = ViewModel.GroupOf(tab);
        source.Tabs.Remove(tab);
        if (source.SelectedTab == tab)
            source.SelectedTab = source.Tabs.LastOrDefault();
        target.Tabs.Insert(Math.Clamp(index, 0, target.Tabs.Count), tab);

        if (source != target && tab.View is UIElement view)
        {
            _groupViews[source].RemoveTerminal(view);
            _groupViews[target].AddTerminal(view);
        }
    }

    private void RemoveWorkspaceTab(TabViewModel tab)
    {
        var group = ViewModel.GroupOf(tab);
        if (tab.View is OnboardingView onboarding)
            onboarding.CancelPreview();
        if (tab.View is UIElement view)
            _groupViews[group].RemoveTerminal(view);
        ViewModel.CloseTab(tab);
    }

    private void RemoveWorkspaceGroup(TabGroupViewModel group)
    {
        if (group.Tabs.Count > 0 || !_groupLayout.Remove(group))
            return;
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_groupViews[group]) is Panel parent)
            parent.Children.Remove(_groupViews[group]);
        _groupViews.Remove(group);
        ViewModel.Groups.Remove(group);
    }

    internal void ShowOperationNotice(string title, string message) => ShowWorkspaceNotice(title, message);

    private void ShowWorkspaceNotice(string title, string message)
    {
        WorkspaceNotice.Title = title;
        WorkspaceNotice.Message = message;
        WorkspaceNotice.IsOpen = true;
    }

}
