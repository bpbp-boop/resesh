using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sessions.App.Controls;
using Sessions.App.Dialogs;
using Sessions.App.Terminal;
using Sessions.App.ViewModels;
using Sessions.Core.Models;
using Sessions.Core.Storage;
using Windows.System;

namespace Sessions.App;

public sealed partial class MainWindow : Window, ITabGroupHost
{
    public MainViewModel ViewModel { get; }

    private readonly Dictionary<TabGroupViewModel, TabGroupView> _groupViews = [];

    public MainWindow()
    {
        ViewModel = new MainViewModel(App.Store, App.Credentials);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AttachGroupView(ViewModel.Groups[0], column: 0);
        ViewModel.TreeRebuilt += () =>
        {
            ScheduleExpansionSync();
            SyncEmptyState();
        };
        ScheduleExpansionSync();
        SyncEmptyState();
        ApplySettingsToApp();
        RegisterAccelerators();
        Closed += (_, _) =>
        {
            SaveSplitterFraction();
            ViewModel.CloseAllTabs(); // tear down live SSH sessions without hanging
        };
    }

    private TabGroupView AttachGroupView(TabGroupViewModel group, int column)
    {
        var view = new TabGroupView(group, this);
        Grid.SetColumn(view, column);
        GroupArea.Children.Add(view);
        _groupViews[group] = view;
        return view;
    }

