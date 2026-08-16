using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sessions.App.Controls;
using Sessions.App.Dialogs;
using Sessions.App.Terminal;
using Sessions.App.ViewModels;
using Sessions.Core.Layout;
using Sessions.Core.Models;
using Sessions.Core.Storage;
using Windows.System;

namespace Sessions.App;

public sealed partial class MainWindow : Window, ITabGroupHost
{
    public MainViewModel ViewModel { get; }

    private readonly Dictionary<TabGroupViewModel, TabGroupView> _groupViews = [];
    private readonly SplitLayout<TabGroupViewModel> _groupLayout;

    public MainWindow()
    {
        ViewModel = new MainViewModel(App.Store, App.Credentials);
        _groupLayout = new SplitLayout<TabGroupViewModel>(ViewModel.Groups[0]);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        InitializeTitleBar();
        // Lets the icon catalog rasterize at true device pixels (XamlRoot is null until
        // the content loads; the catalog falls back to scale 1 and re-renders on demand).
        App.Icons.ScaleProvider = () => Root.XamlRoot?.RasterizationScale ?? 1.0;
        AttachGroupView(ViewModel.Groups[0]);
        RebuildGroupLayout();
        ViewModel.TreeRebuilt += () =>
        {
            ClearSelection(); // rebuild recreates every node; stale references would leak
            ScheduleExpansionSync();
            SyncEmptyState();
        };
        ScheduleExpansionSync();
        SyncEmptyState();
        ApplySettingsToApp();
        RegisterAccelerators();
        if (App.Settings.Current.TreePaneWidth is { } treeWidth)
            TreeColumn.Width = new GridLength(Math.Clamp(treeWidth, 180, 800));
        Closed += (_, _) =>
        {
            SaveTreePaneWidth();
            ViewModel.CloseAllTabs(); // tear down live SSH sessions without hanging
        };
    }

    private TabGroupView AttachGroupView(TabGroupViewModel group)
    {
        var view = new TabGroupView(group, this);
        _groupViews[group] = view;
        return view;
    }

    private void RegisterAccelerators()
    {
        // Ctrl+F4: close active tab. Ctrl+Shift+\: split the active tab to the right.
        // (Both also work while the terminal has focus — the xterm page forwards Ctrl+F4.)
        var closeTab = new KeyboardAccelerator { Key = VirtualKey.F4, Modifiers = VirtualKeyModifiers.Control };
        closeTab.Invoked += (sender, e) =>
        {
            e.Handled = true;
            if (ViewModel.ActiveTab is { } tab)
                _ = RequestCloseTabAsync(tab);
        };
        var split = new KeyboardAccelerator
        {
            Key = (VirtualKey)220, // VK_OEM_5, the '\' key
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        split.Invoked += (_, e) =>
        {
            e.Handled = true;
            if (ViewModel.ActiveTab is { } tab && ViewModel.GroupOf(tab).Tabs.Count > 1)
                SplitRight(tab);
        };
        // Ctrl+Shift+E: toggle the active tab's file pane (also forwarded by the xterm page).
        var filePane = new KeyboardAccelerator
        {
            Key = VirtualKey.E,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        filePane.Invoked += (_, e) =>
        {
            e.Handled = true;
            if (ViewModel.ActiveTab is { } tab)
                ToggleFilePane(tab);
        };
        // Ctrl+K: focus the quick connect box (only while a XAML control has focus;
        // keystrokes inside the terminal's WebView2 never reach these accelerators).
        var quickConnect = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        quickConnect.Invoked += (_, e) =>
        {
            e.Handled = true;
            QuickConnectBox.Focus(FocusState.Programmatic);
        };
        Root.KeyboardAccelerators.Add(closeTab);
        Root.KeyboardAccelerators.Add(split);
        Root.KeyboardAccelerators.Add(filePane);
        Root.KeyboardAccelerators.Add(quickConnect);
    }

    // ---- Custom title bar ----

    /// <summary>
    /// Merges the app content into the title bar: the 48px AppTitleBar row hosts the
    /// menus, quick connect box and window buttons; the system draws only the caption
    /// buttons. Interactive controls are punched out of the drag region with
    /// passthrough rects, which must be recomputed on every layout/scale change.
    /// </summary>
    private void InitializeTitleBar()
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            AppTitleBar.Height = double.NaN; // Win10 fallback: keep the row as a plain toolbar
            return;
        }
        // The Window-level property (not AppWindow.TitleBar's) is what installs the
        // fallback drag region across the top strip; without it nothing is draggable.
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        ApplyTitleBarButtonColors();
        TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
            new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")));
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        // Re-assert regions on every activation: creating a WebView2 (e.g. opening the
        // second split group) can transiently disturb the non-client drag region.
        Activated += (_, _) => UpdateTitleBarRegions();
        // The center/right blocks move without AppTitleBar itself resizing (e.g. the
        // MenuBar collapsing items), so track them individually too.
        TitleBarMenus.SizeChanged += (_, _) => UpdateTitleBarRegions();
        QuickConnectHost.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBarButtons.SizeChanged += (_, _) => UpdateTitleBarRegions();
    }

