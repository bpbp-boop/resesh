using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Resesh.App.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Layout;
using Resesh.Core.Storage;

namespace Resesh.App;

// Workspaces as rail buttons. A window keeps each workspace it opens: the one it shows is
// laid out as usual, and the others are parked with their tabs still connected. Home holds
// the tabs that belong to no saved workspace and is always open.
public sealed partial class MainWindow
{
    /// <summary>A workspace the window holds but does not show: its groups in layout order,
    /// the split that arranges them, the pane sizes it had, and the pane that had focus.</summary>
    private sealed class ParkedWorkspace(
        SplitLayout<TabGroupViewModel> layout,
        Dictionary<SplitLayoutBranch<TabGroupViewModel>, IReadOnlyList<double>> paneSizes,
        TabGroupViewModel focusedGroup)
    {
        public SplitLayout<TabGroupViewModel> Layout { get; } = layout;
        public Dictionary<SplitLayoutBranch<TabGroupViewModel>, IReadOnlyList<double>> PaneSizes { get; } = paneSizes;
        public TabGroupViewModel FocusedGroup { get; set; } = focusedGroup;
        public IReadOnlyList<TabGroupViewModel> Groups => Layout.Values;
        public IEnumerable<TabViewModel> Tabs => Groups.SelectMany(group => group.Tabs);
    }

    /// <summary>Home's key: the unsaved tabs have no workspace id.</summary>
    private static readonly Guid HomeWorkspace = Guid.Empty;

    private readonly Dictionary<Guid, ParkedWorkspace> _parkedWorkspaces = [];
    private Guid _shownWorkspace = HomeWorkspace;
    private bool _railRefreshQueued;
    private WorkspaceRailItemViewModel? _middlePressedRailItem;

    /// <summary>Home's rail button, alone in its list so it shares the workspace template.</summary>
    public ObservableCollection<WorkspaceRailItemViewModel> RailHome { get; } = [WorkspaceRailItemViewModel.CreateHome()];

    /// <summary>One rail button per saved workspace, in saved order.</summary>
    public ObservableCollection<WorkspaceRailItemViewModel> RailWorkspaces { get; } = [];

    /// <summary>The saved workspace this window shows, or null while it shows Home.</summary>
    internal Guid? ShownWorkspaceId => _shownWorkspace == HomeWorkspace ? null : _shownWorkspace;

    internal bool IsWorkspaceOpen(Guid id) =>
        id != HomeWorkspace && (_shownWorkspace == id || _parkedWorkspaces.ContainsKey(id));

