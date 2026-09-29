using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Input;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Storage;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Resesh.App;

public sealed partial class MainWindow : Window, ITabGroupHost, IMainWindowServices
{
    public MainViewModel ViewModel { get; }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    private readonly Dictionary<TabGroupViewModel, TabGroupView> _groupViews = [];
    private SplitLayout<TabGroupViewModel> _groupLayout;
    private Guid? _workspaceId;
    private bool _closeConfirmed;
    private bool _closePromptOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterDebounce;
    private RectInt32? _normalWindowBounds;
    private ThemeVisualPalette _themePalette = ThemeVisualPalette.For(App.ResolveTheme(App.Settings.Current.Theme));
    private int _themeApplyVersion;
    private DependencyObject? _palettePreviousFocus;
    private bool _paletteOpenedFromTerminal;
    private const double TreeIndentationScale = 0.5;
    private string _selectedRailTab = "sessions";
    private bool _sessionsPaneOpen;
    private double _sessionsPaneWidth = 280;
    private Storyboard? _sessionsPaneStoryboard;
    private FrameworkElement? _animatedSessionsPane;
    private int _sessionsPaneAnimationVersion;
    private readonly UISettings _uiSettings = new();
    private bool _isHighContrast = App.IsHighContrast;

    private readonly ViewModelEnvironment _viewModelEnvironment = new()
    {
        CurrentTheme = () => App.Settings.Current.Theme,
        ResolveTheme = App.ResolveTheme,
        ShowAgentIcons = () => App.Settings.Current.ShowAgentIcons,
        IsSessionVisible = session => !session.IsLocal || !session.BuiltIn || App.AvailableLocalShells.Contains(session.Id),
        ApplySessionSettings = tab =>
        {
            if (tab.View is TerminalTabView view)
            {
                view.ApplyTheme(App.Settings.Current.Theme);
                view.ApplyNonThemeSettings(App.Settings.Current);
            }
        },
        ReportError = App.ReportRecoverableError,
        RecentSessionIds = () => App.Settings.Current.RecentSessionIds,
        SaveRecentSessionIds = ids => App.SaveSettings(App.Settings.Current with { RecentSessionIds = ids }),
        RecordingDirectory = () => App.Settings.Current.RecordingDirectory,
        DefaultLocalProfileId = () => App.Settings.Current.DefaultLocalProfileId,
        SetDefaultLocalProfile = id => App.SaveSettings(App.Settings.Current with { DefaultLocalProfileId = id }),
    };