    private void RegisterAccelerators()
    {
        // Ctrl+F4: close active tab. Ctrl+Shift+\: split right / move to other group.
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
            if (ViewModel.ActiveTab is { } tab)
            {
                if (ViewModel.IsSplit)
                    MoveToOtherGroup(tab);
                else
                    SplitRight(tab);
            }
        };
        Root.KeyboardAccelerators.Add(closeTab);
        Root.KeyboardAccelerators.Add(split);
    }

    /// <summary>Opens a tab for the session and starts its terminal + SSH lifecycle.</summary>
    private void ConnectSession(Session session, TabGroupViewModel? group = null)
    {
        var tab = ViewModel.Connect(session, group);
        var view = new TerminalTabView(tab, App.Credentials, App.KnownHosts);
        view.CloseRequested += () => _ = RequestCloseTabAsync(tab);
        view.UnlockRequested += () => _ = HandleUnlockAsync(tab, view);
        view.SplitRequested += () =>
        {
            if (ViewModel.IsSplit)
                MoveToOtherGroup(tab);
            else if (ViewModel.GroupOf(tab).Tabs.Count > 1)
                SplitRight(tab);
        };
        tab.View = view;
        _groupViews[ViewModel.GroupOf(tab)].AddTerminal(view);
    }

    // ---- ITabGroupHost ----

    public void FocusGroup(TabGroupViewModel group)
    {
        ViewModel.FocusedGroup = group;
        ViewModel.NotifyActiveTabChanged();
    }

    /// <summary>THE close pathway: X button, Ctrl+F4, context menu, and middle-click all land here.</summary>
    public async Task RequestCloseTabAsync(TabViewModel tab)
    {
        var detail = tab.State == TabConnectionState.Connected ? " The session is still connected." : "";
        if (await ConfirmAsync("Close Tab", $"Close \"{tab.Header}\"?{detail}", "Close"))
            CloseTabCore(tab);
    }

    public async Task RequestCloseManyAsync(IReadOnlyList<TabViewModel> tabs, string description)
    {
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
        if (ViewModel.IsSplit)
        {
            MoveToOtherGroup(tab);
            return;
        }
        var newGroup = new TabGroupViewModel();
        ViewModel.Groups.Add(newGroup);
        AttachGroupView(newGroup, column: 2);

        var fraction = App.Settings.Current.SplitterFraction is > 0.1 and < 0.9 ? App.Settings.Current.SplitterFraction!.Value : 0.5;
        LeftGroupColumn.Width = new GridLength(fraction, GridUnitType.Star);
        RightGroupColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
        Splitter.Visibility = Visibility.Visible;

        MoveTabBetweenGroups(tab, newGroup, 0);
        ViewModel.OnGroupsChanged();
    }

    public void MoveToOtherGroup(TabViewModel tab)
    {
        if (!ViewModel.IsSplit)
            return;
        var other = ViewModel.Groups.First(g => g != ViewModel.GroupOf(tab));
        MoveTabBetweenGroups(tab, other, other.Tabs.Count);
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
        CollapseGroupIfEmpty(source);
    }

    private void CollapseGroupIfEmpty(TabGroupViewModel group)
    {
        if (!ViewModel.IsSplit || group.Tabs.Count > 0)
            return;

        SaveSplitterFraction();
        GroupArea.Children.Remove(_groupViews[group]);
        _groupViews.Remove(group);
        ViewModel.Groups.Remove(group);

        var remaining = ViewModel.Groups[0];
        Grid.SetColumn(_groupViews[remaining], 0);
        LeftGroupColumn.Width = new GridLength(1, GridUnitType.Star);
        RightGroupColumn.Width = new GridLength(0);
        Splitter.Visibility = Visibility.Collapsed;
        FocusGroup(remaining);
        ViewModel.OnGroupsChanged();
    }

    public void CloneSession(TabViewModel tab) =>
        ConnectSession(tab.Session, ViewModel.GroupOf(tab));

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
                    Children = { theme, fontFamily, fontSize, scrollback, copyOnSelect, rightClickPaste },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
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
        foreach (var tab in ViewModel.AllTabs)
        {
            if (tab.View is TerminalTabView view)
                view.ApplySettings(settings);
        }
    }

    private void SyncEmptyState() =>
        EmptyState.Visibility = App.Store.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- splitter persistence ----

    private void Splitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        SaveSplitterFraction();

    private void SaveSplitterFraction()
    {
        if (!ViewModel.IsSplit)
            return;
        var total = LeftGroupColumn.ActualWidth + RightGroupColumn.ActualWidth;
        if (total > 0)
            App.Settings.Save(App.Settings.Current with { SplitterFraction = LeftGroupColumn.ActualWidth / total });
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

    // ---- Search ----

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = ViewModel.RankedMatches(sender.Text).Take(8).ToList();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var target = args.ChosenSuggestion as Session
            ?? ViewModel.RankedMatches(args.QueryText).FirstOrDefault();
        if (target is not null)
        {
            ConnectSession(target);
            sender.Text = "";
            ViewModel.SearchText = "";
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

    // ---- Folder context menu ----

    private async void FolderNewSession_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            await OpenSessionEditorAsync(existing: null, defaultFolder: node.FolderPath);
    }

    private async void FolderNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node)
            return;
        var name = await PromptAsync("New Folder", $"Folder name (inside {node.FolderPath})", "");
        if (!string.IsNullOrWhiteSpace(name))
            ViewModel.CreateFolder(FolderPaths.Combine(node.FolderPath, name));
    }

    private async void FolderRename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node)
            return;
        var name = await PromptAsync("Rename Folder", "New name", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name)
            return;
        var newPath = FolderPaths.Combine(FolderPaths.Parent(node.FolderPath), name);
        ViewModel.RenameFolder(node.FolderPath, newPath);
    }

    private async void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node)
            return;
        var count = ViewModel.CountSessionsUnder(node.FolderPath);
        var confirmed = await ConfirmAsync(
            "Delete Folder",
            count == 0
                ? $"Delete the folder \"{node.Name}\"?"
                : $"Delete the folder \"{node.Name}\" and the {count} session(s) inside it? Their saved credentials are removed too.");
        if (confirmed)
            ViewModel.DeleteFolder(node.FolderPath);
    }

    // ---- Session context menu / interactions ----

    private void SessionConnect_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender)?.Session is { } session)
            ConnectSession(session);
    }

    private void SessionNode_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (NodeOf(sender)?.Session is { } session)
        {
            ConnectSession(session);
            e.Handled = true;
        }
    }

    private async void SessionEdit_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender)?.Session is { } session)
            await OpenSessionEditorAsync(session, session.FolderPath);
    }

    private async void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender)?.Session is not { } session)
            return;
        var confirmed = await ConfirmAsync(
            "Delete Session",
            $"Delete \"{session.Name}\" ({session.Host})? Its saved credential is removed too.");
        if (confirmed)
            ViewModel.DeleteSession(session);
    }

    // ---- Tree expansion bookkeeping ----

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