    private void InitializeWorkspaceRail()
    {
        // Middle-click closes a workspace, as it closes a tab. The list items handle presses
        // themselves, so listen to handled events too.
        WorkspaceRailList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(WorkspaceRail_PointerPressed), true);
        WorkspaceRailList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(WorkspaceRail_PointerReleased), true);
        InitializeRailToolCards();
        ViewModel.TabChanged += (_, property) =>
        {
            if (property is nameof(TabViewModel.HasUnseenOutput) or nameof(TabViewModel.Agent)
                or nameof(TabViewModel.State) or nameof(TabViewModel.IsPinned))
                QueueWorkspaceRailRefresh();
        };
        // Tabs opening and closing in Home change what the + button would do.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText))
                QueueWorkspaceRailRefresh();
        };
    }

    // ---- showing a workspace ----

    /// <summary>Shows a workspace: Home, one this window has parked, or a saved one it opens
    /// now (from its unsaved draft, if it has one). One open in another window is shown there
    /// instead. The workspace being left is parked with its tabs connected; its changes are
    /// kept as a draft in case the app stops, but its saved layout is not touched.</summary>
    internal void ShowWorkspace(Guid id, bool focusTerminal = true, bool keepDraft = true, bool animate = true)
    {
        if (id == _shownWorkspace)
        {
            if (focusTerminal)
                FocusActiveTerminal();
            return;
        }

        Workspace? saved = null;
        if (id != HomeWorkspace && !_parkedWorkspaces.ContainsKey(id))
        {
            saved = App.Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Id == id);
            if (saved is null)
                return;
            if (App.WindowWithWorkspace(id, except: this) is { } other)
            {
                other.ShowWorkspace(id);
                other.BringToFront();
                return;
            }
        }

        EndZoomForLayoutChange();
        if (keepDraft)
            KeepShownWorkspaceDraft();
        ParkShownWorkspace();
        if (_parkedWorkspaces.Remove(id, out var parked))
        {
            UnparkWorkspace(parked);
        }
        else
        {
            StartEmptyWorkspace();
            if (saved is not null)
                ApplyWorkspaceLayout(
                    App.Workspaces.DraftOf(id) ?? new WorkspaceLayout { Groups = saved.Groups, Layout = saved.Layout },
                    additive: false);
            else
                FinishLayoutChange(ViewModel.Groups[0], null);
        }

        var previous = _shownWorkspace;
        _shownWorkspace = id;
        if (animate)
            PlayWorkspaceSwitchMotion(previous, id);
        App.RefreshWorkspaceRails();
        App.RefreshWindowTitles();
        if (focusTerminal)
            FocusActiveTerminal();
    }

    /// <summary>Keeps the shown workspace's unsaved changes as a draft (or drops a draft it no
    /// longer needs), so they survive a crash. Its saved layout is not touched.</summary>
    private void KeepShownWorkspaceDraft()
    {
        if (_shownWorkspace != HomeWorkspace)
            WorkspaceOperations.KeepDraft(_shownWorkspace, CaptureWorkspaceLayout());
    }

    private void ParkShownWorkspace()
    {
        CaptureLivePaneSizes();
        var parked = new ParkedWorkspace(_groupLayout, new(_savedPaneSizes), ViewModel.FocusedGroup);
        foreach (var group in parked.Groups)
        {
            var view = _groupViews[group];
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(view) is Panel parent)
                parent.Children.Remove(view);
            view.IsParked = true;
            foreach (var tab in group.Tabs)
            {
                tab.IsInBackgroundWorkspace = true;
                tab.IsGroupFocused = false;
            }
        }
        ViewModel.BackgroundGroups.AddRange(parked.Groups);
        ViewModel.Groups.Clear();
        _parkedWorkspaces[_shownWorkspace] = parked;
    }

    private void UnparkWorkspace(ParkedWorkspace parked)
    {
        foreach (var group in parked.Groups)
        {
            ViewModel.BackgroundGroups.Remove(group);
            ViewModel.Groups.Add(group);
            _groupViews[group].IsParked = false;
            foreach (var tab in group.Tabs)
                tab.IsInBackgroundWorkspace = false;
        }
        _groupLayout = parked.Layout;
        _savedPaneSizes.Clear();
        foreach (var (branch, sizes) in parked.PaneSizes)
            _savedPaneSizes[branch] = sizes;
        var focused = ViewModel.Groups.Contains(parked.FocusedGroup) ? parked.FocusedGroup : ViewModel.Groups[0];
        FinishLayoutChange(focused, focused.SelectedTab);
    }

    /// <summary>One empty pane for a workspace that has nothing laid out yet.</summary>
    private TabGroupViewModel StartEmptyWorkspace()
    {
        var group = new TabGroupViewModel();
        ViewModel.Groups.Add(group);
        AttachGroupView(group);
        _groupLayout = new SplitLayout<TabGroupViewModel>(group);
        _savedPaneSizes.Clear();
        ViewModel.FocusedGroup = group;
        return group;
    }

    /// <summary>A parked workspace that holds one empty pane, ready to be shown.</summary>
    private ParkedWorkspace CreateEmptyParkedWorkspace()
    {
        var group = new TabGroupViewModel();
        AttachGroupView(group).IsParked = true;
        ViewModel.BackgroundGroups.Add(group);
        return new ParkedWorkspace(new SplitLayout<TabGroupViewModel>(group), [], group);
    }

    /// <summary>Records each split's live pane sizes, which the grids hold until a rebuild.</summary>
    private void CaptureLivePaneSizes() =>
        CapturePaneSizes(_groupLayout.Root, GroupArea.Children.FirstOrDefault() as FrameworkElement);

    private void CapturePaneSizes(SplitLayoutNode<TabGroupViewModel> node, FrameworkElement? element)
    {
        if (node is not SplitLayoutBranch<TabGroupViewModel> branch || element is not Grid grid)
            return;
        var isColumns = branch.Orientation == SplitOrientation.Columns;
        var tracks = isColumns ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count;
        if (tracks != branch.Children.Count * 2 - 1 || grid.Children.Count < branch.Children.Count * 3 - 2)
            return;
        var sizes = branch.Children.Select((_, index) => isColumns
            ? grid.ColumnDefinitions[index * 2].ActualWidth
            : grid.RowDefinitions[index * 2].ActualHeight).ToList();
        if (sizes.All(size => double.IsFinite(size) && size > 0))
            _savedPaneSizes[branch] = sizes;
        for (var index = 0; index < branch.Children.Count; index++)
            CapturePaneSizes(branch.Children[index], grid.Children[index * 3] as FrameworkElement);
    }

    /// <summary>A parked workspace's layout as a save would see it. It has no grids, so pane
    /// sizes come from what it recorded when it was parked.</summary>
    private static WorkspaceLayout CaptureParkedLayout(ParkedWorkspace parked)
    {
        var groups = parked.Groups.ToList();
        var indices = groups.Select((group, index) => (group, index)).ToDictionary(item => item.group, item => item.index);
        return new WorkspaceLayout
        {
            Groups = groups.Select(group => CaptureWorkspaceGroup(group.Tabs, group.SelectedTab)).ToList(),
            Layout = CaptureParkedNode(parked.Layout.Root, indices, parked.PaneSizes),
        };
    }

    private static WorkspaceLayoutNode CaptureParkedNode(
        SplitLayoutNode<TabGroupViewModel> node,
        IReadOnlyDictionary<TabGroupViewModel, int> indices,
        IReadOnlyDictionary<SplitLayoutBranch<TabGroupViewModel>, IReadOnlyList<double>> sizes) =>
        node switch
        {
            SplitLayoutLeaf<TabGroupViewModel> leaf => new WorkspaceLayoutNode { GroupIndex = indices[leaf.Value] },
            SplitLayoutBranch<TabGroupViewModel> branch => new WorkspaceLayoutNode
            {
                Orientation = branch.Orientation,
                Sizes = sizes.TryGetValue(branch, out var branchSizes) && branchSizes.Count == branch.Children.Count
                    ? branchSizes.ToList()
                    : [],
                Children = branch.Children.Select(child => CaptureParkedNode(child, indices, sizes)).ToList(),
            },
            _ => throw new InvalidOperationException("Unknown split layout node."),
        };

    /// <summary>The workspace a background group belongs to.</summary>
    private Guid? ParkedWorkspaceOf(TabGroupViewModel group) =>
        _parkedWorkspaces.FirstOrDefault(entry => entry.Value.Groups.Contains(group)) is { Value: not null } found
            ? found.Key
            : null;

    /// <summary>A background pane lost its last tab. It leaves its workspace's split unless it
    /// is the only pane there, as a shown pane would.</summary>
    private void PruneParkedGroup(TabGroupViewModel group)
    {
        if (group.Tabs.Count > 0 || ParkedWorkspaceOf(group) is not { } id)
            return;
        var parked = _parkedWorkspaces[id];
        if (parked.Groups.Count > 1 && parked.Layout.Remove(group))
        {
            _groupViews.Remove(group);
            ViewModel.BackgroundGroups.Remove(group);
            if (ReferenceEquals(parked.FocusedGroup, group))
                parked.FocusedGroup = parked.Groups[0];
        }
        QueueWorkspaceRailRefresh();
    }

    // ---- creating, closing and removing ----

    /// <summary>The rail's + button. On Home with tabs open, those tabs become the new
    /// workspace and Home starts again empty; otherwise the new workspace starts empty.</summary>
    private async Task NewWorkspaceAsync()
    {
        var takesHome = _shownWorkspace == HomeWorkspace && ViewModel.ShownTabs.Any(tab => !tab.IsAppPage);
        var name = (await PromptAsync("New Workspace", "Workspace name", ""))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        if (takesHome)
        {
            AdoptShownTabsAsWorkspace(name);
            return;
        }
        if (WorkspaceOperations.Create(name, new WorkspaceLayout()) is { } created)
            ShowWorkspace(created.Id);
    }

    /// <summary>Saves what Home shows as a new workspace and lets that workspace keep the
    /// tabs, so its button does not open a second copy of every connection.</summary>
    private void AdoptShownTabsAsWorkspace(string name)
    {
        EndZoomForLayoutChange();
        if (WorkspaceOperations.Create(name, CaptureWorkspaceLayout()) is not { } created)
            return;
        _parkedWorkspaces[HomeWorkspace] = CreateEmptyParkedWorkspace();
        _shownWorkspace = created.Id;
        App.RefreshWorkspaceRails();
        App.RefreshWindowTitles();
    }

    /// <summary>Closes a workspace's tabs. With changes from its saved layout, asks whether to
    /// save them first (closing without saving drops them); otherwise asks only when sessions
    /// would disconnect, as before.</summary>
    private async Task CloseWorkspaceAsync(Workspace workspace)
    {
        if (!IsWorkspaceOpen(workspace.Id))
            return;
        var tabs = (_shownWorkspace == workspace.Id ? ViewModel.ShownTabs : _parkedWorkspaces[workspace.Id].Tabs).ToList();
        var sessions = tabs.Count(tab => !tab.IsAppPage);
        var disconnects = sessions == 0
            ? ""
            : $" Its {sessions} open {(sessions == 1 ? "session disconnects" : "sessions disconnect")}.";

        if (App.Workspaces.HasChanges(workspace.Id, LiveWorkspaceLayout(workspace.Id)))
        {
            var choice = await Dialogs.ConfirmDialog.ChooseAsync(
                Root.XamlRoot,
                "Save Workspace Changes?",
                $"\"{workspace.Name}\" has tabs or panes that differ from its saved layout. Save them before closing?{disconnects}",
                "Save and close",
                "Close without saving");
            if (choice == ContentDialogResult.None || !IsWorkspaceOpen(workspace.Id))
                return;
            if (choice == ContentDialogResult.Primary)
            {
                if (!WorkspaceOperations.Save(workspace.Id, LiveWorkspaceLayout(workspace.Id)))
                    return;
            }
            else
            {
                WorkspaceOperations.DiscardDraft(workspace.Id);
            }
        }
        else
        {
            if (sessions > 0 && App.Settings.Current.ConfirmCloseActiveSessions
                && !await ConfirmAsync(
                    "Close Workspace?",
                    $"Close \"{workspace.Name}\"?{disconnects} Opening it again reconnects.",
                    "Close",
                    acceptY: true))
            {
                return;
            }
            if (!IsWorkspaceOpen(workspace.Id))
                return; // closed while the dialog was open
            WorkspaceOperations.DiscardDraft(workspace.Id); // a stale one from a crash
        }
        DiscardWorkspace(workspace.Id);
    }

    /// <summary>Discards an open workspace's changes and reopens it as saved, or for one that
    /// is not open, forgets the draft it would have opened from.</summary>
    private async Task RevertWorkspaceAsync(Workspace workspace)
    {
        var open = IsWorkspaceOpen(workspace.Id);
        if (!await ConfirmAsync(
            "Revert Workspace?",
            open
                ? $"Discard the changes to \"{workspace.Name}\" and reopen it as saved? Its open sessions disconnect and reconnect."
                : $"Discard the unsaved changes to \"{workspace.Name}\"? It opens as saved next time.",
            "Revert"))
        {
            return;
        }
        WorkspaceOperations.DiscardDraft(workspace.Id);
        if (!IsWorkspaceOpen(workspace.Id))
            return;
        var wasShown = _shownWorkspace == workspace.Id;
        DiscardWorkspace(workspace.Id);
        if (wasShown)
            ShowWorkspace(workspace.Id, animate: false);
    }

    /// <summary>An open workspace's layout as it stands, shown or parked.</summary>
    private WorkspaceLayout LiveWorkspaceLayout(Guid id) =>
        _shownWorkspace == id ? CaptureWorkspaceLayout() : CaptureParkedLayout(_parkedWorkspaces[id]);

    /// <summary>Whether a workspace differs from what it was saved as: live here, live in the
    /// window that holds it, or through the draft it will open from.</summary>
    internal bool HasUnsavedWorkspaceChanges(Guid id)
    {
        if (id == HomeWorkspace)
            return false;
        if (IsWorkspaceOpen(id))
            return App.Workspaces.HasChanges(id, LiveWorkspaceLayout(id));
        if (App.WindowWithWorkspace(id, except: this) is { } other)
            return other.HasUnsavedWorkspaceChanges(id);
        return App.Workspaces.HasDraft(id);
    }

    /// <summary>Closes every tab of a workspace in this window without saving anything. A
    /// shown workspace hands the window back to Home before it goes.</summary>
    internal void DiscardWorkspace(Guid id)
    {
        if (!IsWorkspaceOpen(id))
            return;
        if (_shownWorkspace == id)
            ShowWorkspace(HomeWorkspace, keepDraft: false);

        if (!_parkedWorkspaces.Remove(id, out var parked))
            return;
        foreach (var group in parked.Groups.ToList())
        {
            var view = _groupViews[group];
            foreach (var tab in group.Tabs.ToList())
            {
                if (tab.View is OnboardingView onboarding)
                    onboarding.CancelPreview();
                if (tab.View is UIElement content)
                    view.RemoveTerminal(content);
                ViewModel.CloseTab(tab);
            }
            _groupViews.Remove(group);
            ViewModel.BackgroundGroups.Remove(group);
        }
        App.SavePinnedSessions();
        App.RefreshWorkspaceRails();
    }

    /// <summary>Keeps every open workspace's unsaved changes as a draft, so it reopens as it
    /// was left, and saves the Home layout, as the window closes. No workspace's saved layout
    /// changes here.</summary>
    private void SaveWorkspacesOnClose()
    {
        foreach (var (id, parked) in _parkedWorkspaces)
        {
            if (id != HomeWorkspace)
                WorkspaceOperations.KeepDraft(id, CaptureParkedLayout(parked));
        }
        KeepShownWorkspaceDraft();
        var home = _shownWorkspace == HomeWorkspace
            ? CaptureWorkspaceLayout()
            : _parkedWorkspaces.TryGetValue(HomeWorkspace, out var parkedHome)
                ? CaptureParkedLayout(parkedHome)
                : new WorkspaceLayout();
        WorkspaceOperations.SaveLastLayout(home, ShownWorkspaceId);
    }

    // ---- the rail ----

    internal void QueueWorkspaceRailRefresh()
    {
        if (_railRefreshQueued)
            return;
        _railRefreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _railRefreshQueued = false;
            RefreshWorkspaceRail();
        });
    }

    /// <summary>Brings the rail buttons in line with the saved workspaces and with what each
    /// window holds. Buttons are reused by id, so a drag in progress keeps its item.</summary>
    internal void RefreshWorkspaceRail()
    {
        var saved = App.Workspaces.Workspaces;
        for (var index = 0; index < saved.Count; index++)
        {
            var workspace = saved[index];
            var existing = -1;
            for (var probe = 0; probe < RailWorkspaces.Count; probe++)
            {
                if (RailWorkspaces[probe].Id == workspace.Id)
                {
                    existing = probe;
                    break;
                }
            }
            if (existing < 0)
                RailWorkspaces.Insert(index, WorkspaceRailItemViewModel.For(workspace));
            else
            {
                if (existing != index)
                    RailWorkspaces.Move(existing, index);
                RailWorkspaces[index].Update(workspace);
            }
        }
        while (RailWorkspaces.Count > saved.Count)
            RailWorkspaces.RemoveAt(RailWorkspaces.Count - 1);

        RailHome[0].SetState(true, _shownWorkspace == HomeWorkspace, false, AlertFor(HomeWorkspace), 1);
        for (var index = 0; index < RailWorkspaces.Count; index++)
        {
            var item = RailWorkspaces[index];
            item.SetState(
                IsWorkspaceOpen(item.Id),
                _shownWorkspace == item.Id,
                App.WindowWithWorkspace(item.Id, except: this) is not null,
                AlertFor(item.Id),
                index + 2,
                HasUnsavedWorkspaceChanges(item.Id));
        }

        // The shown workspace is the selection of whichever list holds it, so ListView draws its
        // own accent pill; the other list selects nothing.
        WorkspaceRailHomeList.SelectedItem = _shownWorkspace == HomeWorkspace ? RailHome[0] : null;
        WorkspaceRailList.SelectedItem = RailWorkspaces.FirstOrDefault(item => item.Id == _shownWorkspace);

        var hasWorkspaces = saved.Count > 0;
        WorkspaceRailDivider.Visibility = hasWorkspaces ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceRailHomeList.Visibility = hasWorkspaces ? Visibility.Visible : Visibility.Collapsed;
        var newTip = _shownWorkspace == HomeWorkspace && ViewModel.ShownTabs.Any(tab => !tab.IsAppPage)
            ? "New workspace from the tabs open in Home"
            : "New workspace";
        ToolTipService.SetToolTip(NewWorkspaceRailButton, newTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NewWorkspaceRailButton, newTip);
    }

    private WorkspaceAlert AlertFor(Guid id) =>
        id != _shownWorkspace && _parkedWorkspaces.TryGetValue(id, out var parked)
            ? WorkspaceAlerts.Summarize(parked.Tabs)
            : WorkspaceAlert.None;

    /// <summary>Ctrl+Shift+number: 1 is Home, then the workspaces in rail order. Without saved
    /// workspaces the key is left to the shell.</summary>
    private bool GoToWorkspaceAt(int position)
    {
        if (RailWorkspaces.Count == 0)
            return false;
        if (position == 1)
            ShowWorkspace(HomeWorkspace);
        else if (position - 2 < RailWorkspaces.Count)
            ShowWorkspace(RailWorkspaces[position - 2].Id);
        return true;
    }

    private void WorkspaceRail_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WorkspaceRailItemViewModel item)
            ShowWorkspace(item.Id);
    }

    private void WorkspaceRail_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        _middlePressedRailItem = e.GetCurrentPoint(WorkspaceRailList).Properties.IsMiddleButtonPressed
            ? RailItemFromSource(e.OriginalSource)
            : null;

    /// <summary>Closes the workspace when the middle button is pressed and released on the
    /// same open one, through the same confirmed path as the menu's Close.</summary>
    private void WorkspaceRail_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pressed = _middlePressedRailItem;
        _middlePressedRailItem = null;
        if (pressed is { IsOpen: true, Workspace: { } workspace }
            && e.GetCurrentPoint(WorkspaceRailList).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased
            && ReferenceEquals(RailItemFromSource(e.OriginalSource), pressed))
        {
            e.Handled = true;
            _ = CloseWorkspaceAsync(workspace);
        }
    }

    private static WorkspaceRailItemViewModel? RailItemFromSource(object source) =>
        (source as FrameworkElement)?.DataContext as WorkspaceRailItemViewModel;

    private void WorkspaceRail_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        WorkspaceOperations.Reorder(RailWorkspaces.Select(item => item.Id).ToList());

    private void NewWorkspaceRailButton_Click(object sender, RoutedEventArgs e) => _ = NewWorkspaceAsync();

    private void WorkspaceRail_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: WorkspaceRailItemViewModel item } source)
            return;
        args.Handled = true;
        HideWorkspaceHoverCard();
        var menu = BuildWorkspaceRailMenu(item);
        if (args.TryGetPosition(source, out var point))
            menu.ShowAt(source, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(source);
    }

    private MenuFlyout BuildWorkspaceRailMenu(WorkspaceRailItemViewModel item)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, System.Windows.Input.ICommand command, object? parameter = null) =>
            new() { Text = text, Command = command, CommandParameter = parameter };

        if (item.Workspace is not { } workspace)
        {
            if (!item.IsActive)
                menu.Items.Add(Item("Show Home", ViewModel.ShowHomeWorkspaceCommand));
            var save = new MenuFlyoutItem { Text = "New workspace from these tabs…" };
            save.Click += (_, _) => _ = NewWorkspaceAsync();
            save.IsEnabled = item.IsActive && ViewModel.ShownTabs.Any(tab => !tab.IsAppPage);
            menu.Items.Add(save);
            return menu;
        }

        if (!item.IsActive)
            menu.Items.Add(Item(item.IsOpenElsewhere ? "Go to window" : "Show", ViewModel.OpenWorkspaceCommand, workspace));
        if (!item.IsOpen && !item.IsOpenElsewhere)
            menu.Items.Add(Item("Open in new window", ViewModel.OpenWorkspaceInNewWindowCommand, workspace));
        if (!item.IsActive && !item.IsOpen && !item.IsOpenElsewhere)
            menu.Items.Add(Item("Add to current layout", ViewModel.OpenWorkspaceAdditivelyCommand, workspace));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Icon…", ViewModel.CustomizeWorkspaceCommand, workspace));
        menu.Items.Add(Item("Rename…", ViewModel.RenameWorkspaceCommand, workspace));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var entry in SaveWorkspaceMenuItems(workspace))
            menu.Items.Add(entry);
        // Always listed, so middle-click is discoverable; only an open one can close here.
        var close = item.IsOpen
            ? Item("Close workspace", ViewModel.CloseWorkspaceCommand, workspace)
            : new MenuFlyoutItem { Text = "Close workspace", IsEnabled = false };
        close.KeyboardAcceleratorTextOverride = "Middle-click";
        menu.Items.Add(close);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete…", ViewModel.DeleteWorkspaceCommand, workspace));
        return menu;
    }

    /// <summary>"Save workspace" and "Revert to saved…", enabled while the workspace differs
    /// from its saved layout, open here or through a draft.</summary>
    private IEnumerable<MenuFlyoutItemBase> SaveWorkspaceMenuItems(Workspace workspace)
    {
        var unsaved = HasUnsavedWorkspaceChanges(workspace.Id);
        var save = new MenuFlyoutItem { Text = "Save workspace", IsEnabled = unsaved };
        save.Click += (_, _) => SaveWorkspaceNow(workspace);
        var revert = new MenuFlyoutItem { Text = "Revert to saved…", IsEnabled = unsaved };
        revert.Click += (_, _) => _ = RevertWorkspaceAsync(workspace);
        return [save, revert];
    }

    /// <summary>Makes a workspace's current layout what it opens to: live if it is open here,
    /// otherwise the draft it was left with. Nothing else saves a workspace's layout.</summary>
    private void SaveWorkspaceNow(Workspace workspace)
    {
        var layout = IsWorkspaceOpen(workspace.Id)
            ? LiveWorkspaceLayout(workspace.Id)
            : App.Workspaces.DraftOf(workspace.Id);
        if (layout is not null)
            WorkspaceOperations.Save(workspace.Id, layout);
    }

    private async Task CustomizeWorkspaceAsync(Workspace workspace)
    {
        var current = App.Workspaces.Workspaces.FirstOrDefault(candidate => candidate.Id == workspace.Id) ?? workspace;
        if (await Dialogs.WorkspaceIconDialog.EditAsync(Root.XamlRoot, current) is { } choice)
            WorkspaceOperations.SetAppearance(current.Id, choice.Icon, choice.Initials, choice.LabelIcon);
    }
}