    public MainWindow()
    {
        ViewModel = new MainViewModel(App.Store, App.Credentials, _viewModelEnvironment, this);
        _groupLayout = new SplitLayout<TabGroupViewModel>(ViewModel.Groups[0]);
        InitializeComponent();
        // TreeViewItem consumes Enter before the TreeView's normal KeyDown event. Listen to
        // handled events so the app's Explorer-style multi-selection can open every session.
        SessionTree.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(SessionTree_KeyDown),
            handledEventsToo: true);
        CommandPalette.CloseRequested += CloseCommandPalette;
        CommandPalette.CommandInvoked += command => _ = ExecuteCommandPaletteEntryAsync(command);
        InitializeHistoryOverlay();
        RestoreWindowPlacement();
        AppWindow.Changed += AppWindow_Changed;
        ConfigureSplitter(TreeSplitter, TreeSplitterLine);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        InitializeTitleBar();
        InitializeNarrowLayout();
        // Lets the icon catalog rasterize at true device pixels (XamlRoot is null until
        // the content loads; the catalog falls back to scale 1 and re-renders on demand).
        App.Icons.ScaleProvider = () => Root.XamlRoot?.RasterizationScale ?? 1.0;
        AttachGroupView(ViewModel.Groups[0]);
        RebuildGroupLayout();
        ViewModel.TreeRebuilt += () =>
        {
            // Rebuilds reuse surviving nodes; only drop selections that left the tree.
            ViewModel.PruneTreeSelection();
            ScheduleExpansionSync();
            SyncEmptyState();
            ViewModel.RefreshRecentSessions();
            App.RefreshJumpList(); // sessions were renamed, added or deleted
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText))
            {
                App.RefreshWindowTitles();
            }
            else if (e.PropertyName == nameof(MainViewModel.Progress))
            {
                ApplyTaskbarProgress();
            }
        };
        ScheduleExpansionSync();
        SyncEmptyState();
        ApplySettingsToApp();
        RegisterAccelerators();
        InitializeSessionsRail();
        RefreshWorkspaceMenu();
        _uiSettings.ColorValuesChanged += SystemColorsChanged;
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            _uiSettings.ColorValuesChanged -= SystemColorsChanged;
            Resesh.Core.Backend.CleanupActions.Run(App.ReportRecoverableError,
                SaveTreePaneWidth,
                () => WorkspaceOperations.SaveLastLayout(),
                ViewModel.CloseAllTabs);
        };
    }

    internal void RefreshWindowTitle(bool showContext)
    {
        if (!showContext)
        {
            Title = ViewModel.ActiveTab is { } tab ? $"{tab.Session.Name} - resesh" : "resesh";
            return;
        }

        var workspaceName = _workspaceId is { } workspaceId
            ? App.Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId)?.Name
            : null;
        Title = workspaceName ?? ViewModel.ActiveTab?.Session.Name ?? "resesh";
    }

    private void SetWorkspaceContext(Guid? workspaceId)
    {
        _workspaceId = workspaceId;
        App.RefreshWindowTitles();
    }

    private void SystemColorsChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var theme = App.Settings.Current.Theme;
            var isHighContrast = App.IsHighContrast;
            var highContrastChanged = isHighContrast != _isHighContrast;
            _isHighContrast = isHighContrast;
            if (highContrastChanged || string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase))
                ApplyThemeToApp(theme);
        });
    }

    private void RestoreWindowPlacement()
    {
        if (App.Settings.Current.WindowPlacement is not { Width: >= 320, Height: >= 240 } placement)
            return;

        var requested = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
        var workArea = DisplayArea.GetFromRect(requested, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(requested.Width, workArea.Width);
        var height = Math.Min(requested.Height, workArea.Height);
        var x = Math.Clamp(requested.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(requested.Y, workArea.Y, workArea.Y + workArea.Height - height);
        _normalWindowBounds = new RectInt32(x, y, width, height);
        AppWindow.MoveAndResize(_normalWindowBounds.Value);

        if (placement.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange)
            && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalWindowBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void SaveWindowPlacement()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var bounds = _normalWindowBounds
            ?? new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        if (bounds.Width < 320 || bounds.Height < 240)
            return;

        App.SaveSettings(App.Settings.Current with
        {
            WindowPlacement = new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized),
        });
    }

    private TabGroupView AttachGroupView(TabGroupViewModel group)
    {
        var view = new TabGroupView(group, this);
        // The initial app-wide theme pass has already run when a user creates a split.
        // Apply the live palette now so the new strip does not start with Fluent defaults.
        view.ApplyTheme(_themePalette);
        _groupViews[group] = view;
        return view;
    }

    /// <summary>Registers every window shortcut from the shared table. A focused terminal
    /// does not see these accelerators; it forwards the same chords to <see cref="ExecuteShortcut"/>.</summary>
    private void RegisterAccelerators()
    {
        foreach (var binding in KeyBindings.All)
        {
            if (binding.Scope is not (ShortcutScope.App or ShortcutScope.Window))
                continue;
            for (var i = 0; i < binding.Chords.Count; i++)
            {
                var accelerator = AppShortcuts.Accelerator(binding.Chords[i]);
                var id = binding.Id;
                var chord = i;
                accelerator.Invoked += (_, e) => e.Handled = ExecuteShortcut(id, chord);
                Root.KeyboardAccelerators.Add(accelerator);
            }
        }
        ApplyShortcutLabels();
    }

    private void ApplyShortcutLabels()
    {
        NewWindowMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.NewWindow);
        SettingsMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.Settings);
        SessionsPaneMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.ToggleSessionsPane);
        FullScreenMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.FullScreen);
        CommandPaletteMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.CommandPalette);
        CommandHistoryMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.CommandHistory);
        KeyboardShortcutsMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.KeyboardShortcuts);
        SplitRightMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.SplitRight);
        SplitDownMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.SplitDown);
        FilePaneMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.FilePane);
        ReconnectMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.ReconnectTab);
        SendBreakMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.SendBreak);
        CloneMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.CloneTab);
        CloseTabMenuItem.KeyboardAcceleratorTextOverride = AppShortcuts.Label(ShortcutIds.CloseTab);
        var quickConnect = AppShortcuts.Label(ShortcutIds.QuickConnect);
        QuickConnectHintText.Text = quickConnect;
        ToolTipService.SetToolTip(QuickConnectBox,
            $"Connect with ssh user@host or telnet host port, or search saved sessions ({quickConnect})");
        ToolTipService.SetToolTip(QuickConnectButton, $"Quick connect or search sessions ({quickConnect})");
    }

    /// <summary>Runs one shortcut from the shared table. <paramref name="source"/> is the tab
    /// whose terminal had focus when a terminal forwarded the key; tab actions apply to it.
    /// Returns false when the shortcut does not apply, so the key keeps its normal meaning.</summary>
    private bool ExecuteShortcut(string id, int chord, TabViewModel? source = null)
    {
        if (source is not null && !ViewModel.AllTabs.Contains(source))
            source = null;
        if (source is not null && !source.IsGroupFocused)
            FocusGroup(ViewModel.GroupOf(source));
        var fromTerminal = source is not null;
        var tab = source ?? ViewModel.ActiveTab;
        var group = tab is null ? ViewModel.FocusedGroup : ViewModel.GroupOf(tab);

        // Focus and navigation keys act on the window's layout directly.
        switch (id)
        {
            case ShortcutIds.CommandHistory when HistoryOverlay.IsOpen:
                CloseHistory();
                return true;
            case ShortcutIds.NextTab or ShortcutIds.PreviousTab:
                return SelectTab(group, TabNavigation.Cycle(
                    group.SelectedTab is { } selected ? group.Tabs.IndexOf(selected) : -1,
                    group.Tabs.Count,
                    id == ShortcutIds.NextTab ? 1 : -1));
            case ShortcutIds.GoToTab:
                return SelectTab(group, TabNavigation.GoTo(chord + 1, group.Tabs.Count));
            case ShortcutIds.LastTab:
                return SelectTab(group, TabNavigation.GoTo(9, group.Tabs.Count));
            case ShortcutIds.FocusGroupLeft:
                return tab is not null && FocusNeighborGroup(group, NavigationDirection.Left);
            case ShortcutIds.FocusGroupRight:
                return tab is not null && FocusNeighborGroup(group, NavigationDirection.Right);
            case ShortcutIds.FocusGroupUp:
                return tab is not null && FocusNeighborGroup(group, NavigationDirection.Up);
            case ShortcutIds.FocusGroupDown:
                return tab is not null && FocusNeighborGroup(group, NavigationDirection.Down);
        }

        // Everything else runs the command the menus and palette run. It runs only where
        // its CanExecute allows, so a key that does not apply keeps its normal meaning.
        if (ViewModel.Commands.ForShortcut(id) is not { } descriptor)
            return false;
        var parameter = descriptor.CurrentParameter ?? source;
        if (!descriptor.Command.CanExecute(parameter))
            return false;
        _invokedFromTerminal = fromTerminal;
        try
        {
            descriptor.Command.Execute(parameter);
        }
        finally
        {
            _invokedFromTerminal = false;
        }
        return true;
    }

    /// <summary>True while a terminal-forwarded shortcut runs its command, so overlays and
    /// dialogs it opens return focus to the terminal.</summary>
    private bool _invokedFromTerminal;

    /// <summary>Opens a dialog from a shortcut. WinUI allows one ContentDialog at a time, so
    /// the key does nothing while another dialog is showing.</summary>
    private async Task ShowThenRefocusAsync(Func<Task> show, bool refocusTerminal)
    {
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(popup => popup.Child is ContentDialog))
            return;
        try
        {
            await show();
        }
        catch (Exception exception)
        {
            App.ReportRecoverableError(exception);
        }
        if (refocusTerminal)
            FocusActiveTerminal();
    }

    private void FocusSessionFilter()
    {
        if (!SessionsPaneShown || _selectedRailTab != "sessions")
            SelectSessionsRailTab("sessions");
        FilterBox.Focus(FocusState.Programmatic);
        FilterBox.SelectAll();
    }

    private bool SelectTab(TabGroupViewModel group, int index)
    {
        if (index < 0 || index >= group.Tabs.Count)
            return false;
        var tab = group.Tabs[index];
        if (ReferenceEquals(group.SelectedTab, tab))
            FocusTabContent(tab);
        else
            group.SelectedTab = tab; // the selection change focuses the tab's terminal
        return true;
    }

    /// <summary>Moves a tab one place within its group. Pinned tabs stay in front.</summary>
    private void MoveTab(TabViewModel tab, int delta)
    {
        var group = ViewModel.GroupOf(tab);
        var index = group.Tabs.IndexOf(tab);
        var target = TabNavigation.MoveTarget(
            index, delta, group.Tabs.Count, group.Tabs.Count(t => t.IsPinned), tab.IsPinned);
        if (target < 0)
            return;
        group.Tabs.Move(index, target);
        group.SelectedTab = tab; // the move must not steal selection
        FocusTabContent(tab);
    }

    /// <summary>Focuses the tab group next to <paramref name="current"/> on screen.</summary>
    private bool FocusNeighborGroup(TabGroupViewModel current, NavigationDirection direction)
    {
        if (!ViewModel.IsSplit)
            return false;
        var groups = new List<(TabGroupViewModel Item, LayoutBounds Bounds)>();
        LayoutBounds? origin = null;
        foreach (var (group, view) in _groupViews)
        {
            if (view.ActualWidth <= 0 || view.ActualHeight <= 0 || !ViewModel.Groups.Contains(group))
                continue;
            var rect = view.TransformToVisual(Root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, view.ActualWidth, view.ActualHeight));
            var bounds = new LayoutBounds(rect.X, rect.Y, rect.Width, rect.Height);
            groups.Add((group, bounds));
            if (ReferenceEquals(group, current))
                origin = bounds;
        }
        if (origin is null)
            return false;
        if (GroupNavigation.FindNeighbor(groups, origin.Value, direction) is { } target)
        {
            FocusGroup(target);
            if (target.SelectedTab is { } selected)
                FocusTabContent(selected);
        }
        return true;
    }

    private void FocusTabContent(TabViewModel tab)
    {
        if (tab.View is TerminalTabView view)
            DispatcherQueue.TryEnqueue(view.FocusTerminal);
    }

    private OverlappedPresenter? _presenterBeforeFullScreen;

    private void ToggleFullScreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            if (_presenterBeforeFullScreen is { } previous)
                AppWindow.SetPresenter(previous);
            else
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _presenterBeforeFullScreen = null;
        }
        else
        {
            // Reuse the overlapped presenter: it carries always-on-top and maximized state.
            _presenterBeforeFullScreen = AppWindow.Presenter as OverlappedPresenter;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        FullScreenMenuItem.IsChecked = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    }

    private Task ShowKeyboardShortcutsAsync() => KeyboardShortcutsDialog.OpenAsync(Root.XamlRoot);

    private void ShowCommandPalette(bool openedFromTerminal = false)
    {
        if (CommandPalette.IsOpen)
            return;
        if (HistoryOverlay.IsOpen)
            HistoryOverlay.Close();

        _paletteOpenedFromTerminal = openedFromTerminal;
        _palettePreviousFocus = openedFromTerminal
            ? null
            : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        var commands = BuildCommandPalette();
        CommandPalette.Open(commands);
    }

    private void CloseCommandPalette()
    {
        if (!CommandPalette.IsOpen)
            return;

        CommandPalette.Close();
        RestorePaletteFocus();
    }

    private async Task ExecuteCommandPaletteEntryAsync(CommandPaletteEntry command)
    {
        CommandPalette.Close();
        try
        {
            await command.ExecuteAsync();
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Command could not run", exception.Message);
        }
        finally
        {
            if (!command.KeepActionFocus)
                FocusActiveTerminal();
            _palettePreviousFocus = null;
            _paletteOpenedFromTerminal = false;
        }
    }

    private void RestorePaletteFocus()
    {
        if (_paletteOpenedFromTerminal)
        {
            FocusActiveTerminal();
        }
        else if (_palettePreviousFocus is { } previous)
        {
            _ = FocusManager.TryFocusAsync(previous, FocusState.Programmatic);
        }

        _palettePreviousFocus = null;
        _paletteOpenedFromTerminal = false;
    }

    private void FocusActiveTerminal()
    {
        if (ViewModel.ActiveTab?.View is TerminalTabView view)
            DispatcherQueue.TryEnqueue(view.FocusTerminal);
    }

    /// <summary>The palette lists the view model's available commands, plus two things only
    /// the window knows: its open tabs (after the application commands) and the active
    /// terminal page's own actions (before Close Tab).</summary>
    private IReadOnlyList<CommandPaletteEntry> BuildCommandPalette()
    {
        var commands = new List<CommandPaletteEntry>();
        var openTabsAdded = false;
        foreach (var descriptor in ViewModel.Commands.PaletteCommands())
        {
            if (!openTabsAdded && descriptor.Category != "Application")
            {
                commands.AddRange(BuildOpenTabCommands());
                openTabsAdded = true;
            }
            if (ReferenceEquals(descriptor.Command, ViewModel.CloseTabCommand))
                commands.AddRange(BuildTerminalPageCommands());
            commands.Add(ToPaletteEntry(descriptor));
        }
        if (!openTabsAdded)
            commands.AddRange(BuildOpenTabCommands());
        return commands;
    }

    private static CommandPaletteEntry ToPaletteEntry(CommandDescriptor descriptor)
    {
        var parameter = descriptor.CurrentParameter;
        return new CommandPaletteEntry
        {
            Category = descriptor.Category,
            Title = descriptor.Title(),
            Keywords = descriptor.Keywords,
            Shortcut = descriptor.ShortcutId is { } id ? AppShortcuts.Label(id) : "",
            KeepActionFocus = descriptor.KeepActionFocus,
            ExecuteAsync = () =>
            {
                if (descriptor.Command is IAsyncRelayCommand asyncCommand)
                    return asyncCommand.ExecuteAsync(parameter);
                descriptor.Command.Execute(parameter);
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>Terminal actions run in the page, as their keys do; Find keeps focus in its field.</summary>
    private IReadOnlyList<CommandPaletteEntry> BuildTerminalPageCommands()
    {
        if (ViewModel.ActiveTab is not { View: TerminalTabView terminal, IsLocked: false })
            return [];

        var commands = new List<CommandPaletteEntry>();
        void AddTerminal(string title, string keywords, string id, bool keepFocus = false) =>
            commands.Add(new CommandPaletteEntry
            {
                Category = "Terminal",
                Title = title,
                Keywords = keywords,
                Shortcut = AppShortcuts.Label(id),
                KeepActionFocus = keepFocus,
                ExecuteAsync = () =>
                {
                    if (keepFocus)
                        terminal.FocusTerminal();
                    terminal.InvokeTerminalShortcut(id);
                    return Task.CompletedTask;
                },
            });
        AddTerminal("Find", "search text scrollback", ShortcutIds.Find, keepFocus: true);
        AddTerminal("Select All", "selection copy", ShortcutIds.SelectAll);
        AddTerminal("Clear Scrollback", "history buffer reset clean", ShortcutIds.ClearScrollback);
        AddTerminal("Zoom In", "font size bigger larger", ShortcutIds.ZoomIn);
        AddTerminal("Zoom Out", "font size smaller", ShortcutIds.ZoomOut);
        AddTerminal("Reset Zoom", "font size default", ShortcutIds.ZoomReset);
        return commands;
    }

    // ---- Local profiles: default launch + split-button menu ----

    /// <summary>Opens the default local profile; falls back to the SSH editor when no
    /// local shell was discovered at all (unlikely — cmd.exe always exists).</summary>
    private void OpenDefaultLocalProfile()
    {
        var profile = Core.Local.LocalShellDiscovery.DefaultProfile(
            App.Store, App.Settings.Current.DefaultLocalProfileId, App.AvailableLocalShells);
        if (profile is not null)
            ConnectSession(profile);
        else
            _ = OpenSessionEditorAsync(existing: null, defaultFolder: "");
    }

    /// <summary>Rebuilds the + Session menu: visible local profiles, then the creators.</summary>
    private void NewSessionFlyout_Opening(object sender, object e)
    {
        NewSessionFlyout.Items.Clear();
        foreach (var profile in ViewModel.VisibleSessions.Where(s => s.IsLocal)
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var captured = profile;
            var item = new MenuFlyoutItem
            {
                Text = profile.Name,
                Icon = App.Icons.GetImage(profile.Icon, Icons.SessionIconCatalog.ListIconSize) is { } image
                    ? new ImageIcon { Source = image }
                    : new FontIcon { Glyph = "" },
            };
            item.Click += (_, _) => ConnectSession(captured);
            NewSessionFlyout.Items.Add(item);
        }
        if (NewSessionFlyout.Items.Count > 0)
            NewSessionFlyout.Items.Add(new MenuFlyoutSeparator());
        NewSessionFlyout.Items.Add(new MenuFlyoutItem { Text = "New SSH Session…", Command = ViewModel.NewSshSessionCommand });
        NewSessionFlyout.Items.Add(new MenuFlyoutItem { Text = "New Telnet Session…", Command = ViewModel.NewTelnetSessionCommand });
        NewSessionFlyout.Items.Add(new MenuFlyoutItem { Text = "New Local Profile…", Command = ViewModel.NewLocalProfileCommand });
    }

    private async Task OpenLocalProfileEditorAsync(
        Session? existing,
        string defaultFolder,
        SessionSettingsTarget initialTarget = SessionSettingsTarget.General)
    {
        var isCurrentDefault = existing is not null && App.Settings.Current.DefaultLocalProfileId == existing.Id;
        var dialog = new LocalProfileEditDialog(
            ViewModel.LocalFolderPathsForPicker, existing, defaultFolder, isCurrentDefault, initialTarget)
        {
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowModalAsync();
        if (dialog.Result is not { } result)
            return;

        if (existing is null)
            ViewModel.AddSession(result, null);
        else
            ViewModel.UpdateSession(result, null);
        if (dialog.MakeDefault && App.Settings.Current.DefaultLocalProfileId != result.Id)
            App.SaveSettings(App.Settings.Current with { DefaultLocalProfileId = result.Id });
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
        ApplyTitleBarButtonColors(App.Settings.Current.Theme);
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
        QuickConnectHost.SizeChanged += (_, _) =>
        {
            UpdateQuickConnectHint();
            UpdateTitleBarRegions();
        };
        TitleBarButtons.SizeChanged += (_, _) => UpdateTitleBarRegions();
    }

    // ---- narrow windows ----

    // Title bar widths, in DIPs, below which parts of it compact. Measured at 100%:
    // menus 314, caption buttons 144, New SSH session 203 (about 80 as an icon).
    private const double TitleBarFullWidth = 1000;
    private const double TitleBarSearchWidth = 860;
    // Keeps the compact title bar and a usable tab area (rail + minimum tree + tabs).
    private const double MinimumWindowWidth = 740;
    private const double MinimumWindowHeight = 480;
    private const double MinimumTabAreaWidth = 320;

    // With the Automatic layout, the sessions pane floats over the tabs below this window width.
    // It docks again only above the wider bound, so resizing near the edge doesn't flicker.
    private const double PaneOverlayBelowWidth = 1000;
    private const double PaneDockAboveWidth = 1060;

    private bool _quickConnectExpanded;
    private bool _paneOverlay;
    private bool _overlayPaneShown;
    private TabViewModel? _lastActiveTab;

    /// <summary>Whether a sessions pane is on screen: the floating one on a narrow window,
    /// otherwise the saved docked state.</summary>
    private bool SessionsPaneShown => _paneOverlay ? _overlayPaneShown : _sessionsPaneOpen;

    private void InitializeNarrowLayout()
    {
        AppTitleBar.SizeChanged += (_, _) => ApplyTitleBarWidth();
        Root.Loaded += (_, _) =>
        {
            ApplyMinimumWindowSize();
            ApplyWindowWidth();
            Root.XamlRoot.Changed += (_, _) =>
            {
                ApplyMinimumWindowSize();
                ApplyWindowWidth();
            };
        };
        foreach (var pane in SessionsPanes())
            pane.KeyDown += SessionsPane_KeyDown;
        // Opening or switching to a tab from the floating pane is done with it.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.StatusText) || ReferenceEquals(ViewModel.ActiveTab, _lastActiveTab))
                return;
            _lastActiveTab = ViewModel.ActiveTab;
            if (_paneOverlay && _overlayPaneShown)
                SetSessionsPaneOpen(false);
        };
    }

    private Grid[] SessionsPanes() => [SessionsPane, WorkspacesPane, RecentPane, RecordingsPane];

    private void ApplyWindowWidth()
    {
        UpdatePaneMode();
        LimitTreeWidth();
    }

    private void UpdatePaneMode()
    {
        if (Root.XamlRoot is not { } root)
            return;
        var width = root.Size.Width;
        var overlay = SessionsPaneLayouts.Normalize(App.Settings.Current.SessionsPaneLayout) switch
        {
            SessionsPaneLayouts.Float => true,
            SessionsPaneLayouts.Dock => false,
            _ => _paneOverlay ? width < PaneDockAboveWidth : width < PaneOverlayBelowWidth,
        };
        if (overlay == _paneOverlay)
        {
            if (_paneOverlay && _overlayPaneShown)
                ApplySessionsRailLayout(); // keep the floating width within the window
            return;
        }
        if (overlay && _sessionsPaneOpen && TreeWidthIsUserChosen)
            _sessionsPaneWidth = TreeColumn.ActualWidth;
        // The floating pane starts closed; docking restores the saved state.
        _paneOverlay = overlay;
        _overlayPaneShown = false;
        _sessionsPaneStoryboard?.Stop();
        _sessionsPaneStoryboard = null;
        if (_animatedSessionsPane is { } pane)
        {
            pane.Opacity = 1;
            if (pane.RenderTransform is TranslateTransform transform)
                transform.X = 0;
            _animatedSessionsPane = null;
        }
        ApplySessionsRailLayout();
    }

    /// <summary>The saved width, capped so the floating pane leaves the tabs in view.</summary>
    private double OverlayPaneWidth()
    {
        var available = (Root.XamlRoot?.Size.Width ?? 1000) - MainArea.ColumnDefinitions[0].ActualWidth - 96;
        return Math.Max(200, Math.Min(Math.Min(_sessionsPaneWidth, 360), available));
    }

    private void PaneDismissLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        SetSessionsPaneOpen(false);
    }

    private void SessionsPane_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || !_paneOverlay || !_overlayPaneShown)
            return;
        e.Handled = true;
        SetSessionsPaneOpen(false);
        FocusActiveTerminal();
    }

    /// <summary>AppWindow sizes are physical pixels, so the minimum follows the display scale.</summary>
    private void ApplyMinimumWindowSize()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0 || AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinimumWindowWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumWindowHeight * scale);
    }

    /// <summary>Drops the New SSH session label first, then folds quick connect into a
    /// search button that expands over the menus while it is in use.</summary>
    private void ApplyTitleBarWidth()
    {
        var width = AppTitleBar.ActualWidth;
        NewSessionLabel.Visibility = width >= TitleBarFullWidth ? Visibility.Visible : Visibility.Collapsed;
        var compact = width < TitleBarSearchWidth;
        if (!compact)
            _quickConnectExpanded = false;
        var showBox = !compact || _quickConnectExpanded;
        QuickConnectHost.Visibility = showBox ? Visibility.Visible : Visibility.Collapsed;
        QuickConnectButton.Visibility = showBox ? Visibility.Collapsed : Visibility.Visible;
        TitleBarMenus.Visibility = compact && _quickConnectExpanded ? Visibility.Collapsed : Visibility.Visible;
        UpdateTitleBarRegions();
    }

    private void QuickConnectButton_Click(object sender, RoutedEventArgs e) => FocusQuickConnectBox();

    /// <summary>Focuses quick connect, expanding it first when the title bar shows only its button.</summary>
    private void FocusQuickConnectBox()
    {
        if (QuickConnectHost.Visibility == Visibility.Visible)
        {
            QuickConnectBox.Focus(FocusState.Programmatic);
            return;
        }
        // Focus the box before the search button hides. Hiding a focused button moves
        // focus on to the next control, which would collapse the box again at once.
        _quickConnectExpanded = true;
        TitleBarMenus.Visibility = Visibility.Collapsed;
        QuickConnectHost.Visibility = Visibility.Visible;
        AppTitleBar.UpdateLayout();
        QuickConnectBox.Focus(FocusState.Programmatic);
        ApplyTitleBarWidth();
    }

    private void CollapseQuickConnectIfIdle()
    {
        if (!_quickConnectExpanded)
            return;
        // Focus can pass through the suggestion list; decide once it has settled.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_quickConnectExpanded || QuickConnectHasFocus() || QuickConnectBox.IsSuggestionListOpen)
                return;
            _quickConnectExpanded = false;
            ApplyTitleBarWidth();
        });
    }

    /// <summary>Focus sits on the box's inner TextBox; the AutoSuggestBox's own FocusState stays Unfocused.</summary>
    private bool QuickConnectHasFocus()
    {
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, QuickConnectBox))
                return true;
        }
        return false;
    }

    /// <summary>A wide saved tree pane gives way to the tabs on a narrow window. The saved
    /// width is kept and returns when the window widens.</summary>
    private void MainArea_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyWindowWidth();

    private void LimitTreeWidth()
    {
        // The window's width, not MainArea's: a grid whose columns overflow is arranged at
        // its overflowing width and clipped, so MainArea never reports less.
        if (Root.XamlRoot is not { } root)
            return;
        var fixedColumns = MainArea.ColumnDefinitions[0].ActualWidth + TreeSplitterColumn.ActualWidth;
        TreeColumn.MaxWidth = Math.Max(180, root.Size.Width - fixedColumns - MinimumTabAreaWidth);
    }

    /// <summary>The tree's current width, unless the window is narrowing it below the saved width.</summary>
    private bool TreeWidthIsUserChosen =>
        TreeColumn.ActualWidth >= 180 && TreeColumn.ActualWidth < TreeColumn.MaxWidth - 0.5;

    /// <summary>Caption buttons live outside XAML theming; keep them in sync with the app theme.</summary>
    private void ApplyTitleBarButtonColors(string theme)
    {
        theme = App.ResolveTheme(theme);
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            return;
        var tb = AppWindow.TitleBar;
        var palette = ThemeVisualPalette.For(theme);
        if (palette.IsHighContrast)
        {
            tb.ButtonBackgroundColor = palette.Shell;
            tb.ButtonInactiveBackgroundColor = palette.Shell;
            tb.ButtonForegroundColor = palette.TreeForeground;
            tb.ButtonInactiveForegroundColor = palette.TreeForeground;
            tb.ButtonHoverBackgroundColor = palette.TreeSelection;
            tb.ButtonHoverForegroundColor = palette.TreeSelectionForeground;
            tb.ButtonPressedBackgroundColor = palette.TreeSelection;
            tb.ButtonPressedForegroundColor = palette.TreeSelectionForeground;
            return;
        }
        var dark = !ThemeCatalog.IsLight(theme);
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
        if (!double.IsFinite(scale) || scale <= 0)
            return;
        TitleBarLeftPadding.Width = new GridLength(TitleBarInset(AppWindow.TitleBar.LeftInset, scale));
        TitleBarRightPadding.Width = new GridLength(TitleBarInset(AppWindow.TitleBar.RightInset, scale));

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

    /// <summary>Caption insets can be transiently negative mid-DPI change (moving between
    /// monitors, docking/undocking); GridLength throws on those, so clamp to zero.</summary>
    internal static double TitleBarInset(int physicalInset, double scale)
    {
        var inset = physicalInset / scale;
        return double.IsFinite(inset) && inset > 0 ? inset : 0;
    }

    private TerminalProgress _taskbarProgress;

    /// <summary>Mirrors this window's combined tab progress on its taskbar button.</summary>
    private void ApplyTaskbarProgress()
    {
        var progress = ViewModel.Progress;
        if (progress == _taskbarProgress)
            return;
        _taskbarProgress = progress;
        try
        {
            Interop.TaskbarProgress.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), progress);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            App.ReportRecoverableError(ex);
        }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Save before potentially cancelling this event for the open-tabs confirmation.
        // Calling Close() after that dialog is accepted does not reliably raise a second
        // AppWindow.Closing event, so waiting for the confirmed pass can lose the bounds.
        SaveWindowPlacement();

        // File > Exit, Alt+F4, the title-bar close button, and other window-close
        // requests all come through this event. Require confirmation when closing
        // open sessions, but let an empty window close immediately.
        if (_closeConfirmed || !ViewModel.AllTabs.Any())
            return;
        if (!App.Settings.Current.ConfirmCloseActiveSessions
            || !ViewModel.AllTabs.Any(tab => !tab.IsAppPage))
            return;

        args.Cancel = true;
        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        _ = ConfirmWindowCloseAsync();
    }

    private async Task ConfirmWindowCloseAsync()
    {
        try
        {
            var count = ViewModel.AllTabs.Count(tab => !tab.IsAppPage);
            if (count == 0)
            {
                _closeConfirmed = true;
                Close();
                return;
            }

            var sessionText = count == 1 ? "session" : "sessions";
            var pronoun = count == 1 ? "it" : "them";
            if (await ConfirmDialog.ConfirmAsync(
                    Root.XamlRoot,
                    "Exit resesh?",
                    $"Are you sure you want to exit? You have {count} open {sessionText}. Exiting will close {pronoun}.",
                    "Exit",
                    acceptY: true))
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = PinButton.IsChecked == true;
    }

    // ---- Command history overlay ----

    private DependencyObject? _historyPreviousFocus;
    private bool _historyOpenedFromTerminal;

    private void InitializeHistoryOverlay()
    {
        HistoryOverlay.CloseRequested += CloseHistory;
        HistoryOverlay.CanInsert = () =>
            ViewModel.ActiveTab is { View: TerminalTabView, State: TabConnectionState.Connected, IsLocked: false };
        HistoryOverlay.InsertCommand = entry =>
        {
            if (ViewModel.ActiveTab?.View is not TerminalTabView view || !view.InsertText(entry.Command))
                return false;
            // Focus belongs to the terminal now; closing must not send it back.
            _historyPreviousFocus = null;
            _historyOpenedFromTerminal = true;
            return true;
        };
        HistoryOverlay.SessionNameFor = entry =>
            entry.SessionId is { } id && App.Store.Find(id) is { } session ? session.Name : null;
        HistoryOverlay.OpenSession = entry =>
        {
            if (entry.SessionId is { } id && App.Store.Find(id) is { } session)
                ConnectSession(session);
        };
        HistoryOverlay.TurnOnRequested = () =>
        {
            if (!App.SaveSettings(App.Settings.Current with { KeepCommandHistory = true }))
                return;
            App.ApplySettingsToAllWindows();
            HistoryOverlay.SetHistoryEnabled(true);
        };
    }

    private void ShowHistory(Guid? sessionId = null, bool openedFromTerminal = false)
    {
        if (CommandPalette.IsOpen)
            CommandPalette.Close();
        if (HistoryOverlay.IsOpen)
        {
            HistoryOverlay.Close();
        }
        else
        {
            _historyOpenedFromTerminal = openedFromTerminal;
            _historyPreviousFocus = openedFromTerminal
                ? null
                : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        }
        var settings = App.Settings.Current;
        HistoryOverlay.Open(settings.KeepCommandHistory, sessionId, settings.FontFamily);
    }

    private void CloseHistory()
    {
        if (!HistoryOverlay.IsOpen)
            return;
        HistoryOverlay.Close();
        if (_historyOpenedFromTerminal || _historyPreviousFocus is null)
            FocusActiveTerminal();
        else
            _ = FocusManager.TryFocusAsync(_historyPreviousFocus, FocusState.Programmatic);
        _historyPreviousFocus = null;
        _historyOpenedFromTerminal = false;
    }

    // ---- agent awareness (Phase 6.2) ----

    private void OnCommandCompletion(TabViewModel tab, Resesh.Core.Backend.CommandCompletion completion)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (Interop.WindowAlerts.IsForeground(hwnd) && tab.IsActive && !tab.IsLocked
            && tab.View is TerminalTabView { IsRewinding: false })
            return; // The tab's completion tooltip is enough when its output is visible.

        var target = new WeakReference<TabViewModel>(tab);
        if (!Interop.CommandCompletionNotifications.TryShow(tab.Header, completion.ProgramName,
                completion.ExitCode, completion.Duration, () =>
                {
                    if (target.TryGetTarget(out var liveTab) && App.WindowFor(liveTab) is { } owner)
                        owner.ActivateCommandOutput(liveTab, completion.ExecutionId);
                }))
        {
            Interop.WindowAlerts.Flash(hwnd);
            ShowOperationNotice("Command finished", "Windows notifications are unavailable. " + tab.CompletionNotificationTooltip);
        }
    }

    private void ActivateCommandOutput(TabViewModel tab, long executionId)
    {
        if (!ViewModel.AllTabs.Contains(tab) || tab.View is not TerminalTabView view)
            return;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Restore();
        var group = ViewModel.GroupOf(tab);
        group.SelectedTab = tab;
        FocusGroup(group);
        Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Interop.WindowAlerts.BringToForeground(hwnd);
        view.ScrollToCommand(executionId);
        view.FocusTerminal();
        Trace($"Command completion activation foreground={Interop.WindowAlerts.IsForeground(hwnd)}");
    }

    /// <summary>
    /// An agent in some tab wants the user. Nothing happens for a tab the user is already
    /// looking at; otherwise the tab's own badge is the notification, plus (per settings)
    /// a taskbar flash and the system sound when the window itself is in the background.
    /// Content never leaves the app: the OS-level signals carry no agent text at all.
    /// </summary>
    private void OnAgentAlert(TabViewModel tab, Resesh.Core.Agents.AgentSnapshot snapshot)
    {
        var settings = App.Settings.Current;
        if (!settings.ShowAgentIcons)
            return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (Interop.WindowAlerts.IsForeground(hwnd) && tab.IsActive)
            return;
        if (!Interop.WindowAlerts.IsForeground(hwnd))
        {
            if (settings.AgentAlertFlash)
                Interop.WindowAlerts.Flash(hwnd);
            if (settings.AgentAlertSound)
                Interop.WindowAlerts.Beep();
        }
    }

    /// <summary>Tab menu: pin an agent identity for this tab (null = auto-detect).</summary>
    public void SetTabAgent(TabViewModel tab, string? key)
    {
        if (tab.View is TerminalTabView view)
            view.SetAgentOverride(key);
    }

    /// <summary>Persists whatever the tab is showing as the session's default identity, so
    /// new tabs of this session start there. Saved sessions only — an ad-hoc or deleted
    /// session has nowhere to keep it.</summary>
    public void SaveTabAgentAsSessionDefault(TabViewModel tab)
    {
        if (tab.View is not TerminalTabView view || App.Store.Find(tab.Session.Id) is not { } session)
            return;
        var key = view.AgentOverride ?? view.AgentState.Key;
        ViewModel.UpdateSession(session with { Agent = key }, null);
    }

    public Task ShowAgentAdaptersAsync() => ShowSettingsAsync(GlobalSettingsTarget.Agents);

    // ---- settings ----

    /// <summary>Opens Settings in this window's Settings tab (or reuses the one already
    /// open) at the given field.</summary>
    private Task ShowSettingsAsync(GlobalSettingsTarget target)
    {
        var tab = OpenAppPage(AppPage.Settings, _ => CreateSettingsPage());
        if (tab.View is SettingsPage page)
            page.Navigate(target);
        return Task.CompletedTask;
    }

    private SettingsPage CreateSettingsPage()
    {
        var settings = new SettingsViewModel(new SettingsEnvironment
        {
            Current = () => App.Settings.Current,
            Save = App.SaveSettings,
            HistorySize = () => App.History.SizeOnDisk(),
            HistoryDirectory = App.History.Directory,
            ClearHistory = () => App.History.Clear(),
            IsStorageFailure = Resesh.Core.History.CommandHistoryStore.IsStorageFailure,
            ReportError = App.ReportRecoverableError,
            CanLaunchAtSignIn = Program.UsesDefaultDataDirectory,
            LaunchAtSignIn = Interop.SignInLaunch.IsEnabled,
            SetLaunchAtSignIn = enabled => Interop.SignInLaunch.TrySet(enabled, Environment.ProcessPath!),
        });
        settings.SettingChanged += property => App.ApplySettingChange(property, source: settings);
        return new SettingsPage(settings);
    }

    /// <summary>Re-reads this window's Settings page after another window or command changed settings.</summary>
    private void RefreshSettingsPage(SettingsViewModel? source)
    {
        // Dragging Settings between windows can leave two Settings tabs in one window.
        foreach (var tab in ViewModel.AllTabs)
        {
            if (tab.View is SettingsPage page && !ReferenceEquals(page.ViewModel, source))
                page.ViewModel.Refresh();
        }
    }

    /// <summary>Applies the persisted settings to the shell and every open terminal.</summary>
    internal void ApplySettingsToApp()
    {
        ApplySettingsToApp(App.Settings.Current);
        if (_sessionsPaneOpen && _selectedRailTab == "recordings")
            _ = ViewModel.RefreshRecordingsAsync();
        RefreshSettingsPage(source: null);
    }

    /// <summary>Applies one saved setting to this window after Settings, in any window, changed it.
    /// <paramref name="source"/> is the Settings page that made the change; it is already current.</summary>
    internal void ApplySettingChange(string property, SettingsViewModel? source)
    {
        RefreshSettingsPage(source);
        var settings = App.Settings.Current;
        switch (property)
        {
            case nameof(SettingsViewModel.Theme):
                ApplyThemeToApp(settings.Theme);
                break;
            case nameof(SettingsViewModel.ShowStatusBar):
                ApplyStatusBarVisibility(settings.ShowStatusBar);
                break;
            case nameof(SettingsViewModel.SessionsPaneLayout):
                UpdatePaneMode();
                break;
            case nameof(SettingsViewModel.RecordingDirectory):
                if (_sessionsPaneOpen && _selectedRailTab == "recordings")
                    _ = ViewModel.RefreshRecordingsAsync();
                break;
            default:
                ApplyTerminalSettings(settings);
                break;
        }
    }

    /// <summary>Terminals re-read the saved highlighting rules.</summary>
    internal void RefreshHighlights() => PreviewHighlights(null);

    private void PreviewHighlights(HighlightsStore? draft)
    {
        foreach (var tab in ViewModel.AllTabs)
            if (tab.View is TerminalTabView view)
                view.PreviewHighlights(draft);
    }
    private void ApplyStatusBarVisibility(bool visible)
    {
        StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StatusBarMenuItem.IsChecked = visible;
    }

    private void SetStatusBarVisible(bool visible)
    {
        if (App.SaveSettings(App.Settings.Current with { ShowStatusBar = visible }))
            App.ApplySettingChange(nameof(SettingsViewModel.ShowStatusBar));
    }

    private void ApplySettingsToApp(AppSettings settings)
    {
        ApplyThemeToApp(settings.Theme);
        ApplyStatusBarVisibility(settings.ShowStatusBar);
        UpdatePaneMode();
        ApplyTerminalSettings(settings);
    }

    private void ApplyTerminalSettings(AppSettings settings)
    {
        foreach (var tab in ViewModel.AllTabs)
        {
            if (tab.View is TerminalTabView view)
                view.ApplyNonThemeSettings(settings);
            tab.NotifyAgentVisuals(); // the agent icon/badge is gated on a setting
        }
    }

    /// <summary>Shows an unsaved theme in this window, for Welcome's theme choice.</summary>
    internal void PreviewTheme(string theme) => ApplyThemeToApp(theme);

    /// <summary>Applies a reversible Settings-dialog theme preview without repeating
    /// layout, scrollback, or highlight work in every open terminal.</summary>
    private void ApplyThemeToApp(string theme)
    {
        theme = App.ResolveTheme(theme);
        var version = ++_themeApplyVersion;
        var requestedTheme = ThemeCatalog.IsLight(theme) ? ElementTheme.Light : ElementTheme.Dark;
        if (Root.RequestedTheme == requestedTheme)
        {
            ApplyThemePalette(theme);
            return;
        }

        // Fluent controls resolve a light/dark RequestedTheme change during the next
        // composition pass. Apply custom brushes in that pass too, before it is drawn,
        // so the tree, tab chrome, terminals, and Fluent buttons change as one frame.
        void ApplyPaletteBeforeRender(object? sender, object args)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= ApplyPaletteBeforeRender;
            if (version == _themeApplyVersion)
                ApplyThemePalette(theme);
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += ApplyPaletteBeforeRender;
        Root.RequestedTheme = requestedTheme;
    }

    private void ApplyThemePalette(string theme)
    {
        var palette = ThemeVisualPalette.For(theme);
        _themePalette = palette;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionShellBrush"]).Color = palette.Shell;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionChromeBrush"]).Color = palette.Chrome;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionAccentBrush"]).Color = palette.Accent;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionInputBrush"]).Color = palette.Input;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionChromeFrameBrush"]).Color = palette.Frame;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionTreeForegroundBrush"]).Color = palette.TreeForeground;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionTreeMutedForegroundBrush"]).Color = palette.TreeMutedForeground;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionTreeSelectionBrush"]).Color = palette.TreeSelection;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SessionTreeSelectionForegroundBrush"]).Color = palette.TreeSelectionForeground;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SettingsCardBorderBrush"]).Color = palette.Frame;
        ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["SettingsCardBackgroundBrush"]).Color = palette.Input;
        RepaintSplitterLines();
        foreach (var groupView in _groupViews.Values)
            groupView.ApplyTheme(palette);
        ApplyTitleBarButtonColors(theme); // caption buttons don't follow XAML theming
        foreach (var tab in ViewModel.AllTabs)
        {
            if (tab.View is TerminalTabView view)
                view.ApplyTheme(theme);
            tab.ApplyAppTheme(theme);
        }
    }

    private void SyncEmptyState()
    {
        EmptyState.Visibility = App.Store.Sessions.Count == 0 && !ViewModel.IsFiltering
            ? Visibility.Visible : Visibility.Collapsed;
        NoFilterMatchesState.Visibility = ViewModel.IsFiltering && ViewModel.MatchCount == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly object TraceGate = new();

    [System.Diagnostics.Conditional("DEBUG")]
    internal static void Trace(string message)
    {
        try
        {
            var dir = AppDataPaths.Local();
            Directory.CreateDirectory(dir);
            // TraceHook producers (SSH/ConPTY read loops) call this off the UI thread, and a
            // second app instance or a log tail may hold the file — so serialize in-process,
            // share the handle, and never let diagnostics throw into the caller.
            lock (TraceGate)
            {
                using var stream = new FileStream(
                    Path.Combine(dir, "trace.log"),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
        }
        catch (IOException)
        {
        }
    }

    // ---- Dialog helpers ----

    private async Task OpenSessionEditorAsync(Session? existing, string defaultFolder,
        SessionKind newKind = SessionKind.Ssh)
    {
        var dialog = new SessionEditDialog(ViewModel.FolderPathsForPicker, existing, defaultFolder, App.SshKeys,
            newKind: newKind)
        {
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowModalAsync();
        if (dialog.Result is not { } result)
            return;

        if (existing is null)
            ViewModel.AddSession(result, dialog.Password);
        else
            ViewModel.UpdateSession(result, dialog.Password);
    }

    private Task<string?> PromptAsync(string title, string placeholder, string initial) =>
        TextPromptDialog.PromptAsync(Root.XamlRoot, title, placeholder, initial);

    private Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryText = "Delete",
        bool acceptY = false) =>
        ConfirmDialog.ConfirmAsync(Root.XamlRoot, title, message, primaryText, acceptY);
}

/// <summary>One row in the quick connect dropdown: a saved session match or an ad-hoc target.</summary>
public sealed class QuickConnectSuggestion
{
    public string Display { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "\uEDA2";
    public Session Session { get; init; } = null!;

    /// <summary>AutoSuggestBox writes this into the text box when a suggestion is chosen.</summary>
    public override string ToString() => Display;
}
