using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Input;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Recording;
using Microsoft.UI.Windowing;
using Windows.System;

namespace Resesh.App;

// Opening tabs (sessions, app pages, recordings), the window as host of its tab groups
// (ITabGroupHost), and browser-style pinned tabs.
public sealed partial class MainWindow
{
    /// <summary>Launch-time entry for App's --open argument (the automated test rig).</summary>
    public void OpenSessionFromLaunch(Session session) => ConnectSession(session);

    /// <summary>Shows this window in front, restoring it if minimized, for a launch redirected here.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        Interop.WindowAlerts.BringToForeground(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void OpenWelcomeIfNeeded()
    {
        if (App.Settings.Current.OnboardingCompleted == false)
            OpenWelcome();
    }

    /// <summary>Shows an app page in the tab strip: selects the tab already hosting it,
    /// or opens one in the focused group with the view <paramref name="createView"/> builds.</summary>
    private TabViewModel OpenAppPage(AppPage page, Func<TabViewModel, UIElement> createView)
    {
        if (ViewModel.FindAppPage(page) is { } existing)
        {
            var existingGroup = ViewModel.GroupOf(existing);
            existingGroup.SelectedTab = existing;
            FocusGroup(existingGroup);
            return existing;
        }

        var group = ViewModel.FocusedGroup;
        var tab = TabViewModel.CreateAppPage(page, _viewModelEnvironment);
        ViewModel.AttachTab(tab, group, group.Tabs.Count);
        var view = createView(tab);
        tab.View = view;
        _groupViews[group].AddTerminal(view);
        return tab;
    }

    // Welcome can be dragged to another window, so its actions go to the window hosting it now.
    private void OpenWelcome() => OpenAppPage(AppPage.Welcome, tab =>
    {
        MainWindow Host() => App.WindowFor(tab) ?? this;
        var view = new OnboardingView(
            App.Settings.Current,
            App.PreviewThemeInAllWindows,
            () => Host().ViewModel.RebuildTree(),
            Core.Local.LocalShellDiscovery.DefaultProfile(
                App.Store, App.Settings.Current.DefaultLocalProfileId, App.AvailableLocalShells)?.Name);
        view.FinishRequested += () => Host().FinishOnboarding(tab, view);
        view.NewSessionRequested += () => _ = Host().OpenSessionEditorAsync(existing: null, defaultFolder: "");
        view.LocalShellRequested += () => Host().OpenDefaultLocalProfile();
        return view;
    });

    private void FinishOnboarding(TabViewModel tab, OnboardingView view)
    {
        if (!ViewModel.AllTabs.Contains(tab))
            return;

        App.SaveSettings(view.Complete());
        App.ApplySettingsToAllWindows();
        CloseTabCore(tab);
    }

    /// <summary>Opens a tab for the session and starts its terminal + shell lifecycle
    /// (SSH connect or local ConPTY launch, per the session's kind).</summary>
    private TabViewModel ConnectSession(
        Session session,
        TabGroupViewModel? group = null,
        bool trackRecent = true,
        int? resumeTmuxSlot = null,
        TabViewModel? insertAfter = null)
    {
        var tab = ViewModel.Connect(session, group, insertAfter);
        if (resumeTmuxSlot is { } slot)
            tab.TmuxSlot = slot;
        var tmuxSlotsAlreadyOpen = ViewModel.AllTabs
            .Where(other => other != tab && other.Session.Id == session.Id)
            .Select(other => other.TmuxSlot)
            .ToHashSet();
        var view = new TerminalTabView(
            tab, App.Credentials, App.KnownHosts, App.SshKeys, tmuxSlotsAlreadyOpen, resumeTmuxSlot);
        WireTerminalWindowEvents(tab, view);
        tab.View = view;
        _groupViews[ViewModel.GroupOf(tab)].AddTerminal(view);
        view.SetRulerPresentation(ViewModel.IsSplit, tab.IsGroupFocused);
        if (trackRecent && App.Store.Find(session.Id) is not null)
        {
            try { App.Settings.RecordRecentSession(session.Id); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                App.ReportRecoverableError(exception);
            }
            ViewModel.RefreshRecentSessions();
            App.RefreshJumpList();
        }
        return tab;
    }