    /// <summary>Caption buttons live outside XAML theming; keep them in sync with the app theme.</summary>
    private void ApplyTitleBarButtonColors()
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            return;
        var tb = AppWindow.TitleBar;
        var dark = App.Settings.Current.Theme != "light";
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
        tb.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(25, 255, 255, 255)
            : Windows.UI.Color.FromArgb(25, 0, 0, 0);
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(50, 255, 255, 255)
            : Windows.UI.Color.FromArgb(50, 0, 0, 0);
        tb.ButtonPressedForegroundColor = fg;
    }

    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is null || !AppWindow.TitleBar.ExtendsContentIntoTitleBar)
            return;
        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        TitleBarLeftPadding.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale);
        TitleBarRightPadding.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        var rects = new List<Windows.Graphics.RectInt32>();
        foreach (var el in new FrameworkElement[] { TitleBarMenus, QuickConnectHost, TitleBarButtons })
        {
            if (el.ActualWidth == 0 || el.ActualHeight == 0)
                continue;
            var bounds = el.TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rects.Add(new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }
        var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        // Explicit caption strip: don't rely on the framework's fallback drag region,
        // which proved flaky right after layout changes. Passthrough wins where they overlap.
        source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption,
            [new Windows.Graphics.RectInt32(0, 0,
                (int)Math.Round(AppTitleBar.ActualWidth * scale),
                (int)Math.Round(AppTitleBar.ActualHeight * scale))]);
        source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [.. rects]);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = PinButton.IsChecked == true;
    }

    // Menu items act on the focused group's selected tab; they no-op when idle.
    private void SplitRightMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is not { } tab)
            return;
        if (ViewModel.GroupOf(tab).Tabs.Count > 1)
            SplitRight(tab);
    }

    private void SplitDownMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab && ViewModel.GroupOf(tab).Tabs.Count > 1)
            SplitDown(tab);
    }

    private void FilePaneMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            ToggleFilePane(tab);
    }

    private void ReconnectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            ReconnectTab(tab);
    }

    private void DisconnectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            DisconnectTab(tab);
    }

    private void CloneMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            CloneSession(tab);
    }

    private void PinTabMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            TogglePin(tab);
    }

    private void SessionOptionsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            _ = OpenSessionOptionsAsync(tab);
    }

    private void EndRemoteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            _ = EndRemoteSessionAsync(tab);
    }

    private void CloseTabMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTab is { } tab)
            _ = RequestCloseTabAsync(tab);
    }

    // ---- Quick connect ----

    private void QuickConnect_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        UpdateQuickConnectHint();
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        var text = sender.Text.Trim();
        var items = new List<QuickConnectSuggestion>();
        if (TryParseSshTarget(text, out var adhoc))
        {
            items.Add(new QuickConnectSuggestion
            {
                Display = $"Connect to {adhoc.Username}@{adhoc.Host}" + (adhoc.Port != 22 ? $":{adhoc.Port}" : ""),
                Detail = "new connection",
                Glyph = "\uE768",
                Session = adhoc,
            });
        }
        if (text.Length > 0)
        {
            items.AddRange(ViewModel.RankedMatches(text).Take(8).Select(s => new QuickConnectSuggestion
            {
                Display = s.Name,
                Detail = $"{s.Username}@{s.Host}" + (s.Port != 22 ? $":{s.Port}" : ""),
                Glyph = "\uE756",
                Session = s,
            }));
        }
        sender.ItemsSource = items;
    }

    private void QuickConnect_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var target = (args.ChosenSuggestion as QuickConnectSuggestion)?.Session;
        if (target is null)
        {
            var text = args.QueryText.Trim();
            if (text.Length == 0)
                return;
            target = TryParseSshTarget(text, out var adhoc)
                ? adhoc
                : ViewModel.RankedMatches(text).FirstOrDefault();
        }
        if (target is null)
            return;
        ConnectSession(target);
        sender.Text = "";
        sender.ItemsSource = null;
    }

    private void QuickConnect_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            QuickConnectBox.Text = "";
            QuickConnectBox.ItemsSource = null;
            e.Handled = true;
        }
    }

    private void QuickConnect_FocusChanged(object sender, RoutedEventArgs e) => UpdateQuickConnectHint();

    private void UpdateQuickConnectHint() =>
        QuickConnectHint.Visibility =
            QuickConnectBox.Text.Length == 0 && QuickConnectBox.FocusState == FocusState.Unfocused
                ? Visibility.Visible
                : Visibility.Collapsed;

    /// <summary>
    /// Parses "ssh user@host", "user@host:2222" etc. into an ad-hoc (unsaved) session.
    /// A bare hostname only counts with an explicit "ssh " prefix, so plain words keep
    /// meaning "search my saved sessions".
    /// </summary>
    private static bool TryParseSshTarget(string input, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Session? session)
    {
        session = null;
        var text = input.Trim();
        var explicitSsh = text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase);
        if (explicitSsh)
            text = text[4..].Trim();
        if (text.Length == 0 || text.Contains(' ') || (!explicitSsh && !text.Contains('@')))
            return false;

        var user = "";
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at];
            text = text[(at + 1)..];
        }
        var port = 22;
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535)
                return false;
            text = text[..colon];
        }
        if (text.Length == 0 || at == 0)
            return false;

        var username = user.Length > 0 ? user : Environment.UserName;
        session = new Session
        {
            Name = $"{username}@{text}",
            Host = text,
            Port = port,
            Username = username,
            AuthMethod = AuthMethod.Password,
        };
        return true;
    }

    /// <summary>Opens a tab for the session and starts its terminal + SSH lifecycle.</summary>
    private TabViewModel ConnectSession(Session session, TabGroupViewModel? group = null)
    {
        var tab = ViewModel.Connect(session, group);
        var view = new TerminalTabView(tab, App.Credentials, App.KnownHosts);
        view.CloseRequested += () => _ = RequestCloseTabAsync(tab);
        view.UnlockRequested += () => _ = HandleUnlockAsync(tab, view);
        view.IconSuggested += key =>
        {
            // Re-check the stored copy: a manual choice made while connecting must win,
            // and ad-hoc/deleted sessions (not in the store) are left alone.
            if (App.Store.Find(tab.Session.Id) is { Icon: null } current)
                ViewModel.UpdateSession(current with { Icon = key }, null);
        };
        view.SplitRequested += () =>
        {
            if (ViewModel.GroupOf(tab).Tabs.Count > 1)
                SplitRight(tab);
        };
        tab.View = view;
        _groupViews[ViewModel.GroupOf(tab)].AddTerminal(view);
        view.SetRulerPresentation(ViewModel.IsSplit, tab.IsGroupFocused);
        return tab;
    }

    // ---- ITabGroupHost ----

    public void FocusGroup(TabGroupViewModel group)
    {
        ViewModel.FocusedGroup = group;
        ViewModel.NotifyActiveTabChanged();
        UpdateRulerPresentations();
    }

    private void UpdateRulerPresentations()
    {
        foreach (var tab in ViewModel.AllTabs)
        {
            if (tab.View is TerminalTabView view)
                view.SetRulerPresentation(ViewModel.IsSplit, tab.IsGroupFocused);
        }
    }

    /// <summary>THE close pathway: X button, Ctrl+F4, context menu, and middle-click all land here.</summary>
    public async Task RequestCloseTabAsync(TabViewModel tab)
    {
        var detail = tab.State != TabConnectionState.Connected
            ? ""
            : tab.Session.Persistent
                ? " The remote session keeps running (persistent) — connecting again resumes it."
                : " The session is still connected.";
        if (tab.IsPinned)
        {
            // Pinned tabs need the extra deliberate step; one dialog covers both.
            if (!await ConfirmAsync("Tab Is Pinned", $"\"{tab.Header}\" is pinned.{detail}", "Unpin and Close"))
                return;
            TogglePin(tab);
            CloseTabCore(tab);
            return;
        }
        if (await ConfirmAsync("Close Tab", $"Close \"{tab.Header}\"?{detail}", "Close"))
            CloseTabCore(tab);
    }

    public async Task RequestCloseManyAsync(IReadOnlyList<TabViewModel> tabs, string description)
    {
        tabs = tabs.Where(t => !t.IsPinned).ToList(); // bulk closes never touch pinned tabs
        if (tabs.Count == 0)
            return;
        var connected = tabs.Count(t => t.State == TabConnectionState.Connected);
        var message = connected > 0
            ? $"Close {tabs.Count} {description}? {connected} of them {(connected == 1 ? "is" : "are")} still connected."
            : $"Close {tabs.Count} {description}?";
        if (!await ConfirmAsync("Close Tabs", message, "Close"))
            return;
        foreach (var tab in tabs)
            CloseTabCore(tab);
    }

    private void CloseTabCore(TabViewModel tab)
    {
        var group = ViewModel.GroupOf(tab);
        if (tab.View is TerminalTabView view)
            _groupViews[group].RemoveTerminal(view);
        ViewModel.CloseTab(tab);
        CollapseGroupIfEmpty(group);
    }

    public void SplitRight(TabViewModel tab)
    {
        var sourceGroup = ViewModel.GroupOf(tab);
        if (sourceGroup.Tabs.Count > 1)
            SplitTab(tab, sourceGroup, SplitDirection.Right);
    }

    public void SplitDown(TabViewModel tab)
    {
        var sourceGroup = ViewModel.GroupOf(tab);
        if (sourceGroup.Tabs.Count > 1)
            SplitTab(tab, sourceGroup, SplitDirection.Down);
    }

    public void SplitTab(TabViewModel tab, TabGroupViewModel targetGroup, SplitDirection direction)
    {
        var sourceGroup = ViewModel.GroupOf(tab);
        if (ReferenceEquals(sourceGroup, targetGroup) && sourceGroup.Tabs.Count <= 1)
            return;

        var newGroup = new TabGroupViewModel();
        _groupLayout.Split(targetGroup, newGroup, direction);
        ViewModel.Groups.Add(newGroup);
        SyncGroupOrder();
        AttachGroupView(newGroup);
        ViewModel.OnGroupsChanged();

        MoveTabBetweenGroups(tab, newGroup, 0);
        RebuildGroupLayout();
    }

    public void SetTabContentDropTargetsVisible(bool visible)
    {
        foreach (var groupView in _groupViews.Values)
            groupView.SetContentDropTargetVisible(visible);
    }

    public void MoveTabBetweenGroups(TabViewModel tab, TabGroupViewModel targetGroup, int targetIndex)
    {
        var source = ViewModel.GroupOf(tab);
        if (source == targetGroup)
            return;

        source.Tabs.Remove(tab);
        if (source.SelectedTab == tab)
            source.SelectedTab = source.Tabs.LastOrDefault();

        targetGroup.Tabs.Insert(Math.Clamp(targetIndex, 0, targetGroup.Tabs.Count), tab);
        targetGroup.SelectedTab = tab;

        if (tab.View is TerminalTabView view)
        {
            _groupViews[source].RemoveTerminal(view);
            _groupViews[targetGroup].AddTerminal(view);
        }

        FocusGroup(targetGroup);
        // The setter no-ops when targetGroup was already focused; the moved tab still
        // needs its group-focus flag refreshed.
        ViewModel.SyncGroupFocus();
        CollapseGroupIfEmpty(source);
    }

    private void CollapseGroupIfEmpty(TabGroupViewModel group)
    {
        if (!ViewModel.IsSplit || group.Tabs.Count > 0)
            return;

        if (!_groupLayout.Remove(group))
            return;
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_groupViews[group]) is Panel parent)
            parent.Children.Remove(_groupViews[group]);
        _groupViews.Remove(group);
        ViewModel.Groups.Remove(group);
        SyncGroupOrder();

        if (ReferenceEquals(ViewModel.FocusedGroup, group))
            FocusGroup(_groupLayout.Values[0]);
        ViewModel.OnGroupsChanged();
        RebuildGroupLayout();
    }

    private void SyncGroupOrder()
    {
        var ordered = _groupLayout.Values;
        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var currentIndex = ViewModel.Groups.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
                ViewModel.Groups.Move(currentIndex, targetIndex);
        }
    }

    private void RebuildGroupLayout()
    {
        // A group view keeps its parent even after the old root grid leaves the visual tree.
        // Detach every leaf before the recursive layout is rebuilt around the same views.
        foreach (var groupView in _groupViews.Values)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(groupView) is Panel parent)
                parent.Children.Remove(groupView);
        }

        GroupArea.Children.Clear();
        GroupArea.Children.Add(BuildGroupLayoutElement(_groupLayout.Root));
        UpdateRulerPresentations();
        // Re-assert after WebView2 controls settle into their new rows and columns.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarRegions);
    }

    private FrameworkElement BuildGroupLayoutElement(SplitLayoutNode<TabGroupViewModel> node)
    {
        if (node is SplitLayoutLeaf<TabGroupViewModel> leaf)
            return _groupViews[leaf.Value];

        var branch = (SplitLayoutBranch<TabGroupViewModel>)node;
        var grid = new Grid();
        var isColumns = branch.Orientation == SplitOrientation.Columns;

        for (var index = 0; index < branch.Children.Count; index++)
        {
            var gridIndex = index * 2;
            if (isColumns)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            else
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var child = BuildGroupLayoutElement(branch.Children[index]);
            Grid.SetColumn(child, 0);
            Grid.SetRow(child, 0);
            if (isColumns)
                Grid.SetColumn(child, gridIndex);
            else
                Grid.SetRow(child, gridIndex);
            grid.Children.Add(child);

            if (index == branch.Children.Count - 1)
                continue;

            if (isColumns)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            else
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            var splitter = new CommunityToolkit.WinUI.Controls.GridSplitter
            {
                ResizeBehavior = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeBehavior.PreviousAndNext,
                ResizeDirection = isColumns
                    ? CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns
                    : CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            if (isColumns)
                Grid.SetColumn(splitter, gridIndex + 1);
            else
                Grid.SetRow(splitter, gridIndex + 1);
            grid.Children.Add(splitter);
        }

        return grid;
    }

    public void CloneSession(TabViewModel tab) =>
        ConnectSession(tab.Session, ViewModel.GroupOf(tab));

    // ---- pinning (browser-style; pinned session ids persist and reopen on launch) ----

    public void TogglePin(TabViewModel tab)
    {
        tab.IsPinned = !tab.IsPinned;
        if (tab.IsPinned)
        {
            // Pinned tabs live at the front of their group, after any already-pinned tabs.
            var group = ViewModel.GroupOf(tab);
            var selected = group.SelectedTab;
            var index = group.Tabs.IndexOf(tab);
            var target = group.Tabs.Count(t => t.IsPinned) - 1;
            if (index != target)
                group.Tabs.Move(index, target);
            group.SelectedTab = selected; // the move must not steal selection
        }
        SavePinnedSessions();
    }

    private void SavePinnedSessions() =>
        App.Settings.Save(App.Settings.Current with
        {
            PinnedSessionIds = ViewModel.AllTabs.Where(t => t.IsPinned).Select(t => t.Session.Id).Distinct().ToList(),
        });

    /// <summary>Reopens and reconnects the pinned sessions from the last run; called once at launch.</summary>
    public void RestorePinnedSessions()
    {
        var ids = App.Settings.Current.PinnedSessionIds;
        if (ids.Count == 0)
            return;
        var restored = new List<Guid>();
        foreach (var id in ids)
        {
            if (App.Store.Find(id) is { } session)
            {
                ConnectSession(session).IsPinned = true;
                restored.Add(id);
            }
        }
        // Sessions deleted since the last run drop out of the pinned list.
        if (restored.Count != ids.Count)
            App.Settings.Save(App.Settings.Current with { PinnedSessionIds = restored });
    }

    public async Task OpenSessionOptionsAsync(TabViewModel tab)
    {
        var current = App.Store.Find(tab.Session.Id);
        if (current is null)
            return;
        var notice = tab.State == TabConnectionState.Connected
            ? "This tab is connected — changes to host, port, or authentication apply on the next connect."
            : null;
        var dialog = new SessionEditDialog(ViewModel.FolderPathsForPicker, current, current.FolderPath, notice)
        {
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
        if (dialog.Result is { } result)
            ViewModel.UpdateSession(result, dialog.Password);
    }

    public async Task LockSessionAsync(TabViewModel tab)
    {
        var box = new PasswordBox { Header = "Lock password (kept in memory only — not stored anywhere)" };
        var dialog = new ContentDialog
        {
            Title = "Lock Session",
            Content = box,
            PrimaryButtonText = "Lock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Password.Length == 0)
            return;
        tab.Lock(box.Password);
        (tab.View as TerminalTabView)?.ShowLockOverlay();
    }

    private async Task HandleUnlockAsync(TabViewModel tab, TerminalTabView view)
    {
        var wait = tab.LockoutUntil - DateTimeOffset.Now;
        if (wait > TimeSpan.Zero)
        {
            await new ContentDialog
            {
                Title = "Session Locked",
                Content = $"Too many failed attempts. Try again in {Math.Ceiling(wait.TotalSeconds)} seconds.",
                CloseButtonText = "OK",
                XamlRoot = Root.XamlRoot,
            }.ShowAsync();
            return;
        }

        var box = new PasswordBox { Header = "Unlock password" };
        var dialog = new ContentDialog
        {
            Title = "Unlock Session",
            Content = box,
            PrimaryButtonText = "Unlock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (tab.TryUnlock(box.Password))
        {
            view.HideLockOverlay();
        }
        else
        {
            var lockedOut = tab.LockoutUntil > DateTimeOffset.Now;
            await new ContentDialog
            {
                Title = "Wrong Password",
                Content = lockedOut
                    ? "Wrong password. Unlocking is now delayed for 30 seconds."
                    : "Wrong password.",
                CloseButtonText = "OK",
                XamlRoot = Root.XamlRoot,
            }.ShowAsync();
        }
    }

    public void ReconnectTab(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view && tab.State == TabConnectionState.Disconnected)
            _ = view.ConnectAsync(isReconnect: true);
    }

    public void DisconnectTab(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view && tab.State == TabConnectionState.Connected)
            view.DisconnectLocal();
    }

    public async Task EndRemoteSessionAsync(TabViewModel tab)
    {
        if (tab.View is not TerminalTabView view || tab.State != TabConnectionState.Connected)
            return;
        var confirmed = await ConfirmAsync(
            "End Remote Session",
            $"End the persistent session for \"{tab.Header}\" on the server? " +
            "Anything running inside it will be terminated. (Closing the tab merely detaches.)",
            "End Session");
        if (confirmed)
            view.EndRemoteSession();
    }

    public void ToggleFilePane(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view && !tab.IsLocked)
            view.ToggleFilePane();
    }

    public async Task OpenFilePaneAtCurrentFolderAsync(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view && !tab.IsLocked)
            await view.OpenFilePaneAtCurrentFolderAsync();
    }

    // ---- settings ----

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var current = App.Settings.Current;
        var theme = new ComboBox
        {
            Header = "Theme",
            ItemsSource = new[] { "dark", "light" },
            SelectedItem = current.Theme == "light" ? "light" : "dark",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var fontFamily = new TextBox { Header = "Terminal font family", Text = current.FontFamily };
        var fontSize = new NumberBox
        {
            Header = "Font size",
            Value = current.FontSize,
            Minimum = 8,
            Maximum = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var scrollback = new NumberBox
        {
            Header = "Scrollback lines",
            Value = current.Scrollback,
            Minimum = 1000,
            Maximum = 100000,
            SmallChange = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var copyOnSelect = new ToggleSwitch { Header = "Copy on select", IsOn = current.CopyOnSelect };
        var rightClickPaste = new ToggleSwitch { Header = "Right-click paste", IsOn = current.RightClickPaste };
        var highlighting = new Button { Content = "Keyword highlighting…" };

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 380,
                    Children = { theme, fontFamily, fontSize, scrollback, copyOnSelect, rightClickPaste, highlighting },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        // Only one ContentDialog may be open at a time: leave Settings, run the editor.
        // Highlight changes apply/persist immediately, so abandoning Settings loses nothing.
        highlighting.Click += async (_, _) =>
        {
            dialog.Hide();
            await Dialogs.HighlightEditorDialog.ShowAsync(Root.XamlRoot, ApplySettingsToApp);
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var updated = current with
        {
            Theme = theme.SelectedItem as string ?? "dark",
            FontFamily = string.IsNullOrWhiteSpace(fontFamily.Text) ? current.FontFamily : fontFamily.Text.Trim(),
            FontSize = double.IsNaN(fontSize.Value) ? current.FontSize : (int)fontSize.Value,
            Scrollback = double.IsNaN(scrollback.Value) ? current.Scrollback : (int)scrollback.Value,
            CopyOnSelect = copyOnSelect.IsOn,
            RightClickPaste = rightClickPaste.IsOn,
        };
        App.Settings.Save(updated);
        ApplySettingsToApp();
    }

    /// <summary>Applies the persisted settings to the shell theme and every open terminal.</summary>
    private void ApplySettingsToApp()
    {
        var settings = App.Settings.Current;
        Root.RequestedTheme = settings.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        ApplyTitleBarButtonColors(); // caption buttons don't follow XAML theming
        foreach (var tab in ViewModel.AllTabs)
        {
            if (tab.View is TerminalTabView view)
                view.ApplySettings(settings);
        }
    }

    private void SyncEmptyState() =>
        EmptyState.Visibility = App.Store.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- tree pane persistence ----

    private void TreeSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        SaveTreePaneWidth();

    private void SaveTreePaneWidth()
    {
        if (TreeColumn.ActualWidth > 0)
            App.Settings.Save(App.Settings.Current with { TreePaneWidth = TreeColumn.ActualWidth });
    }

    /// <summary>
    /// TreeViewItem ignores IsExpanded applied while its children aren't realized yet
    /// (container recycling), so after each rebuild we push the view-model state onto the
    /// realized containers, re-queueing until the tree settles (expanding a node realizes
    /// its children on a later tick).
    /// </summary>
    private void ScheduleExpansionSync(int remainingPasses = 10)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var changed = false;

            void Sync(IEnumerable<TreeNodeViewModel> nodes)
            {
                foreach (var node in nodes.Where(n => n.IsFolder))
                {
                    if (SessionTree.ContainerFromItem(node) is TreeViewItem container)
                    {
                        if (container.IsExpanded != node.IsExpanded)
                        {
                            Trace($"sync: '{node.FolderPath}' container={container.IsExpanded} vm={node.IsExpanded} -> pushing");
                            container.IsExpanded = node.IsExpanded;
                            changed = true;
                        }
                        Sync(node.Children);
                    }
                    else if (node.IsExpanded)
                    {
                        Trace($"sync: '{node.FolderPath}' no container yet");
                        changed = true; // container not realized yet; try again next pass
                    }
                }
            }

            Sync(ViewModel.RootNodes);
            Trace($"sync pass done, changed={changed}, remaining={remainingPasses}");
            if (changed && remainingPasses > 0)
            {
                // Containers realize lazily across layout passes; space retries out in time
                // rather than burning them all within the same tick.
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(50);
                timer.IsRepeating = false;
                timer.Tick += (_, _) => ScheduleExpansionSync(remainingPasses - 1);
                timer.Start();
            }
        });
    }

    private static TreeNodeViewModel? NodeOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TreeNodeViewModel;

    [System.Diagnostics.Conditional("DEBUG")]
    internal static void Trace(string message)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sessions");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
    }

    // ---- Filter ----

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.SearchText = FilterBox.Text;

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Enter connects the best match and clears the filter.
            if (ViewModel.RankedMatches(FilterBox.Text).FirstOrDefault() is { } target)
            {
                ConnectSession(target);
                FilterBox.Text = "";
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            FilterBox.Text = "";
            e.Handled = true;
        }
    }

    // ---- Toolbar / root context menu ----

    private async void NewSession_Click(object sender, RoutedEventArgs e) =>
        await OpenSessionEditorAsync(existing: null, defaultFolder: "");

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New Folder", "Folder name", "");
        if (!string.IsNullOrWhiteSpace(name))
            ViewModel.CreateFolder(name);
    }

    // ---- SecureCRT import ----

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Core.Import.SecureCrtImporter.DefaultConfigSessionsPath;
            if (!Directory.Exists(dir))
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null)
                    return;
                dir = folder.Path;
            }

            var scan = Core.Import.SecureCrtImporter.Scan(dir);
            if (scan.Importable.Count == 0 && scan.Skipped.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "Import from SecureCRT",
                    Content = $"No SecureCRT session files (.ini) found under:\n{dir}",
                    CloseButtonText = "OK",
                    XamlRoot = Root.XamlRoot,
                }.ShowAsync();
                return;
            }

            var dialog = new ImportPreviewDialog(scan) { XamlRoot = Root.XamlRoot };
            await dialog.ShowAsync();
            if (dialog.Confirmed is not { Count: > 0 } confirmed)
                return;

            var (imported, duplicates) = Core.Import.SecureCrtImporter.Commit(App.Store, confirmed);
            ViewModel.RebuildTree();
            await new ContentDialog
            {
                Title = "Import complete",
                Content = duplicates == 0
                    ? $"Imported {imported} session(s)."
                    : $"Imported {imported} session(s); skipped {duplicates} duplicate(s).",
                CloseButtonText = "OK",
                XamlRoot = Root.XamlRoot,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "Import failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = Root.XamlRoot,
            }.ShowAsync();
        }
    }

    // ---- Tree selection (Explorer-style: click, Ctrl+click toggle, Shift+click range) ----

    private readonly List<TreeNodeViewModel> _selection = [];
    private TreeNodeViewModel? _selectionAnchor;

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ClearSelection()
    {
        foreach (var node in _selection)
            node.IsSelected = false;
        _selection.Clear();
        _selectionAnchor = null;
    }

    private void SelectOnly(TreeNodeViewModel node)
    {
        ClearSelection();
        node.IsSelected = true;
        _selection.Add(node);
        _selectionAnchor = node;
    }

    private void ToggleSelection(TreeNodeViewModel node)
    {
        if (node.IsSelected)
        {
            node.IsSelected = false;
            _selection.Remove(node);
        }
        else
        {
            node.IsSelected = true;
            _selection.Add(node);
        }
        _selectionAnchor = node;
    }

    /// <summary>Range select over the flattened visible tree, from the anchor to the clicked node.</summary>
    private void SelectRangeTo(TreeNodeViewModel node)
    {
        var anchor = _selectionAnchor;
        var visible = VisibleNodes().ToList();
        var from = anchor is null ? -1 : visible.IndexOf(anchor);
        var to = visible.IndexOf(node);
        if (from < 0 || to < 0)
        {
            SelectOnly(node);
            return;
        }
        ClearSelection();
        _selectionAnchor = anchor; // the anchor survives repeated Shift+clicks, as in Explorer
        var (lo, hi) = from <= to ? (from, to) : (to, from);
        for (var i = lo; i <= hi; i++)
        {
            visible[i].IsSelected = true;
            _selection.Add(visible[i]);
        }
    }

    /// <summary>Nodes in display order, skipping children of collapsed folders.</summary>
    private IEnumerable<TreeNodeViewModel> VisibleNodes()
    {
        static IEnumerable<TreeNodeViewModel> Walk(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                if (node.IsFolder && node.IsExpanded)
                    foreach (var child in Walk(node.Children))
                        yield return child;
            }
        }
        return Walk(ViewModel.RootNodes);
    }

    /// <summary>True when the tap landed on the expand/collapse chevron rather than the row content.</summary>
    private static bool IsChevronHit(object originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null and not TreeViewItem)
        {
            if (current is FrameworkElement { Name: "ExpandCollapseChevron" })
                return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void TreeNode_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node || IsChevronHit(e.OriginalSource))
            return;
        e.Handled = true; // taps bubble to ancestor folder items; only the innermost row counts
        if (IsKeyDown(VirtualKey.Shift))
            SelectRangeTo(node);
        else if (IsKeyDown(VirtualKey.Control))
            ToggleSelection(node);
        else
            SelectOnly(node);
    }

    private void SessionNode_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (NodeOf(sender)?.Session is { } session)
        {
            ConnectSession(session);
            e.Handled = true;
        }
    }

    // ---- Tree context menu (built per selection: session, folder, or multi) ----

    private void TreeNode_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (NodeOf(sender) is not { } node)
            return;
        args.Handled = true; // keep the TreeView's background flyout from also opening
        if (!node.IsSelected)
            SelectOnly(node); // right-click outside the selection retargets it, as in Explorer
        if (BuildSelectionMenu() is not { } menu)
            return;
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else
            menu.ShowAt((FrameworkElement)sender);
    }

    private MenuFlyout? BuildSelectionMenu()
    {
        if (_selection.Count == 0)
            return null;
        var menu = new MenuFlyout();

        // Single session: the original per-session menu.
        if (_selection is [{ Session: { } single }])
        {
            AddItem(menu, "Connect", () => ConnectSession(single));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(menu, "Edit…", async () => await OpenSessionEditorAsync(single, single.FolderPath));
            AddItem(menu, "Delete", async () => await DeleteSessionAsync(single));
            return menu;
        }

        var selection = _selection.ToList(); // snapshot: the live list mutates before Click fires
        AddItem(menu, "Connect in Tabs", () =>
        {
            foreach (var session in SessionsOf(selection))
                ConnectSession(session);
        });
        AddItem(menu, "Connect in Tabs in New Window", () => ConnectInNewWindow(SessionsOf(selection).ToList()));
        menu.Items.Add(new MenuFlyoutSeparator());

        // Single folder keeps its create/rename items; mixed selections get the shared subset.
        if (_selection is [{ IsFolder: true } folder])
        {
            AddItem(menu, "Expand All", () => SetFolderExpansion(folder, expanded: true));
            AddItem(menu, "Collapse All", () => SetFolderExpansion(folder, expanded: false));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(menu, "New Session…", async () => await OpenSessionEditorAsync(existing: null, defaultFolder: folder.FolderPath));
            AddItem(menu, "New Folder…", async () => await NewSubfolderAsync(folder));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(menu, "Rename…", async () => await RenameFolderAsync(folder));
            AddItem(menu, "Delete", async () => await DeleteFolderAsync(folder));
        }
        else
        {
            AddItem(menu, "Delete", async () => await DeleteSelectionAsync(selection));
        }
        return menu;
    }

    private static void AddItem(MenuFlyout menu, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    /// <summary>All sessions under a folder node, in display order (subfolders first, recursively).</summary>
    private static IEnumerable<Session> SessionsUnder(TreeNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            if (child.Session is { } session)
                yield return session;
            else
                foreach (var nested in SessionsUnder(child))
                    yield return nested;
        }
    }

    /// <summary>Sessions of the selection: selected sessions plus everything under selected folders, deduplicated.</summary>
    private static IEnumerable<Session> SessionsOf(IEnumerable<TreeNodeViewModel> nodes)
    {
        var seen = new HashSet<Guid>();
        foreach (var node in nodes)
        {
            if (node.Session is { } session)
            {
                if (seen.Add(session.Id))
                    yield return session;
            }
            else
            {
                foreach (var nested in SessionsUnder(node))
                    if (seen.Add(nested.Id))
                        yield return nested;
            }
        }
    }

    private void ConnectInNewWindow(IReadOnlyList<Session> sessions)
    {
        if (sessions.Count == 0)
            return;
        var window = new MainWindow();
        window.Activate();
        foreach (var session in sessions)
            window.ConnectSession(session);
    }

    private async Task NewSubfolderAsync(TreeNodeViewModel folder)
    {
        var name = await PromptAsync("New Folder", $"Folder name (inside {folder.FolderPath})", "");
        if (!string.IsNullOrWhiteSpace(name))
            ViewModel.CreateFolder(FolderPaths.Combine(folder.FolderPath, name));
    }

    private async Task RenameFolderAsync(TreeNodeViewModel folder)
    {
        var name = await PromptAsync("Rename Folder", "New name", folder.Name);
        if (string.IsNullOrWhiteSpace(name) || name == folder.Name)
            return;
        var newPath = FolderPaths.Combine(FolderPaths.Parent(folder.FolderPath), name);
        ViewModel.RenameFolder(folder.FolderPath, newPath);
    }

    private async Task DeleteFolderAsync(TreeNodeViewModel folder)
    {
        var count = ViewModel.CountSessionsUnder(folder.FolderPath);
        var confirmed = await ConfirmAsync(
            "Delete Folder",
            count == 0
                ? $"Delete the folder \"{folder.Name}\"?"
                : $"Delete the folder \"{folder.Name}\" and the {count} session(s) inside it? Their saved credentials are removed too.");
        if (confirmed)
            ViewModel.DeleteFolder(folder.FolderPath);
    }

    private async Task DeleteSessionAsync(Session session)
    {
        var confirmed = await ConfirmAsync(
            "Delete Session",
            $"Delete \"{session.Name}\" ({session.Host})? Its saved credential is removed too.");
        if (confirmed)
            ViewModel.DeleteSession(session);
    }

    private async Task DeleteSelectionAsync(IReadOnlyList<TreeNodeViewModel> items)
    {
        var folders = items.Where(n => n.IsFolder).ToList();
        var sessions = items.Where(n => !n.IsFolder).ToList();
        var affected = SessionsOf(items).Count();

        var parts = new List<string>();
        if (folders.Count > 0)
            parts.Add($"{folders.Count} folder(s)");
        if (sessions.Count > 0)
            parts.Add($"{sessions.Count} session(s)");
        var message = $"Delete {string.Join(" and ", parts)}?"
            + (affected > 0 ? $" {affected} session(s) will be removed; their saved credentials are removed too." : "");
        if (!await ConfirmAsync("Delete Selection", message))
            return;

        // Folders first; sessions already removed with a folder become harmless no-ops.
        foreach (var folder in folders)
            ViewModel.DeleteFolder(folder.FolderPath);
        foreach (var node in sessions)
            ViewModel.DeleteSession(node.Session!);
    }

    // ---- Tree expansion bookkeeping ----

    private void SetFolderExpansion(TreeNodeViewModel folder, bool expanded)
    {
        ViewModel.SetExpansionUnder(folder, expanded);
        ScheduleExpansionSync(); // nested containers realize lazily; push state as they appear
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetExpansionAll(true);
        ScheduleExpansionSync();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetExpansionAll(false);
        ScheduleExpansionSync();
    }

    private void SessionTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is TreeNodeViewModel node)
        {
            Trace($"Expanding '{node.FolderPath}'");
            ViewModel.NoteExpansion(node, expanded: true);
        }
    }

    private void SessionTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is TreeNodeViewModel node)
        {
            Trace($"Collapsed '{node.FolderPath}'");
            ViewModel.NoteExpansion(node, expanded: false);
        }
    }

    // ---- Drag and drop ----

    private void SessionTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        // v1: sessions move between folders; folders themselves are not draggable.
        if (args.Items.Any(i => i is TreeNodeViewModel { IsFolder: true }))
            args.Cancel = true;
    }

    private void SessionTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        // Dropping onto a session targets that session's folder; onto nothing targets the root.
        var targetFolder = args.NewParentItem switch
        {
            TreeNodeViewModel { IsFolder: true } folder => folder.FolderPath,
            TreeNodeViewModel sessionNode => sessionNode.FolderPath,
            _ => "",
        };

        var moved = false;
        foreach (var node in args.Items.OfType<TreeNodeViewModel>())
        {
            if (node.Session is { } session
                && !FolderPaths.Normalize(session.FolderPath).Equals(FolderPaths.Normalize(targetFolder), StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.MoveSessionToFolder(session.Id, targetFolder);
                moved = true;
            }
        }

        // Rebuild even on a no-op drop: the TreeView may have reordered the bound
        // collections in ways the model doesn't track (order is always alphabetical).
        if (!moved)
            ViewModel.RebuildTree();
    }

    // ---- Dialog helpers ----

    private async Task OpenSessionEditorAsync(Session? existing, string defaultFolder)
    {
        var dialog = new SessionEditDialog(ViewModel.FolderPathsForPicker, existing, defaultFolder)
        {
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
        if (dialog.Result is not { } result)
            return;

        if (existing is null)
            ViewModel.AddSession(result, dialog.Password);
        else
            ViewModel.UpdateSession(result, dialog.Password);
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string initial)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText = "Delete")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>One row in the quick connect dropdown: a saved session match or an ad-hoc target.</summary>
public sealed class QuickConnectSuggestion
{
    public string Display { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "\uE756";
    public Session Session { get; init; } = null!;

    /// <summary>AutoSuggestBox writes this into the text box when a suggestion is chosen.</summary>
    public override string ToString() => Display;
}
