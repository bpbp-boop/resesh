using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.App.ViewModels;
using Resesh.Core.Layout;
using Resesh.Core.Models;

namespace Resesh.App.Controls;

/// <summary>What a tab group needs from the window: dialogs, group management, the single close pathway.</summary>
public interface ITabGroupHost
{
    MainViewModel ViewModel { get; }
    void FocusGroup(TabGroupViewModel group);
    Task RequestCloseTabAsync(TabViewModel tab);
    Task RequestCloseManyAsync(IReadOnlyList<TabViewModel> tabs, string description);
    void SplitRight(TabViewModel tab);
    void SplitDown(TabViewModel tab);
    void SplitTab(TabViewModel tab, TabGroupViewModel targetGroup, SplitDirection direction);
    void MoveTabBetweenGroups(TabViewModel tab, TabGroupViewModel targetGroup, int targetIndex);
    void TransferTabToGroup(
        TabViewModel tab,
        ITabGroupHost sourceHost,
        TabGroupViewModel targetGroup,
        int targetIndex);
    void DetachTabForTransfer(TabViewModel tab);
    void SetTabContentDropTargetsVisible(bool visible);
    void CloneSession(TabViewModel tab);
    void TogglePin(TabViewModel tab);
    Task OpenSessionOptionsAsync(TabViewModel tab);
    Task LockSessionAsync(TabViewModel tab);
    void ReconnectTab(TabViewModel tab);
    void DisconnectTab(TabViewModel tab);
    Task EndRemoteSessionAsync(TabViewModel tab);
    void ToggleFilePane(TabViewModel tab);
    Task OpenFilePaneAtCurrentFolderAsync(TabViewModel tab);
    void OpenWorkingFolder(TabViewModel tab);
    Task OpenRecordingsLocationAsync();
    void SetTabAgent(TabViewModel tab, string? key);
    void SaveTabAgentAsSessionDefault(TabViewModel tab);
    Task ShowAgentAdaptersAsync();
}

public sealed partial class TabGroupView : UserControl
{
    private const double MinimumTabWidth = 100;
    private const double ExpandedTabActionsFallbackWidth = 180;

    // In-process handoff for cross-group tab drags.
    private static TabViewModel? _draggedTab;
    private static TabGroupView? _dragSource;

    private readonly ITabGroupHost _host;
    private readonly MenuFlyout _tabMenu;
    private TabViewModel? _menuTab;
    private TabViewModel? _middleClickTab;
    private SplitDirection _dropDirection = SplitDirection.Right;
    private bool _tabWidthRefreshQueued;
    private bool _fullTabWidthRefreshQueued;
    private bool _tabDividerRefreshQueued;
    private bool _tabDividerLayoutRefreshPending;
    private ThemeVisualPalette _themePalette = ThemeVisualPalette.For(App.ResolveTheme(App.Settings.Current.Theme));
    private readonly SolidColorBrush _tabBackgroundBrush;
    private readonly SolidColorBrush _tabDividerBrush;
    private TabViewModel? _filePaneButtonTab;
    private Terminal.TerminalTabView? _filePaneButtonView;
    private double _expandedTabActionsWidth = ExpandedTabActionsFallbackWidth;

    public TabGroupViewModel Group { get; }