    private static void WireTerminalWindowEvents(TabViewModel tab, TerminalTabView view)
    {
        view.ShortcutRequested += (id, chord) => App.WindowFor(tab)?.ExecuteShortcut(id, chord, tab);
        view.FilePaneOpenChanged += () =>
        {
            if (view.IsFilePaneOpen && App.WindowFor(tab) is { } owner &&
                owner.Root.ActualWidth < 1200 && owner._sessionsPaneOpen && !owner._paneOverlay)
                owner.SetSessionsPaneOpen(false);
        };
        view.UnlockRequested += () => { if (App.WindowFor(tab) is { } owner) _ = owner.HandleUnlockAsync(tab, view); };
        view.IconSuggested += key =>
        {
            if (App.WindowFor(tab) is { } owner && App.Store.Find(tab.Session.Id) is { Icon: null } current)
            {
                try { owner.ViewModel.UpdateSession(current with { Icon = key }, null); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    App.ReportRecoverableError(exception);
                }
            }
        };
        view.AgentAlert += (sender, snapshot) => App.WindowFor(tab)?.OnAgentAlert(sender, snapshot);
        tab.CompletionRequested += completion => App.WindowFor(tab)?.OnCommandCompletion(tab, completion);
    }

    private async Task<string?> PickRecordingFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".cast");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Recording could not open", exception.Message);
            return null;
        }
    }

    private async Task OpenRecordingPathAsync(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var recording = await Task.Run(() => AsciicastReader.Read(fullPath));
            OpenRecording(recording, fullPath);
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Recording could not open", exception.Message);
        }
    }

    private TabViewModel OpenRecording(TerminalRecording recording, string path)
    {
        var name = string.IsNullOrWhiteSpace(recording.Title)
            ? Path.GetFileNameWithoutExtension(path)
            : recording.Title;
        var session = new Session
        {
            Name = name,
            Kind = SessionKind.Local,
            Local = new LocalTarget
            {
                StartingDirectory = Path.GetDirectoryName(path) ?? "",
            },
        };
        var tab = ViewModel.Connect(session);
        tab.PlaybackPath = path;
        tab.State = TabConnectionState.Playback;
        tab.ConnectionSummary = "read-only asciicast";

        var player = new TerminalPlayerView(recording);
        player.CloseRequested += () => { if (App.WindowFor(tab) is { } owner) _ = owner.RequestCloseTabAsync(tab); };
        player.ShortcutRequested += (id, chord) => App.WindowFor(tab)?.ExecuteShortcut(id, chord, tab);
        tab.View = player;
        _groupViews[ViewModel.GroupOf(tab)].AddTerminal(player);
        return tab;
    }

    /// <summary>Launch-time entry for recording playback and the automated smoke rig.</summary>
    public void OpenRecordingFromLaunch(string path) =>
        OpenRecording(AsciicastReader.Read(path), Path.GetFullPath(path));

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

        RepaintSplitterLines();
    }

    private void RepaintSplitterLines()
    {
        foreach (var (splitter, line) in _splitterLines)
            line.Background = SplitterBrush(line, _activeSplitters.Contains(splitter));
    }

    /// <summary>THE close pathway: X button, Ctrl+F4, context menu, and middle-click all land here.</summary>
    public async Task RequestCloseTabAsync(TabViewModel tab)
    {
        if (tab.IsAppPage
            || (!tab.IsPinned && !App.Settings.Current.ConfirmCloseActiveSessions))
        {
            CloseTabCore(tab);
            return;
        }

        if (tab.Capabilities.RemoteSession
            && tab.Session.Persistent
            && tab.State == TabConnectionState.Connected
            && tab.View is TerminalTabView tmuxView)
        {
            await RequestCloseTmuxTabAsync(tab, tmuxView);
            return;
        }

        var detail = tab.State != TabConnectionState.Connected
            ? ""
            : tab.IsLocal
                ? " The process is still running — closing stops it and everything it started."
                : tab.Session.Persistent
                    ? " The remote session keeps running (persistent) — connecting again resumes it."
                    : " The session is still connected.";
        if (tab.IsPinned)
        {
            // Pinned tabs need the extra deliberate step; one dialog covers both.
            if (!await ConfirmAsync("Tab Is Pinned", $"\"{tab.Header}\" is pinned.{detail}", "Unpin and Close", acceptY: true))
                return;
            TogglePin(tab);
            CloseTabCore(tab);
            return;
        }
        if (await ConfirmAsync("Close Tab", $"Close \"{tab.Header}\"?{detail}", "Close", acceptY: true))
            CloseTabCore(tab);
    }

    private async Task RequestCloseTmuxTabAsync(TabViewModel tab, TerminalTabView view)
    {
        var dialog = new ConfirmDialog(
            tab.IsPinned ? "Tab Is Pinned" : "Close Tab",
            tab.IsPinned
                ? $"\"{tab.Header}\" is pinned. Closing the tab also unpins it."
                : $"Close \"{tab.Header}\"?",
            tab.IsPinned ? "Unpin and Close" : "Close Tab")
        {
            XamlRoot = Root.XamlRoot,
            AcceptsY = true,
            OptionText = "End persistent session",
            Note = "This ends everything running in the persistent session. Leave the check box clear to keep it running.",
        };

        if (!await dialog.ConfirmAsync())
            return;
        if (dialog.IsOptionChecked && !await view.TryEndRemoteSessionAsync())
        {
            await ShowEndRemoteSessionFailureAsync(tab);
            return;
        }
        if (tab.IsPinned)
            TogglePin(tab);
        CloseTabCore(tab);
    }

    public async Task RequestCloseManyAsync(IReadOnlyList<TabViewModel> tabs, string description)
    {
        tabs = tabs.Where(t => !t.IsPinned).ToList(); // bulk closes never touch pinned tabs
        if (tabs.Count == 0)
            return;
        if (!App.Settings.Current.ConfirmCloseActiveSessions)
        {
            foreach (var tab in tabs)
                CloseTabCore(tab);
            return;
        }

        var connected = tabs.Count(t => t.State == TabConnectionState.Connected);
        var message = connected > 0
            ? $"Close {tabs.Count} {description}? {connected} of them {(connected == 1 ? "is" : "are")} still connected."
            : $"Close {tabs.Count} {description}?";
        var persistent = tabs.Where(t => t.Capabilities.RemoteSession && t.Session.Persistent
            && t.State == TabConnectionState.Connected && t.View is TerminalTabView).ToList();
        if (persistent.Count > 0)
        {
            await RequestCloseManyWithTmuxAsync(tabs, persistent, message);
            return;
        }
        if (!await ConfirmAsync("Close Tabs", message, "Close", acceptY: true))
            return;
        foreach (var tab in tabs)
            CloseTabCore(tab);
    }

    /// <summary>Bulk close that can also end the tabs' persistent shells — otherwise each
    /// one is left detached on its host and turns up again at the next connect.</summary>
    private async Task RequestCloseManyWithTmuxAsync(
        IReadOnlyList<TabViewModel> tabs, IReadOnlyList<TabViewModel> persistent, string message)
    {
        var dialog = new ConfirmDialog("Close Tabs", message, "Close")
        {
            XamlRoot = Root.XamlRoot,
            AcceptsY = true,
            OptionText = persistent.Count == 1
                ? "End the persistent session of the connected tab"
                : $"End the persistent sessions of {persistent.Count} connected tabs",
            Note = "This ends everything running in those sessions. Leave the check box clear to keep them running.",
        };
        if (!await dialog.ConfirmAsync())
            return;

        var failed = new HashSet<TabViewModel>();
        if (dialog.IsOptionChecked)
        {
            var results = await Task.WhenAll(persistent.Select(async tab =>
                (tab, ended: await ((TerminalTabView)tab.View!).TryEndRemoteSessionAsync())));
            failed.UnionWith(results.Where(result => !result.ended).Select(result => result.tab));
        }
        // A tab whose shell could not be ended stays open so the user can retry from it.
        foreach (var tab in tabs.Where(tab => !failed.Contains(tab)))
            CloseTabCore(tab);
        if (failed.Count > 0)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Could Not End Remote Sessions", failed.Count == 1
                ? $"The persistent session for \"{failed.First().Header}\" could not be ended, so its tab was left open. Check the connection and try again."
                : $"{failed.Count} persistent sessions could not be ended, so their tabs were left open. Check the connections and try again.");
        }
    }

    private void CloseTabCore(TabViewModel tab)
    {
        Trace($"CloseTabCore: closing '{tab.Header}' selected={ReferenceEquals(ViewModel.GroupOf(tab).SelectedTab, tab)}");
        var group = ViewModel.GroupOf(tab);
        if (tab.View is OnboardingView onboarding)
            onboarding.CancelPreview();
        _groupViews[group].CloseTerminal(
            tab.View as UIElement,
            () => ViewModel.DetachTab(tab),
            () => (tab.View as IDisposable)?.Dispose());
        Trace($"CloseTabCore: done; selected now '{group.SelectedTab?.Header ?? "(null)"}'");
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

        // The tab jumps to its new pane and the layout is rebuilt; nothing slides or fades.
        _groupViews[sourceGroup].SuppressTabMotion();
        MoveTabBetweenGroups(tab, newGroup, 0);
        RebuildGroupLayout();
    }

    public void SetTabContentDropTargetsVisible(bool visible) =>
        App.SetTabContentDropTargetsVisible(visible);

    internal void SetTabContentDropTargetsVisibleCore(bool visible)
    {

        foreach (var groupView in _groupViews.Values)
            groupView.SetContentDropTargetVisible(visible);

    }

    public void MoveTabBetweenGroups(TabViewModel tab, TabGroupViewModel targetGroup, int targetIndex)
    {
        var source = ViewModel.GroupOf(tab);
        if (source == targetGroup)
            return;

        source.RemoveTab(tab);

        targetGroup.Tabs.Insert(Math.Clamp(targetIndex, 0, targetGroup.Tabs.Count), tab);
        targetGroup.SelectedTab = tab;

        if (tab.View is UIElement view)
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

    public void TransferTabToGroup(
        TabViewModel tab,
        ITabGroupHost sourceHost,
        TabGroupViewModel targetGroup,
        int targetIndex)
    {
        if (ReferenceEquals(sourceHost, this))
        {
            MoveTabBetweenGroups(tab, targetGroup, targetIndex);
            return;
        }

        sourceHost.DetachTabForTransfer(tab);
        ViewModel.AttachTab(tab, targetGroup, targetIndex);
        if (tab.View is UIElement view)
            _groupViews[targetGroup].AddTerminal(view);

        FocusGroup(targetGroup);
        ViewModel.SyncGroupFocus();
    }

    public void DetachTabForTransfer(TabViewModel tab)
    {
        var sourceGroup = ViewModel.GroupOf(tab);
        if (tab.View is UIElement view)
            _groupViews[sourceGroup].RemoveTerminal(view);
        ViewModel.DetachTab(tab);
        CollapseGroupIfEmpty(sourceGroup);
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
            groupView.SuppressTabMotion();
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(groupView) is Panel parent)
                parent.Children.Remove(groupView);
        }

        GroupArea.Children.Clear();
        foreach (var splitter in _splitterLines.Keys.Where(splitter => splitter != TreeSplitter).ToList())
            _splitterLines.Remove(splitter);
        _paneBoundaries.Clear();
        GroupArea.Children.Add(BuildGroupLayoutElement(_groupLayout.Root));
        UpdateRulerPresentations();
        // Re-assert once the rebuilt groups settle into their new rows and columns.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarRegions);
    }

    private FrameworkElement BuildGroupLayoutElement(SplitLayoutNode<TabGroupViewModel> node)
    {
        if (node is SplitLayoutLeaf<TabGroupViewModel> leaf)
            return _groupViews[leaf.Value];

        var branch = (SplitLayoutBranch<TabGroupViewModel>)node;
        var grid = new Grid();
        var isColumns = branch.Orientation == SplitOrientation.Columns;
        var hasSavedSizes = _savedPaneSizes.TryGetValue(branch, out var savedSizes);

        for (var index = 0; index < branch.Children.Count; index++)
        {
            var gridIndex = index * 2;
            if (isColumns)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = hasSavedSizes
                        ? new GridLength(savedSizes![index])
                        : new GridLength(1, GridUnitType.Star),
                });
            else
                grid.RowDefinitions.Add(new RowDefinition
                {
                    Height = hasSavedSizes
                        ? new GridLength(savedSizes![index])
                        : new GridLength(1, GridUnitType.Star),
                });

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
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            else
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

            var splitterLine = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_themePalette.Divider),
                IsHitTestVisible = false,
                HorizontalAlignment = isColumns ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                VerticalAlignment = isColumns ? VerticalAlignment.Stretch : VerticalAlignment.Center,
                Width = isColumns ? 1 : double.NaN,
                Height = isColumns ? double.NaN : 1,
            };
            var splitter = new CommunityToolkit.WinUI.Controls.GridSplitter
            {
                ResizeBehavior = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeBehavior.PreviousAndNext,
                ResizeDirection = isColumns
                    ? CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns
                    : CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Rows,
                HorizontalAlignment = isColumns ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                VerticalAlignment = isColumns ? VerticalAlignment.Stretch : VerticalAlignment.Center,
                Width = isColumns ? 7 : double.NaN,
                Height = isColumns ? double.NaN : 7,
                // The separate Border below supplies the visible one-pixel divider.
                // Keep this wider resize target transparent so it cannot cover that line.
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            // Keep the seven-pixel hit target above the terminal content while its
            // one-pixel grid track lets both tab groups meet the visible divider.
            Canvas.SetZIndex(splitter, 1);
            if (isColumns)
            {
                Grid.SetColumn(splitterLine, gridIndex + 1);
                Grid.SetColumn(splitter, gridIndex + 1);
            }
            else
            {
                Grid.SetRow(splitterLine, gridIndex + 1);
                Grid.SetRow(splitter, gridIndex + 1);
            }
            _paneBoundaries[splitterLine] =
                [.. branch.Children[index].Values, .. branch.Children[index + 1].Values];
            grid.Children.Add(splitterLine);
            grid.Children.Add(splitter);
            ConfigureSplitter(splitter, splitterLine);
        }

        return grid;
    }

    private IReadOnlyList<CommandPaletteEntry> BuildOpenTabCommands()
    {
        var commands = new List<CommandPaletteEntry>();
        for (var pane = 0; pane < ViewModel.Groups.Count; pane++)
        {
            var group = ViewModel.Groups[pane];
            for (var index = 0; index < group.Tabs.Count; index++)
            {
                var tab = group.Tabs[index];
                commands.Add(new CommandPaletteEntry
                {
                    Title = tab.Header,
                    Category = $"Open Tab · Pane {pane + 1} · Tab {index + 1}"
                        + (string.IsNullOrWhiteSpace(tab.Endpoint) ? "" : $" · {tab.Endpoint}"),
                    Keywords = $"switch {tab.Session.Name} {tab.Session.Host} {tab.Subtitle}",
                    ExecuteAsync = () =>
                    {
                        // Tabs can close or move while the palette is open.
                        if (ViewModel.AllTabs.Contains(tab))
                        {
                            var currentGroup = ViewModel.GroupOf(tab);
                            currentGroup.SelectedTab = tab;
                            FocusGroup(currentGroup);
                        }
                        return Task.CompletedTask;
                    },
                });
            }
        }
        return commands;
    }

    public void CloneSession(TabViewModel tab) =>
        ConnectSession(tab.Session, ViewModel.GroupOf(tab), insertAfter: tab);

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

    private void SavePinnedSessions()
    {
        App.SaveSettings(App.Settings.Current with
        {
            PinnedSessionIds = ViewModel.AllTabs.Where(t => t.IsPinned).Select(t => t.Session.Id).Distinct().ToList(),
        });
        App.RefreshJumpList();
    }

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
                ConnectSession(session, trackRecent: false).IsPinned = true;
                restored.Add(id);
            }
        }
        // Sessions deleted since the last run drop out of the pinned list.
        if (restored.Count != ids.Count)
            App.SaveSettings(App.Settings.Current with { PinnedSessionIds = restored });
    }

    public Task OpenSessionOptionsAsync(TabViewModel tab) =>
        OpenSessionSettingsAsync(tab, SessionSettingsTarget.General);

    private async Task OpenSessionSettingsAsync(TabViewModel tab, SessionSettingsTarget initialTarget)
    {
        var current = App.Store.Find(tab.Session.Id);
        if (current is null)
            return;
        if (current.IsLocal)
        {
            await OpenLocalProfileEditorAsync(current, current.FolderPath, initialTarget);
            return;
        }
        var notice = tab.State == TabConnectionState.Connected
            ? "This tab is connected — changes to host, port, or authentication apply on the next connect."
            : null;
        var dialog = new SessionEditDialog(
            ViewModel.FolderPathsForPicker, current, current.FolderPath, App.SshKeys, notice, initialTarget)
        {
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowModalAsync();
        if (dialog.Result is { } result)
            ViewModel.UpdateSession(result, dialog.Password);
    }

    public async Task LockSessionAsync(TabViewModel tab)
    {
        if (await TextPromptDialog.PromptPasswordAsync(Root.XamlRoot, "Lock Session",
                "Lock password (kept in memory only — not stored anywhere)", "Lock") is not { } password)
            return;
        tab.Lock(password);
        (tab.View as TerminalTabView)?.ShowLockOverlay();
    }

    private async Task HandleUnlockAsync(TabViewModel tab, TerminalTabView view)
    {
        var wait = tab.LockoutUntil - DateTimeOffset.Now;
        if (wait > TimeSpan.Zero)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Session Locked",
                $"Too many failed attempts. Try again in {Math.Ceiling(wait.TotalSeconds)} seconds.");
            return;
        }

        // An empty entry still counts as an attempt, so it goes through TryUnlock.
        var password = await new TextPromptDialog("Unlock Session", "Unlock", isPassword: true)
        {
            XamlRoot = Root.XamlRoot,
            FieldHeader = "Unlock password",
        }.PromptAsync();
        if (password is null)
            return;

        if (tab.TryUnlock(password))
        {
            view.HideLockOverlay();
        }
        else
        {
            var lockedOut = tab.LockoutUntil > DateTimeOffset.Now;
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Wrong Password", lockedOut
                ? "Wrong password. Unlocking is now delayed for 30 seconds."
                : "Wrong password.");
        }
    }

    public void ReconnectTab(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view
            && tab.State is TabConnectionState.Disconnected or TabConnectionState.Exited)
            _ = view.ConnectAsync(isReconnect: true);
    }

    public void SendBreak(TabViewModel tab) => (tab.View as TerminalTabView)?.SendBreak();

    public void DisconnectTab(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view && tab.State == TabConnectionState.Connected)
            view.DisconnectLocal();
    }

    private static bool CanManageRemoteSessions(TabViewModel? tab) =>
        tab is { IsLocked: false, IsPlayback: false, IsAppPage: false, View: TerminalTabView }
        && tab.Capabilities.RemoteSession && tab.Session.Persistent
        && tab.State != TabConnectionState.Connecting;

    public async Task ManageRemoteSessionsAsync(TabViewModel tab)
    {
        if (!CanManageRemoteSessions(tab) || tab.View is not TerminalTabView view || _managingRemoteSessions)
            return;
        int? slot;
        _managingRemoteSessions = true;
        try
        {
            using var connection = await view.CreateRemoteManagementConnectionAsync();
            if (connection is null) return;
            var openSlots = ViewModel.AllTabs
                .Where(other => other.Session.Id == tab.Session.Id && !other.IsPlayback)
                .Select(other => other.TmuxSlot)
                .ToHashSet();
            slot = await RemoteSessionsDialog.ManageAsync(Root.XamlRoot, tab.Session.Name,
                tab.Session.Id, openSlots,
                () => Task.Run(() => connection.RunCommand(Resesh.Core.Ssh.TmuxPersistence.ManagementCommand())),
                selected => Task.Run(() => connection.TryRunCommand(Resesh.Core.Ssh.TmuxPersistence.KillCommand(tab.Session.Id, selected))));
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Could Not Manage Remote Sessions",
                exception.Message, closeText: "Close");
            return;
        }
        finally { _managingRemoteSessions = false; }
        if (slot is not { } selectedSlot)
            return;
        var existing = ViewModel.AllTabs.FirstOrDefault(other => other.Session.Id == tab.Session.Id
            && other.TmuxSlot == selectedSlot && !other.IsPlayback);
        if (existing is not null)
        {
            var group = ViewModel.GroupOf(existing);
            group.SelectedTab = existing;
            FocusGroup(group);
            if (!existing.IsLocked && existing.View is TerminalTabView existingView
                && existing.State is TabConnectionState.Disconnected or TabConnectionState.Exited)
                await existingView.ResumeRemoteSessionAsync(selectedSlot);
        }
        else
        {
            ConnectSession(tab.Session, resumeTmuxSlot: selectedSlot);
        }
    }

    private bool _managingRemoteSessions;

    public async Task EndRemoteSessionAsync(TabViewModel tab)
    {
        if (tab.View is not TerminalTabView view || tab.State != TabConnectionState.Connected)
            return;
        var confirmed = await ConfirmAsync(
            "End Remote Session",
            $"End the persistent session for \"{tab.Header}\" on the server? " +
            "Anything running inside it will be terminated. (Closing the tab merely detaches.)",
            "End Session");
        if (confirmed && !await view.TryEndRemoteSessionAsync())
            await ShowEndRemoteSessionFailureAsync(tab);
    }

    private Task ShowEndRemoteSessionFailureAsync(TabViewModel tab) =>
        MessageDialog.ShowMessageAsync(Root.XamlRoot, "Could Not End Remote Session",
            $"The persistent session for \"{tab.Header}\" could not be ended. Check the connection and try again.");

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

    public void OpenWorkingFolder(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view && !tab.IsLocked)
            view.OpenWorkingFolder();
    }

    public async Task OpenRecordingsLocationAsync()
    {
        try
        {
            var directory = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(App.Settings.Current.RecordingDirectory));
            Directory.CreateDirectory(directory);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(directory);
            _ = System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Recordings location could not open", exception.Message);
        }
    }
}
