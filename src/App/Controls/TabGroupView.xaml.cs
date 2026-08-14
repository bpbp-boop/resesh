using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sessions.App.ViewModels;

namespace Sessions.App.Controls;

/// <summary>What a tab group needs from the window: dialogs, group management, the single close pathway.</summary>
public interface ITabGroupHost
{
    MainViewModel ViewModel { get; }
    void FocusGroup(TabGroupViewModel group);
    Task RequestCloseTabAsync(TabViewModel tab);
    Task RequestCloseManyAsync(IReadOnlyList<TabViewModel> tabs, string description);
    void SplitRight(TabViewModel tab);
    void MoveToOtherGroup(TabViewModel tab);
    void MoveTabBetweenGroups(TabViewModel tab, TabGroupViewModel targetGroup, int targetIndex);
    void CloneSession(TabViewModel tab);
    void TogglePin(TabViewModel tab);
    Task OpenSessionOptionsAsync(TabViewModel tab);
    Task LockSessionAsync(TabViewModel tab);
    void ReconnectTab(TabViewModel tab);
    void DisconnectTab(TabViewModel tab);
    Task EndRemoteSessionAsync(TabViewModel tab);
}

public sealed partial class TabGroupView : UserControl
{
    // In-process handoff for cross-group tab drags.
    private static TabViewModel? _draggedTab;
    private static TabGroupView? _dragSource;

    private readonly ITabGroupHost _host;
    private readonly MenuFlyout _tabMenu;
    private TabViewModel? _menuTab;
    private TabViewModel? _middleClickTab;

    public TabGroupViewModel Group { get; }

    public TabGroupView(TabGroupViewModel group, ITabGroupHost host)
    {
        Group = group;
        _host = host;
        InitializeComponent();
        _tabMenu = BuildTabMenu();

        // Focus tracking: interacting anywhere in this group focuses it.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _host.FocusGroup(Group)), true);

        // Middle-click closes a tab (same confirmed pathway); TabView has no built-in support.
        Tabs.AddHandler(PointerPressedEvent, new PointerEventHandler(Tabs_PointerPressed), true);
        Tabs.AddHandler(PointerReleasedEvent, new PointerEventHandler(Tabs_PointerReleased), true);
        Tabs.AddHandler(RightTappedEvent, new RightTappedEventHandler(Tabs_RightTapped), true);
    }

    // ---- terminal hosting ----

    public void AddTerminal(UIElement view)
    {
        TerminalHost.Children.Add(view);
        SyncTerminalVisibility();
    }

    public void RemoveTerminal(UIElement view)
    {
        TerminalHost.Children.Remove(view);
        SyncTerminalVisibility();
    }

    public void SyncTerminalVisibility()
    {
        var selected = Group.SelectedTab?.View;
        foreach (var child in TerminalHost.Children)
            child.Visibility = ReferenceEquals(child, selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncTerminalVisibility();
        _host.FocusGroup(Group);
        _host.ViewModel.NotifyActiveTabChanged();
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
        _middleClickTab = point.Properties.IsMiddleButtonPressed ? TabFromOriginalSource(e.OriginalSource) : null;
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
        _closeGroup = null!, _closeAll = null!, _pin = null!, _lock = null!, _clone = null!, _split = null!, _options = null!;

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
        _split = Item("Split Right", tab =>
        {
            if (_host.ViewModel.IsSplit)
                _host.MoveToOtherGroup(tab);
            else
                _host.SplitRight(tab);
        });
        _split.KeyboardAcceleratorTextOverride = "Ctrl+Shift+\\";
        _options = Item("Session Options…", tab => _ = _host.OpenSessionOptionsAsync(tab));

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
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_options);
        return menu;
    }

    private void ConfigureMenuFor(TabViewModel tab)
    {
        _resetName.IsEnabled = tab.TitleOverride is not null;
        _reconnect.IsEnabled = tab.State == TabConnectionState.Disconnected;
        _disconnect.IsEnabled = tab.State == TabConnectionState.Connected;
        // Only persistent sessions have a remote session to end; close/disconnect only detach them.
        _endRemote.Visibility = tab.Session.Persistent ? Visibility.Visible : Visibility.Collapsed;
        _endRemote.IsEnabled = tab.Session.Persistent && tab.State == TabConnectionState.Connected;
        // Bulk closes skip pinned tabs, so they're only offered when an unpinned tab qualifies.
        _closeDisconnected.IsEnabled = Group.Tabs.Any(t => t.State == TabConnectionState.Disconnected && !t.IsPinned);
        _closeOthers.IsEnabled = Group.Tabs.Any(t => t != tab && !t.IsPinned);
        _closeRight.IsEnabled = Group.Tabs.Skip(Group.Tabs.IndexOf(tab) + 1).Any(t => !t.IsPinned);
        _pin.Text = tab.IsPinned ? "Unpin Tab" : "Pin Tab";
        _lock.IsEnabled = !tab.IsLocked;
        _split.Text = _host.ViewModel.IsSplit ? "Move to Other Group" : "Split Right";
        // Splitting a lone tab would leave an empty group that immediately collapses — pointless.
        _split.IsEnabled = _host.ViewModel.IsSplit || Group.Tabs.Count > 1;
        // Session Options is disabled when the saved session was deleted while connected.
        _options.IsEnabled = _host.ViewModel.RankedMatches("").Any(s => s.Id == tab.Session.Id);
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

        // Insert at the position the tab was dropped.
        var index = Group.Tabs.Count;
        var position = e.GetPosition(Tabs);
        for (var i = 0; i < Group.Tabs.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is TabViewItem item)
            {
                var bounds = item.TransformToVisual(Tabs).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, item.ActualWidth, item.ActualHeight));
                if (position.X < bounds.X + bounds.Width / 2)
                {
                    index = i;
                    break;
                }
            }
        }

        // Never land in front of the target group's pinned tabs.
        _host.MoveTabBetweenGroups(tab, Group, Math.Max(index, Group.Tabs.Count(t => t.IsPinned)));
        _draggedTab = null;
        _dragSource = null;
    }

    private void Tabs_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        _draggedTab = null;
        _dragSource = null;
    }
}