    public TabGroupView(TabGroupViewModel group, ITabGroupHost host)
    {
        Group = group;
        _host = host;
        _tabBackgroundBrush = new SolidColorBrush(_themePalette.InactiveTab);
        _tabDividerBrush = new SolidColorBrush(_themePalette.Divider);
        InitializeComponent();
        _tabMenu = BuildTabMenu();
        ObserveFilePaneButtonTab();

        // Source changes refresh the layout and action buttons. WinUI's native tab-items
        // event runs when it has received the new collection state; use that event to
        // recover full equal widths after a removal. Size changes cover a group that gains
        // space when a neighboring split is removed or resized.
        Group.Tabs.CollectionChanged += (_, _) =>
        {
            QueueTabWidthRefresh();
            UpdateTabActionButtons();
            QueueTabDividerRefresh();
        };
        Tabs.TabItemsChanged += (_, e) =>
        {
            if (e.CollectionChange == Windows.Foundation.Collections.CollectionChange.ItemRemoved)
                QueueFullTabWidthRefresh();
        };
        Tabs.SizeChanged += (_, _) =>
        {
            // TabView caches equal item widths. Invalidating measure alone leaves the old
            // width in place when the strip narrows, so the ScrollViewer shifts tabs left.
            // Reset the mode to force WinUI to recalculate the shared width for this extent.
            QueueFullTabWidthRefresh();
            QueueTabDividerRefresh();
        };
        TabStripHost.SizeChanged += (_, _) =>
        {
            UpdateTabActionLayout();
            QueueTabDividerRefresh();
        };

        // Focus tracking: interacting anywhere in this group focuses it.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _host.FocusGroup(Group)), true);

        // Visibility must also follow the view model, not just TabView.SelectionChanged:
        // when the selected tab is closed, the TabView auto-selects a neighbor and raises
        // SelectionChanged BEFORE the TwoWay binding writes SelectedTab back — a sync from
        // that event reads the stale value and leaves the surviving terminal collapsed
        // (observed: blank terminal area after closing the active tab). This handler runs
        // when the write-back lands and re-syncs against the settled value.
        Group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabGroupViewModel.SelectedTab))
            {
                SyncTerminalVisibility();
                _host.ViewModel.NotifyActiveTabChanged();
                ObserveFilePaneButtonTab();
                FocusTerminal(Group.SelectedTab);
                QueueTabDividerRefresh();
            }
        };

        // Middle-click closes a tab (same confirmed pathway); TabView has no built-in support.
        Tabs.AddHandler(PointerPressedEvent, new PointerEventHandler(Tabs_PointerPressed), true);
        Tabs.AddHandler(PointerReleasedEvent, new PointerEventHandler(Tabs_PointerReleased), true);
        Tabs.AddHandler(RightTappedEvent, new RightTappedEventHandler(Tabs_RightTapped), true);
        // TabView can keep keyboard focus on an already-selected header after a click. In
        // that state xterm cannot see Enter, so retain its stopped-terminal shortcut here.
        Tabs.AddHandler(KeyDownEvent, new KeyEventHandler(Tabs_KeyDown), true);
        ActualThemeChanged += (_, _) => QueueTabTemplateRefresh();

        // TabView consumes drag events over parts of its strip without raising TabStripDrop.
        // Listen on the surrounding row even for handled events so its unused space remains
        // a cross-group drop target.
        TabStripHost.AddHandler(DragOverEvent, new DragEventHandler(TabStripHost_DragOver), true);
        TabStripHost.AddHandler(DropEvent, new DragEventHandler(TabStripHost_Drop), true);
    }

    // ---- terminal hosting ----

    public void AddTerminal(UIElement view)
    {
        TerminalHost.Children.Add(view);
        SyncTerminalVisibility();
    }

    private void Tabs_Loaded(object sender, RoutedEventArgs e)
    {
        NormalizeTabStripTemplate();
        QueueTabWidthRefresh();
    }

    private void NormalizeTabStripTemplate()
    {
        // TabView reserves a 2px minimum column for TabStripHeader even when it is null.
        if (FindDescendant(Tabs, "TabContainerGrid") is Grid tabContainerGrid &&
            tabContainerGrid.ColumnDefinitions.Count > 0)
        {
            tabContainerGrid.ColumnDefinitions[0].MinWidth = 0;
            tabContainerGrid.ColumnDefinitions[0].Width = new GridLength(0);
        }

        // WinUI's TabViewListView template inserts a fixed 4px ItemsPresenter header
        // and another 1px on its ScrollContentPresenter. Keep the header object alive
        // because TabView visual states target its named border, but collapse its width.
        if (FindDescendant(Tabs, "TabsItemsPresenter") is ItemsPresenter { Header: FrameworkElement header })
            header.Width = 0;

        if (FindDescendant(Tabs, "ScrollContentPresenter") is ScrollContentPresenter scrollContent)
        {
            scrollContent.Padding = new Thickness(0);

            if (VisualTreeHelper.GetParent(scrollContent) is Grid scrollViewerGrid &&
                scrollViewerGrid.ColumnDefinitions.Count > 0)
            {
                scrollViewerGrid.ColumnDefinitions[0].MinWidth = 0;
            }
        }
    }

    internal void ApplyTheme(ThemeVisualPalette palette)
    {
        _themePalette = palette;
        // Keep these brush instances stable. WinUI can retain brushes from the existing
        // TabView visual tree when a live theme change rebuilds its template.
        _tabBackgroundBrush.Color = palette.InactiveTab;
        _tabDividerBrush.Color = palette.Divider;
        var background = _tabBackgroundBrush;
        var divider = _tabDividerBrush;
        Tabs.Background = background;
        TabStripActions.Background = background;
        LeftTabStripDivider.Fill = divider;
        RightTabStripDivider.Fill = divider;
        Resources["TabViewBackground"] = background;
        Resources["TabViewBorderBrush"] = divider;
        Resources["CodeTabSeparatorBrush"] = divider;
    }

    private void QueueTabTemplateRefresh()
    {
        // RequestedTheme can rebuild TabView after ApplyTheme returns. Run after that
        // layout pass so WinUI's hidden header width and presenter padding stay removed.
        DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
        {
            NormalizeTabStripTemplate();
            QueueTabWidthRefresh();
        }));
    }


    private void QueueTabWidthRefresh()
    {
        if (_tabWidthRefreshQueued)
            return;

        _tabWidthRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _tabWidthRefreshQueued = false;
            FindDescendant(Tabs, "TabsItemsPresenter")?.InvalidateMeasure();
            Tabs.InvalidateMeasure();
            QueueTabDividerRefreshAfterLayout();
        });
    }

    private void QueueFullTabWidthRefresh()
    {
        if (_fullTabWidthRefreshQueued)
            return;

        _fullTabWidthRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _fullTabWidthRefreshQueued = false;
            // WinUI otherwise defers this full-width path until a pointer event.
            Tabs.TabWidthMode = TabViewWidthMode.SizeToContent;
            Tabs.TabWidthMode = TabViewWidthMode.Equal;
            FindDescendant(Tabs, "TabsItemsPresenter")?.InvalidateMeasure();
            Tabs.InvalidateMeasure();
            QueueTabDividerRefreshAfterLayout();
        });
    }

    private void QueueTabDividerRefresh()
    {
        if (_tabDividerRefreshQueued)
            return;

        _tabDividerRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _tabDividerRefreshQueued = false;
            UpdateTabStripDivider();
        });
    }

    private void QueueTabDividerRefreshAfterLayout()
    {
        if (_tabDividerLayoutRefreshPending)
            return;

        _tabDividerLayoutRefreshPending = true;
        Tabs.LayoutUpdated += Tabs_DividerLayoutUpdated;
    }

    private void Tabs_DividerLayoutUpdated(object? sender, object e)
    {
        Tabs.LayoutUpdated -= Tabs_DividerLayoutUpdated;
        _tabDividerLayoutRefreshPending = false;
        UpdateTabStripDivider();
    }

    private void UpdateTabStripDivider()
    {
        var stripWidth = TabStripHost.ActualWidth;
        if (Tabs.SelectedItem is null)
        {
            LeftTabStripDivider.Width = 0;
            RightTabStripDivider.Width = 0;
            return;
        }

        if (Tabs.ContainerFromItem(Tabs.SelectedItem) is FrameworkElement activeTab &&
            activeTab.ActualWidth > 0)
        {
            var origin = activeTab
                .TransformToVisual(TabStripHost)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var activeLeft = Math.Clamp(origin.X, 0, stripWidth);
            var activeRight = Math.Clamp(origin.X + activeTab.ActualWidth, 0, stripWidth);
            LeftTabStripDivider.Width = activeLeft;
            RightTabStripDivider.Width = stripWidth - activeRight;
            return;
        }

        // A newly opened and selected tab can precede realization of its container.
        // Hide the divider until layout provides exact bounds rather than briefly
        // drawing it beneath the new active tab.
        LeftTabStripDivider.Width = 0;
        RightTabStripDivider.Width = 0;
        QueueTabDividerRefreshAfterLayout();
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name)
                return element;

            if (FindDescendant(child, name) is { } descendant)
                return descendant;
        }

        return null;
    }

    public void RemoveTerminal(UIElement view)
    {
        TerminalHost.Children.Remove(view);
        SyncTerminalVisibility();
    }

    public void SyncTerminalVisibility()
    {
        var selected = Group.SelectedTab?.View;
        MainWindow.Trace($"SyncTerminalVisibility: selected='{Group.SelectedTab?.Header}' children={TerminalHost.Children.Count}");
        foreach (var child in TerminalHost.Children)
            child.Visibility = ReferenceEquals(child, selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- tab-bar action buttons (show commands, current folder, file pane) ----

    private void ObserveFilePaneButtonTab()
    {
        if (_filePaneButtonTab is not null)
            _filePaneButtonTab.PropertyChanged -= FilePaneButtonTab_PropertyChanged;
        if (_filePaneButtonView is not null)
        {
            _filePaneButtonView.FilePaneOpenChanged -= ActionButtonView_StateChanged;
            _filePaneButtonView.CommandsPanelOpenChanged -= ActionButtonView_StateChanged;
            _filePaneButtonView.CaptureStateChanged -= ActionButtonView_StateChanged;
        }

        _filePaneButtonTab = Group.SelectedTab;
        _filePaneButtonView = _filePaneButtonTab?.View as Terminal.TerminalTabView;

        if (_filePaneButtonTab is not null)
            _filePaneButtonTab.PropertyChanged += FilePaneButtonTab_PropertyChanged;
        if (_filePaneButtonView is not null)
        {
            _filePaneButtonView.FilePaneOpenChanged += ActionButtonView_StateChanged;
            _filePaneButtonView.CommandsPanelOpenChanged += ActionButtonView_StateChanged;
            _filePaneButtonView.CaptureStateChanged += ActionButtonView_StateChanged;
        }

        UpdateTabActionButtons();
    }

    private void FilePaneButtonTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.View))
            ObserveFilePaneButtonTab();
        else if (e.PropertyName is nameof(TabViewModel.Session) or nameof(TabViewModel.IsLocked)
                 or nameof(TabViewModel.State) or nameof(TabViewModel.IsGroupFocused))
            UpdateTabActionButtons();
    }

    private void ActionButtonView_StateChanged() => UpdateTabActionButtons();

    private void UpdateTabActionButtons()
    {
        // An empty group has nothing the buttons could act on: hide the whole cluster.
        TabStripActions.Visibility = Group.Tabs.Count > 0 && Group.SelectedTab is { IsOnboarding: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        TabStripActions.Opacity = Group.SelectedTab?.IsGroupFocused == false ? 0.55 : 1.0;
        UpdateTabActionLayout();

        var tab = Group.SelectedTab;
        var isOpen = tab?.View is Terminal.TerminalTabView { IsFilePaneOpen: true };
        var commandsOpen = tab?.View is Terminal.TerminalTabView { IsCommandsPanelOpen: true };
        var recording = tab?.View is Terminal.TerminalTabView { IsRecording: true };
        var rewinding = tab?.View is Terminal.TerminalTabView { IsRewinding: true };
        ShowCommandsButton.IsEnabled = tab is not null && !tab.IsLocked && tab.View is Terminal.TerminalTabView;
        FilePaneToggle.IsEnabled = tab?.Capabilities.FilePane == true
            && !tab.IsLocked
            && tab.View is Terminal.TerminalTabView;
        CurrentFolderButton.IsEnabled = tab?.Capabilities.FilePane == true &&
            tab.State == TabConnectionState.Connected && !tab.IsLocked;
        RecordButton.IsEnabled = tab?.View is Terminal.TerminalTabView { CanRecord: true } && !tab.IsLocked;
        RewindButton.IsEnabled = tab?.View is Terminal.TerminalTabView { CanRewind: true } && !tab.IsLocked;
        RecordOverflowItem.IsEnabled = RecordButton.IsEnabled;
        RewindOverflowItem.IsEnabled = RewindButton.IsEnabled;
        ShowCommandsOverflowItem.IsEnabled = ShowCommandsButton.IsEnabled;
        CurrentFolderOverflowItem.IsEnabled = CurrentFolderButton.IsEnabled;
        FilePaneOverflowItem.IsEnabled = FilePaneToggle.IsEnabled;
        FilePaneToggle.IsChecked = isOpen;
        ShowCommandsButton.IsChecked = commandsOpen;
        RecordButton.IsChecked = recording;
        RecordStartIcon.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        RecordStopIcon.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RewindButton.IsChecked = rewinding;
        FilePaneOverflowItem.IsChecked = isOpen;
        ShowCommandsOverflowItem.IsChecked = commandsOpen;
        RecordOverflowItem.IsChecked = recording;
        RewindOverflowItem.IsChecked = rewinding;

        var label = isOpen ? "Hide file pane" : "Show file pane";
        ToolTipService.SetToolTip(FilePaneToggle, $"{label} (Ctrl+Shift+E)");
        AutomationProperties.SetName(FilePaneToggle, label);
        AutomationProperties.SetName(CurrentFolderButton, "Open file pane at terminal folder");
        var commandsLabel = commandsOpen ? "Hide commands" : "Show commands";
        ToolTipService.SetToolTip(ShowCommandsButton, $"{commandsLabel} (Ctrl+Shift+O)");
        AutomationProperties.SetName(ShowCommandsButton, commandsLabel);
        var recordingLabel = recording ? "Finish recording" : "Start recording";
        ToolTipService.SetToolTip(RecordButton, recordingLabel);
        AutomationProperties.SetName(RecordButton, recordingLabel);
        ToolTipService.SetToolTip(RewindButton, rewinding ? "Return to live terminal" : "Instant rewind");
        AutomationProperties.SetName(RewindButton, rewinding ? "Return to live terminal" : "Instant rewind");
        FilePaneOverflowItem.Text = label;
        ShowCommandsOverflowItem.Text = commandsLabel;
        RecordOverflowItem.Text = recordingLabel;
        RewindOverflowItem.Text = rewinding ? "Return to live terminal" : "Instant rewind";
    }

    private void UpdateTabActionLayout()
    {
        if (ExpandedTabActions.Visibility == Visibility.Visible && ExpandedTabActions.ActualWidth > 0)
            _expandedTabActionsWidth = ExpandedTabActions.ActualWidth;

        var useOverflow = TabStripHost.ActualWidth > 0
            && Group.Tabs.Count > 0
            && TabStripHost.ActualWidth < Group.Tabs.Count * MinimumTabWidth + _expandedTabActionsWidth;
        var expandedVisibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
        if (ExpandedTabActions.Visibility == expandedVisibility)
            return;

        ExpandedTabActions.Visibility = expandedVisibility;
        TabActionsOverflowButton.Visibility = useOverflow ? Visibility.Visible : Visibility.Collapsed;
        QueueTabWidthRefresh();
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (Group.SelectedTab?.View is Terminal.TerminalTabView view)
            await view.ToggleRecordingAsync();
        UpdateTabActionButtons();
    }

    private async void RewindButton_Click(object sender, RoutedEventArgs e)
    {
        if (Group.SelectedTab?.View is Terminal.TerminalTabView view)
            await view.ToggleRewindAsync();
        UpdateTabActionButtons();
    }

    private void ShowCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Group.SelectedTab?.View is Terminal.TerminalTabView view)
        {
            view.ToggleCommandsPanel();
            // Keep keystrokes flowing to the terminal (panel rows refocus it likewise).
            view.FocusTerminal();
        }
    }

    private async void CurrentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (Group.SelectedTab is { } tab)
            await _host.OpenFilePaneAtCurrentFolderAsync(tab);
    }

    private void FilePaneToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Group.SelectedTab is { } tab)
            _host.ToggleFilePane(tab);
        UpdateTabActionButtons();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MainWindow.Trace($"Tabs_SelectionChanged: control={((e.AddedItems.Count > 0 ? e.AddedItems[0] : null) as TabViewModel)?.Header ?? "(none)"} vm={Group.SelectedTab?.Header ?? "(null)"}");
        SyncTerminalVisibility();
        _host.FocusGroup(Group);
        _host.ViewModel.NotifyActiveTabChanged();
        FocusTerminal(Group.SelectedTab);
        QueueTabDividerRefresh();
    }

    private void FocusTerminal(TabViewModel? tab)
    {
        if (tab?.View is Terminal.TerminalTabView view)
            DispatcherQueue.TryEnqueue(view.FocusTerminal);
    }

    // ---- the single confirmed-close pathway (X button converges here too) ----

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewModel tab)
            await _host.RequestCloseTabAsync(tab);
    }

    /// <summary>The custom ×: same confirmed-close pathway as every other close route.</summary>
    private async void TabCloseGlyph_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabViewModel tab)
            await _host.RequestCloseTabAsync(tab);
    }

    private void TabHeader_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabViewModel tab)
            tab.IsPointerOver = true;
    }

    private void TabHeader_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabViewModel tab)
            tab.IsPointerOver = false;
    }

    private static TabViewModel? TabFromOriginalSource(object originalSource)
    {
        for (var d = originalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TabViewItem item)
                return item.DataContext as TabViewModel;
        }
        return null;
    }

    private void Tabs_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Tabs);
        var tab = TabFromOriginalSource(e.OriginalSource);
        _middleClickTab = point.Properties.IsMiddleButtonPressed ? tab : null;

        // SelectionChanged does not run when the selected tab is clicked again. Queue the
        // focus so the TabView can finish its pointer handling before focus enters WebView2.
        if (point.Properties.IsLeftButtonPressed && tab is not null && !IsButtonSource(e.OriginalSource))
            FocusTerminal(tab);
    }

    private static bool IsButtonSource(object originalSource)
    {
        for (var d = originalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Button)
                return true;
            if (d is TabViewItem)
                return false;
        }
        return false;
    }

    private void Tabs_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter
            && Group.SelectedTab is { } tab
            && IsStopped(tab)
            && !IsButtonSource(e.OriginalSource))
        {
            _host.ReconnectTab(tab);
            e.Handled = true;
        }
    }

    private async void Tabs_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Only close when press and release landed on the same tab.
        var pressed = _middleClickTab;
        _middleClickTab = null;
        if (pressed is not null
            && e.GetCurrentPoint(Tabs).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased
            && ReferenceEquals(TabFromOriginalSource(e.OriginalSource), pressed))
        {
            await _host.RequestCloseTabAsync(pressed);
        }
    }

    // ---- context menu ----

    private void Tabs_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (TabFromOriginalSource(e.OriginalSource) is not { } tab)
            return;
        _menuTab = tab;
        ConfigureMenuFor(tab);
        _tabMenu.ShowAt(Tabs, e.GetPosition(Tabs));
        e.Handled = true;
    }

    private MenuFlyoutItem _rename = null!, _resetName = null!, _reconnect = null!, _disconnect = null!, _endRemote = null!,
        _close = null!, _closeDisconnected = null!, _closeOthers = null!, _closeRight = null!,
        _closeGroup = null!, _closeAll = null!, _pin = null!, _lock = null!, _clone = null!, _split = null!, _splitDown = null!,
        _options = null!,
        _filePane = null!, _filePaneCwd = null!, _workingFolder = null!, _recordingsLocation = null!;
    private MenuFlyoutSubItem _highlight = null!, _agent = null!;

    private MenuFlyout BuildTabMenu()
    {
        MenuFlyoutItem Item(string text, Action<TabViewModel> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                if (_menuTab is { } tab)
                    action(tab);
            };
            return item;
        }

        _rename = Item("Rename", tab => _ = RenameTabAsync(tab));
        _resetName = Item("Reset Name", tab => tab.TitleOverride = null);
        _reconnect = Item("Reconnect", tab => _host.ReconnectTab(tab));
        _disconnect = Item("Disconnect", tab => _host.DisconnectTab(tab));
        _endRemote = Item("End Remote Session…", tab => _ = _host.EndRemoteSessionAsync(tab));
        _filePane = Item("File Pane", tab => _host.ToggleFilePane(tab));
        _filePane.KeyboardAcceleratorTextOverride = "Ctrl+Shift+E";
        _filePaneCwd = Item("Open File Pane at Terminal Folder", tab => _ = _host.OpenFilePaneAtCurrentFolderAsync(tab));
        _workingFolder = Item("Open Working Folder", tab => _host.OpenWorkingFolder(tab));
        _recordingsLocation = Item("Open Recordings Location", tab => _ = _host.OpenRecordingsLocationAsync());
        _close = Item("Close", tab => _ = _host.RequestCloseTabAsync(tab));
        _close.KeyboardAcceleratorTextOverride = "Ctrl+F4";
        _closeDisconnected = Item("Close Disconnected Tabs", tab =>
            _ = _host.RequestCloseManyAsync(
                Group.Tabs.Where(t => t.State == TabConnectionState.Disconnected).ToList(),
                "disconnected tab(s) in this group"));
        _closeOthers = Item("Close Other Tabs", tab =>
            _ = _host.RequestCloseManyAsync(Group.Tabs.Where(t => t != tab).ToList(), "other tab(s)"));
        _closeRight = Item("Close Tabs to the Right", tab =>
            _ = _host.RequestCloseManyAsync(Group.Tabs.Skip(Group.Tabs.IndexOf(tab) + 1).ToList(), "tab(s) to the right"));
        _closeGroup = Item("Close Tab Group", tab =>
            _ = _host.RequestCloseManyAsync(Group.Tabs.ToList(), "tab(s) in this group"));
        _closeAll = Item("Close All Tabs", tab =>
            _ = _host.RequestCloseManyAsync(_host.ViewModel.AllTabs.ToList(), "tab(s)"));
        _pin = Item("Pin Tab", tab => _host.TogglePin(tab));
        _lock = Item("Lock Session…", tab => _ = _host.LockSessionAsync(tab));
        _clone = Item("Clone Session", tab => _host.CloneSession(tab));
        _split = Item("Split Right", tab => _host.SplitRight(tab));
        _split.KeyboardAcceleratorTextOverride = "Ctrl+Shift+\\";
        _splitDown = Item("Split Down", tab => _host.SplitDown(tab));
        _options = Item("Session Options…", tab => _ = _host.OpenSessionOptionsAsync(tab));
        _highlight = new MenuFlyoutSubItem { Text = "Highlighting" };
        _agent = new MenuFlyoutSubItem { Text = "Agent" };

        var menu = new MenuFlyout();
        menu.Items.Add(_rename);
        menu.Items.Add(_resetName);
        menu.Items.Add(_reconnect);
        menu.Items.Add(_disconnect);
        menu.Items.Add(_endRemote);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_close);
        menu.Items.Add(_closeDisconnected);
        menu.Items.Add(_closeOthers);
        menu.Items.Add(_closeRight);
        menu.Items.Add(_closeGroup);
        menu.Items.Add(_closeAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_pin);
        menu.Items.Add(_lock);
        menu.Items.Add(_clone);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_split);
        menu.Items.Add(_splitDown);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_filePane);
        menu.Items.Add(_filePaneCwd);
        menu.Items.Add(_workingFolder);
        menu.Items.Add(_recordingsLocation);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_highlight);
        menu.Items.Add(_agent);
        menu.Items.Add(_options);
        return menu;
    }

    private static bool IsStopped(TabViewModel tab) =>
        tab.State is TabConnectionState.Disconnected or TabConnectionState.Exited;

    private void ConfigureMenuFor(TabViewModel tab)
    {
        var caps = tab.Capabilities;
        var isOnboarding = tab.IsOnboarding;
        _rename.IsEnabled = !isOnboarding;
        _resetName.IsEnabled = !isOnboarding && tab.TitleOverride is not null;
        // Local tabs use process verbs (Stop/Restart); remote-only actions disappear entirely.
        _reconnect.Text = caps.StartAgainVerb;
        _reconnect.IsEnabled = !isOnboarding && IsStopped(tab);
        _disconnect.Text = caps.StopVerb;
        _disconnect.IsEnabled = !isOnboarding && tab.State == TabConnectionState.Connected;
        // Only persistent sessions have a remote session to end; close/disconnect only detach them.
        var endRemote = caps.RemoteSession && tab.Session.Persistent;
        _endRemote.Visibility = endRemote ? Visibility.Visible : Visibility.Collapsed;
        _endRemote.IsEnabled = endRemote && tab.State == TabConnectionState.Connected;
        // Bulk closes skip pinned tabs, so they're only offered when an unpinned tab qualifies.
        _closeDisconnected.IsEnabled = Group.Tabs.Any(t => IsStopped(t) && !t.IsPinned);
        _closeOthers.IsEnabled = Group.Tabs.Any(t => t != tab && !t.IsPinned);
        _closeRight.IsEnabled = Group.Tabs.Skip(Group.Tabs.IndexOf(tab) + 1).Any(t => !t.IsPinned);
        _pin.Text = tab.IsPinned ? "Unpin Tab" : "Pin Tab";
        _clone.IsEnabled = !isOnboarding && !tab.IsPlayback;
        _pin.IsEnabled = !isOnboarding && !tab.IsPlayback;
        _lock.IsEnabled = !isOnboarding && !tab.IsPlayback && !tab.IsLocked;
        // Splitting a lone tab would leave an empty group that immediately collapses — pointless.
        _split.IsEnabled = !isOnboarding && Group.Tabs.Count > 1;
        _splitDown.IsEnabled = !isOnboarding && Group.Tabs.Count > 1;
        _filePane.Visibility = caps.FilePane ? Visibility.Visible : Visibility.Collapsed;
        _filePane.Text = tab.View is Terminal.TerminalTabView { IsFilePaneOpen: true } ? "Hide File Pane" : "Show File Pane";
        var filePaneCwd = caps.FilePane;
        _filePaneCwd.Visibility = filePaneCwd ? Visibility.Visible : Visibility.Collapsed;
        _filePaneCwd.IsEnabled = filePaneCwd && tab.State == TabConnectionState.Connected;
        _workingFolder.Visibility = caps.LocalWorkingFolder && !tab.IsPlayback
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Session Options is disabled when the saved session was deleted while connected.
        var sessionExists = _host.ViewModel.RankedMatches("").Any(s => s.Id == tab.Session.Id);
        _options.IsEnabled = !isOnboarding && sessionExists;
        ConfigureHighlightMenu(tab, sessionExists);
        ConfigureAgentMenu(tab, sessionExists);
    }

    /// <summary>
    /// Rebuilds the Agent submenu: what resesh thinks is running here, an explicit
    /// override (or "auto"), and the adapter snippets. A manual choice is exactly that —
    /// detection never overwrites it, and it never touches the session icon.
    /// </summary>
    private void ConfigureAgentMenu(TabViewModel tab, bool sessionExists)
    {
        _agent.Items.Clear();
        var view = tab.View as Terminal.TerminalTabView;
        var chosen = view?.AgentOverride; // null = auto-detect

        ToggleMenuFlyoutItem Choice(string text, string? key)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = string.Equals(chosen, key, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => _host.SetTabAgent(tab, key);
            return item;
        }

        var detected = tab.Agent.IsAgent ? tab.Agent.Name : "none detected";
        _agent.Items.Add(new MenuFlyoutItem { Text = $"Running: {detected}", IsEnabled = false });
        _agent.Items.Add(new MenuFlyoutSeparator());
        _agent.Items.Add(Choice("Auto-detect", null));
        foreach (var identity in Resesh.Core.Agents.AgentIdentities.All.Where(a => a.IsAgent))
            _agent.Items.Add(Choice(identity.Name, identity.Key));
        _agent.Items.Add(Choice("No agent icon", Resesh.Core.Agents.AgentIdentities.None));
        _agent.Items.Add(new MenuFlyoutSeparator());

        var saveDefault = new MenuFlyoutItem
        {
            Text = "Save as Session Default",
            IsEnabled = sessionExists && view is not null,
        };
        saveDefault.Click += (_, _) => _host.SaveTabAgentAsSessionDefault(tab);
        _agent.Items.Add(saveDefault);

        var adapters = new MenuFlyoutItem { Text = "Adapter Snippets…" };
        adapters.Click += (_, _) => _ = _host.ShowAgentAdaptersAsync();
        _agent.Items.Add(adapters);
    }

    /// <summary>Rebuilds the per-session Highlighting submenu: one checkable item per rule
    /// (checked = effective for this session), plus a reset. Toggles are stored on the
    /// session as enable/disable deltas against the global state — never copies.</summary>
    private void ConfigureHighlightMenu(TabViewModel tab, bool sessionExists)
    {
        _highlight.Items.Clear();
        _highlight.IsEnabled = sessionExists; // deltas persist on the saved session
        if (!sessionExists)
            return;

        var overrides = tab.Session.Overrides;
        foreach (var rule in App.Highlights.AllRules)
        {
            var effective = overrides?.DisabledRules?.Contains(rule.Id) == true
                ? false
                : overrides?.EnabledRules?.Contains(rule.Id) == true || rule.Enabled;
            var item = new ToggleMenuFlyoutItem { Text = rule.Name, IsChecked = effective };
            var captured = rule;
            item.Click += (_, _) =>
            {
                if (_menuTab is { } menuTab)
                    ToggleHighlightRule(menuTab, captured, item.IsChecked);
            };
            _highlight.Items.Add(item);
        }

        _highlight.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem
        {
            Text = "Reset to Global Defaults",
            IsEnabled = (overrides?.EnabledRules?.Count ?? 0) > 0 || (overrides?.DisabledRules?.Count ?? 0) > 0,
        };
        reset.Click += (_, _) =>
        {
            if (_menuTab is not { } menuTab)
                return;
            var current = menuTab.Session.Overrides;
            if (current is null)
                return;
            var cleared = current with { EnabledRules = null, DisabledRules = null };
            _host.ViewModel.UpdateSession(
                menuTab.Session with { Overrides = cleared.IsEmpty ? null : cleared }, null);
        };
        _highlight.Items.Add(reset);
    }

    private void ToggleHighlightRule(TabViewModel tab, HighlightRule rule, bool nowEnabled)
    {
        var session = tab.Session;
        var current = session.Overrides ?? new TerminalOverrides();
        var enabled = new HashSet<string>(current.EnabledRules ?? [], StringComparer.Ordinal);
        var disabled = new HashSet<string>(current.DisabledRules ?? [], StringComparer.Ordinal);
        enabled.Remove(rule.Id);
        disabled.Remove(rule.Id);
        // rule.Enabled is the effective global state; only deviations are stored.
        if (nowEnabled != rule.Enabled)
            (nowEnabled ? enabled : disabled).Add(rule.Id);

        var overrides = current with
        {
            EnabledRules = enabled.Count > 0 ? enabled.OrderBy(s => s, StringComparer.Ordinal).ToList() : null,
            DisabledRules = disabled.Count > 0 ? disabled.OrderBy(s => s, StringComparer.Ordinal).ToList() : null,
        };
        _host.ViewModel.UpdateSession(
            session with { Overrides = overrides.IsEmpty ? null : overrides }, null);
    }

    private async Task RenameTabAsync(TabViewModel tab)
    {
        var box = new TextBox { Text = tab.Header, PlaceholderText = "Tab title" };
        var dialog = new ContentDialog
        {
            Title = "Rename Tab",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
            tab.TitleOverride = box.Text.Trim();
    }

    // ---- drag between groups ----

    public void SetContentDropTargetVisible(bool visible)
    {
        ContentDropSurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            ContentDropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Tabs_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        // Pinned tabs stay put at the front of their group.
        if (args.Item is TabViewModel { IsPinned: true })
        {
            args.Cancel = true;
            return;
        }
        _draggedTab = args.Item as TabViewModel;
        _dragSource = this;
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        _host.SetTabContentDropTargetsVisible(true);
    }

    private void Tabs_TabStripDragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is not null)
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void Tabs_TabStripDrop(object sender, DragEventArgs e)
    {
        if (_draggedTab is not { } tab || _dragSource == this)
            return; // reorders within a group are handled natively by the TabView

        MoveDraggedTabIntoGroup(tab, e.GetPosition(Tabs).X);
        EndTabDrag();
    }

    private void TabStripHost_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is not null && _dragSource != this)
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void TabStripHost_Drop(object sender, DragEventArgs e)
    {
        if (_draggedTab is not { } tab || _dragSource == this)
            return;

        MoveDraggedTabIntoGroup(tab, e.GetPosition(Tabs).X);
        EndTabDrag();
    }

    private void MoveDraggedTabIntoGroup(TabViewModel tab, double pointerX)
    {
        // Insert at the position the tab was dropped. The surrounding TabStripHost also
        // receives drops over the unused portion of the strip, where WinUI's TabStripDrop
        // is not raised; in that case the loop naturally appends the tab.
        var index = Group.Tabs.Count;
        for (var i = 0; i < Group.Tabs.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is TabViewItem item)
            {
                var bounds = item.TransformToVisual(Tabs).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, item.ActualWidth, item.ActualHeight));
                if (pointerX < bounds.X + bounds.Width / 2)
                {
                    index = i;
                    break;
                }
            }
        }

        // The target host owns the transfer because each window has its own view model
        // and terminal visual tree.
        _host.TransferTabToGroup(
            tab,
            _dragSource!._host,
            Group,
            Math.Max(index, Group.Tabs.Count(t => t.IsPinned)));
    }

    private void ContentDropSurface_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is null || _dragSource is null)
            return;

        var sourceGroup = _dragSource.Group;
        // Splitting a group's only tab around itself would collapse the source and produce
        // no visible change. A lone tab can still be moved to split another group.
        if (ReferenceEquals(sourceGroup, Group) && sourceGroup.Tabs.Count <= 1)
            return;

        var position = e.GetPosition(ContentDropSurface);
        _dropDirection = SplitDropTarget.Resolve(
            position.X, position.Y, ContentDropSurface.ActualWidth, ContentDropSurface.ActualHeight);
        PositionDropOverlay(_dropDirection);

        ContentDropOverlay.Visibility = Visibility.Visible;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void PositionDropOverlay(SplitDirection direction)
    {
        ContentDropOverlay.Width = double.NaN;
        ContentDropOverlay.Height = double.NaN;
        ContentDropOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
        ContentDropOverlay.VerticalAlignment = VerticalAlignment.Stretch;

        switch (direction)
        {
            case SplitDirection.Left:
                ContentDropOverlay.Width = ContentDropSurface.ActualWidth / 2;
                ContentDropOverlay.HorizontalAlignment = HorizontalAlignment.Left;
                break;
            case SplitDirection.Right:
                ContentDropOverlay.Width = ContentDropSurface.ActualWidth / 2;
                ContentDropOverlay.HorizontalAlignment = HorizontalAlignment.Right;
                break;
            case SplitDirection.Up:
                ContentDropOverlay.Height = ContentDropSurface.ActualHeight / 2;
                ContentDropOverlay.VerticalAlignment = VerticalAlignment.Top;
                break;
            case SplitDirection.Down:
                ContentDropOverlay.Height = ContentDropSurface.ActualHeight / 2;
                ContentDropOverlay.VerticalAlignment = VerticalAlignment.Bottom;
                break;
        }
    }

    private void ContentDropSurface_DragLeave(object sender, DragEventArgs e) =>
        ContentDropOverlay.Visibility = Visibility.Collapsed;

    private void ContentDropSurface_Drop(object sender, DragEventArgs e)
    {
        if (_draggedTab is not { } tab || _dragSource is null)
            return;

        var sourceGroup = _dragSource.Group;
        if (ReferenceEquals(sourceGroup, Group) && sourceGroup.Tabs.Count <= 1)
            return;

        var direction = _dropDirection;
        var sourceHost = _dragSource._host;
        if (!ReferenceEquals(sourceHost, _host))
            _host.TransferTabToGroup(tab, sourceHost, Group, Group.Tabs.Count);
        EndTabDrag();
        _host.SplitTab(tab, Group, direction);
    }

    private void Tabs_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        // WinUI can raise completion on the source before TabStripDrop reaches another
        // TabView. Keep the in-process handoff alive through the current dispatcher turn.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_dragSource == this)
                EndTabDrag();
        });
    }

    private void EndTabDrag()
    {
        _host.SetTabContentDropTargetsVisible(false);
        _draggedTab = null;
        _dragSource = null;
    }
}
