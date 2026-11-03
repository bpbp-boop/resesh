using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Resesh.App.Interop;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Windows.UI.ViewManagement;

namespace Resesh.App;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    private MainWindow? _lastActiveWindow;
    private DispatcherQueue? _dispatcherQueue;

    public static SessionStore Store { get; } = new(DataFile("sessions.json"));
    public static SshKeyStore SshKeys { get; } = new(DataFile("ssh-keys.json"));
    public static IdentityStore Identities { get; } = new(DataFile("identities.json"));

    /// <summary>Resolves session logins against the live session and identity stores.</summary>
    public static Resesh.Core.Identities.LoginResolver Logins { get; } = new(Store, Identities);

    /// <summary>Passwords typed for "ask every time" identities, until the app closes.</summary>
    public static Resesh.Core.Identities.AskedSecretCache AskedSecrets { get; } = new();

    /// <summary>The session agent binaries this copy of the app carries, or null when it was
    /// built without them (eng/build-agent.ps1). Persistence then uses tmux everywhere.</summary>
    public static Resesh.Core.Ssh.AgentPackage? AgentPackage { get; } =
        Resesh.Core.Ssh.AgentPackage.Load(Path.Combine(AppContext.BaseDirectory, "Agent"));

    /// <summary>Persistent shell ownership across every window and connect worker.</summary>
    public static TmuxSlotReservations TmuxSlots { get; } = new();

    /// <summary>The username a session logs in as: its own, or its identity's (with any
    /// per-session override). Empty when it will be asked.</summary>
    public static string LoginUsername(Session session) =>
        session.Kind == SessionKind.Ssh ? Logins.Resolve(session).Username : session.Username;
    public static ICredentialService Credentials { get; } = DemoMode.CreateCredentialService();
    public static KnownHostsStore KnownHosts { get; } = new(DataFile("known_hosts.json"));
    public static SettingsStore Settings { get; } = new(DataFile("settings.json"));
    public static HighlightsStore Highlights { get; } = new(DataFile("highlights.json"));
    public static WorkspaceStore Workspaces { get; } = new(DataFile("workspaces.json"));
    public static Resesh.Core.History.CommandHistoryStore History { get; } =
        new(Program.StorePath("history", Resesh.Core.History.CommandHistoryStore.DefaultDirectory));
    private static AccessibilitySettings Accessibility { get; } = new();
    public static bool IsHighContrast => Accessibility.HighContrast;


    /// <summary>A file in the data folder every store, backup and the single-instance key share.</summary>
    private static string DataFile(string fileName) => Path.Combine(Program.DataDirectory, fileName);
    public static Resesh.App.Icons.SessionIconCatalog Icons { get; } = new();
    public static Resesh.App.Icons.BrandIconImages Brands { get; } = new();

    /// <summary>Built-in local profiles whose shell is installed right now (set once at
    /// launch by discovery). Built-ins outside this set are hidden, not deleted.</summary>
    public static IReadOnlySet<Guid> AvailableLocalShells { get; private set; } = new HashSet<Guid>();

    /// <summary>Whether a saved session is shown anywhere (tree, search, Recent, the jump
    /// list): everything except built-in local profiles whose shell is not installed.</summary>
    public static bool IsSessionVisible(Session session) =>
        !session.IsLocal || !session.BuiltIn || AvailableLocalShells.Contains(session.Id);

    /// <summary>Resolves the System choice to the Windows color mode used right now.</summary>
    public static string ResolveTheme(string? theme)
    {
        if (!ThemeCatalog.IsSystem(theme))
            return ThemeCatalog.Find(theme).Id;

        var background = new UISettings().GetColorValue(UIColorType.Background);
        var luminance = (0.2126 * background.R) + (0.7152 * background.G) + (0.0722 * background.B);
        return luminance >= 128 ? ThemeIds.Light : ThemeIds.Dark;
    }

    public App()
    {
        StartLoadingStores();
        _settingsLoad!.GetAwaiter().GetResult();

        // Application-level theme must be set before any UI exists; it also themes
        // popups/dialogs, which don't inherit element-level RequestedTheme.
        RequestedTheme = ThemeCatalog.IsLight(ResolveTheme(Settings.Current.Theme))
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            // Unexpected UI failures have no proven recovery path. Let WinUI terminate.
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
    }

    private static Task? _settingsLoad;
    private static Task? _storesLoad;

    /// <summary>Starts reading settings and the stores on worker threads. Main calls this once it
    /// knows the process will run the app, so the reads (mostly one-time JSON serializer work)
    /// overlap WinUI's own startup; the UI thread waits only where it first needs the data.</summary>
    internal static void StartLoadingStores()
    {
        if (_settingsLoad is not null)
            return;
        if (!DemoMode.IsEnabled)
            MigrateLegacyDataDir();
        // Connections read the algorithm defaults from whatever settings are saved when they start.
        Resesh.Core.Ssh.SshAlgorithmPolicy.AppDefaultsSource = () => Settings.Current.SshAlgorithms;
        _settingsLoad = Task.Run(Settings.Load);
        _storesLoad = Task.Run(() =>
        {
            Store.Load();
            SshKeys.Load();
            Identities.Load();
            KnownHosts.Load();
            Highlights.Load();
            Workspaces.Load();
        });
    }

    /// <summary>One-time rename migration: %APPDATA%\Sessions → %APPDATA%\Resesh.
    /// Moves the whole directory when the new one doesn't exist yet; on failure the
    /// app just starts fresh and the old data stays untouched.</summary>
    private static void MigrateLegacyDataDir()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = Path.Combine(appData, "Sessions");
            var newDir = Path.Combine(appData, "Resesh");
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                Directory.Move(oldDir, newDir);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    internal static bool SaveSettings(AppSettings settings)
    {
        try
        {
            Settings.Save(settings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportRecoverableError(exception);
            return false;
        }
    }

    internal static void ReportRecoverableError(Exception exception)
    {
        LogCrash(exception);
        if (Current is not App app) return;
        foreach (var window in app._windows.ToList())
        {
            try { window.ShowOperationNotice("The operation could not finish", exception.Message); }
            catch (Exception displayError) { LogCrash(displayError); } // The window may already be closed.
        }
    }

    private static void LogCrash(Exception? ex)
    {
        if (!Settings.Current.WriteCrashReports)
            return;

        try
        {
            var dir = AppDataPaths.Local();
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // logging must never take the app down
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _uiQueue = _dispatcherQueue;
        CommandCompletionNotifications.SetDispatcher(_dispatcherQueue);
        // The app-wide chrome brushes are declared without colors; give them the saved
        // theme's before any window draws with them. Not in the constructor: code can't
        // read Application resources until it has returned.
        ThemeVisualPalette.For(ResolveTheme(Settings.Current.Theme)).ApplyToAppResources();

        _storesLoad!.GetAwaiter().GetResult();
        if (DemoMode.IsEnabled)
        {
            DemoMode.Seed(Store);
        }
        else
        {
            try
            {
                SshKeys.MigrateLegacySessions(Store, Credentials);
            }
            catch (Exception ex)
            {
                LogCrash(ex); // keep legacy session data usable if key-registry migration fails
            }
            try
            {
                // Adds newly discovered shells as built-in local profiles (stable ids) and
                // reports which built-ins are available; unavailable ones are hidden.
                AvailableLocalShells = Resesh.Core.Local.LocalShellDiscovery.SyncBuiltIns(Store);
            }
            catch (Exception ex)
            {
                LogCrash(ex); // discovery must never block launch; local profiles just stay hidden
            }
        }
#if DEBUG
        Resesh.Core.Ssh.SshTerminalSession.TraceHook = message => MainWindow.Trace(message);
        Resesh.Core.Local.LocalTerminalSession.TraceHook = message => MainWindow.Trace(message);
        Resesh.Core.Telnet.TelnetTerminalSession.TraceHook = message => MainWindow.Trace(message);
        Resesh.Core.Serial.SerialTerminalSession.TraceHook = message => MainWindow.Trace(message);
        Resesh.Terminal.TerminalSurface.TraceHook = message => MainWindow.Trace(message);
        TaskbarIntegration.TraceHook = message => MainWindow.Trace(message);
#endif
        Resesh.Terminal.TerminalSurface.Shortcuts = AppShortcuts.ForTerminals();
        PruneCommandHistory();
        // Windows follow the store themselves; views outside them and the jump list follow here.
        Store.Changed += () => RunOnUiThread(() =>
        {
            SessionsChanged?.Invoke();
            RefreshJumpList(); // sessions were renamed, added or deleted
        });
        // An identity's username shows in tab endpoints without any session changing.
        Identities.Changed += () => RunOnUiThread(() =>
        {
            foreach (var tab in AllTabs)
                tab.NotifyLoginChanged();
        });
        var window = CreateWindowCore();
        if (Settings.Current.ReopenLastLayoutAtStartup)
            window.RestoreLastLayout();
        else
            window.RestorePinnedSessions();
        window.OpenWelcomeIfNeeded();

        ApplyLaunchRequest(window, LaunchRequest.Parse(Environment.GetCommandLineArgs()));
        RefreshJumpList();

        var loadMessages = new[]
            {
                Settings.LoadWarning, Store.LoadWarning, SshKeys.LoadWarning, Identities.LoadWarning, Highlights.LoadWarning,
                Workspaces.LoadWarning, KnownHosts.LoadWarning, KnownHosts.LoadError,
            }
            .Where(message => message is not null).ToList();
        if (loadMessages.Count > 0)
            window.ShowOperationNotice("Stored data needs attention", string.Join("\n", loadMessages));
        Program.SetActivationTarget(this);
        // Registering for notification clicks takes ~70 ms of COM work; do it once the
        // first frame is up rather than before the window can appear.
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CommandCompletionNotifications.Register);
        // Every terminal compiles the highlight rules (20-30 ms); do it once, in the background,
        // so opening the first tab does not wait for it.
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var rules = Terminal.TerminalTabView.HighlightPayload(Highlights, null);
            _ = Task.Run(() => Resesh.Terminal.TerminalSurface.PrecompileHighlights(rules));
        });
    }


    /// <summary>Deletes history past the retention period, off the UI thread. History is
    /// pruned even while it is turned off, so the retention promise still holds.</summary>
    internal static void PruneCommandHistory()
    {
        var days = SettingRanges.CommandHistoryDays.Clamp(Settings.Current.CommandHistoryDays);
        _ = Task.Run(() =>
        {
            try { History.Prune(days); }
            catch (Exception exception) when (Resesh.Core.History.CommandHistoryStore.IsStorageFailure(exception))
            {
                LogCrash(exception);
            }
        });
    }

    /// <summary>Another launch was redirected here. A plain launch opens a new window; one that
    /// names sessions or recordings (a jump list item) opens them in the window used last.</summary>
    internal void HandleRedirectedActivation(IReadOnlyList<string> commandLine)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            var request = LaunchRequest.Parse(commandLine);
            if (!request.OpensSomething || _lastActiveWindow is not { } window || !_windows.Contains(window))
                window = CreateWindowCore();
            ApplyLaunchRequest(window, request);
            window.BringToFront();
        });
    }

    /// <summary>Creates a blank, app-owned window. Keeping every window rooted here is
    /// required because WinUI does not expose an application window collection.</summary>
    public static MainWindow OpenNewWindow() => ((App)Current).CreateWindowCore();

    internal static MainWindow? WindowFor(Resesh.App.ViewModels.TabViewModel tab) =>
        Current is App app ? app._windows.FirstOrDefault(window => window.ViewModel.AllTabs.Contains(tab)) : null;

    internal static IEnumerable<ViewModels.TabViewModel> AllTabs =>
        Current is App app ? app._windows.SelectMany(window => window.ViewModel.AllTabs) : [];

    private bool _pinnedSessionsSaveQueued;

    /// <summary>Brings the pinned sessions reopened at launch in line with Home's pinned tabs in
    /// every window, once the change in progress settles. Windows queue it whenever a tab is
    /// pinned, unpinned, opened, closed or moved, or a window closes, so the saved list never
    /// waits for the next pin toggle to catch up.</summary>
    internal static void QueuePinnedSessionsSave()
    {
        if (Current is not App { _pinnedSessionsSaveQueued: false, _dispatcherQueue: { } queue } app)
            return;
        app._pinnedSessionsSaveQueued = true;
        queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            app._pinnedSessionsSaveQueued = false;
            SavePinnedSessions();
        });
    }

    /// <summary>Saves the pinned sessions now. A window calls it as it closes, before its tabs
    /// go, so a change still queued is not lost when it is the last window.</summary>
    internal static void SavePinnedSessions()
    {
        if (Current is not App app
            || ViewModels.PinnedSessions.ToSave(
                Settings.Current.PinnedSessionIds,
                app._windows.SelectMany(window => window.HomeTabs),
                app._windows.Count) is not { } ids)
            return;
        SaveSettings(Settings.Current with { PinnedSessionIds = ids });
        RefreshJumpList();
    }

    /// <summary>Re-reads sessions everywhere after a change the session store does not see,
    /// such as identities an import added. Saved session changes need no call: every window
    /// follows <see cref="SessionStore.Changed"/>.</summary>
    internal static void RefreshSessionsInAllWindows()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.ViewModel.RefreshSessions();
        SessionsChanged?.Invoke();
    }

    /// <summary>A recording started or stopped in some tab: every window showing the
    /// Recordings list reads the folder again.</summary>
    internal static void RefreshRecordingsInAllWindows()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.RefreshRecordingsIfShown();
    }

    /// <summary>Raised on the UI thread after saved sessions, folders or folder identities
    /// change, for views outside the tree.</summary>
    internal static event Action? SessionsChanged;

    /// <summary>Runs an action on the UI thread: at once when already on it, else queued.</summary>
    internal static void RunOnUiThread(Action action)
    {
        if (_uiQueue is { HasThreadAccess: false } queue)
            queue.TryEnqueue(() => action());
        else
            action();
    }

    private static DispatcherQueue? _uiQueue;

    /// <summary>Every window's Recent pane re-reads the saved list (one window opening a
    /// session changes it for all).</summary>
    internal static void RefreshRecentSessionsInAllWindows()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.ViewModel.RefreshRecentSessions();
    }

    internal static void RefreshWindowTitles()
    {
        if (Current is not App app)
            return;
        var showContext = app._windows.Count > 1;
        foreach (var window in app._windows.ToList())
            window.RefreshWindowTitle(showContext);
    }

    /// <summary>Settings are app-wide: every window applies them, not only the one that saved.</summary>
    internal static void ApplySettingsToAllWindows()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.ApplySettingsToApp();
    }

    /// <summary>Previews an unsaved theme in every window. Welcome previews themes and can be
    /// dragged between windows, so a preview and its cancel must reach them all.</summary>
    internal static void PreviewThemeInAllWindows(string theme)
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.PreviewTheme(theme);
    }

    /// <summary>Applies one saved setting in every window. <paramref name="source"/> is the
    /// Settings page that changed it, if any; other windows' Settings pages re-read.</summary>
    internal static void ApplySettingChange(string property, ViewModels.SettingsViewModel? source = null)
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.ApplySettingChange(property, source);
        if (property is nameof(ViewModels.SettingsViewModel.KeepCommandHistory)
            or nameof(ViewModels.SettingsViewModel.CommandHistoryDays))
            PruneCommandHistory();
    }

    /// <summary>Saves one setting changed outside the Settings page (a paste prompt, a menu) and
    /// applies it the way the page does, so every window and every open Settings page shows it.
    /// <paramref name="property"/> is the <see cref="ViewModels.SettingsViewModel"/> property.</summary>
    internal static bool SaveSetting(Func<AppSettings, AppSettings> change, string property)
    {
        if (!SaveSettings(change(Settings.Current)))
            return false;
        ApplySettingChange(property);
        return true;
    }

    /// <summary>Open terminals in every window re-read the saved highlighting rules.</summary>
    internal static void RefreshHighlightsInAllWindows()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.RefreshHighlights();
    }

    internal static void RefreshWorkspaceMenus()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.RefreshWorkspaceMenu();
        RefreshWindowTitles();
    }

    /// <summary>Every window's rail shows which workspaces the others hold, so a workspace
    /// opening or closing anywhere refreshes them all.</summary>
    internal static void RefreshWorkspaceRails()
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.RefreshWorkspaceMenu();
    }

    /// <summary>The window holding a workspace, if any. A workspace is open in one window at
    /// most, so its connections are never opened twice.</summary>
    internal static MainWindow? WindowWithWorkspace(Guid workspaceId, MainWindow? except) =>
        Current is App app
            ? app._windows.FirstOrDefault(window => !ReferenceEquals(window, except) && window.IsWorkspaceOpen(workspaceId))
            : null;

    internal static void DiscardWorkspaceInAllWindows(Guid workspaceId)
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.DiscardWorkspace(workspaceId);
    }

    internal static void SetTabContentDropTargetsVisible(bool visible)
    {
        if (Current is not App app)
            return;
        foreach (var window in app._windows.ToList())
            window.SetTabContentDropTargetsVisibleCore(visible);
    }

    private MainWindow CreateWindowCore()
    {
        var window = new MainWindow();
        TaskbarIntegration.ConfigureWindow(
            WinRT.Interop.WindowNative.GetWindowHandle(window),
            Program.RelaunchCommand(),
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        _windows.Add(window);
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _lastActiveWindow = window;
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            QueuePinnedSessionsSave(); // its pinned tabs no longer reopen, unless it was the last
            RefreshWindowTitles();
            RefreshWorkspaceRails(); // its workspaces are no longer open elsewhere
        };
        window.Activate();
        RefreshWindowTitles();
        return window;
    }

    /// <summary>Opens what a command line names: `--session <id>` (the jump list), `--open <name>`
    /// (the automated UI test rig) and `--open-recording <path>`, each repeatable.</summary>
    private static void ApplyLaunchRequest(MainWindow window, LaunchRequest request)
    {
        foreach (var id in request.SessionIds)
        {
            if (Store.Find(id) is { } session)
                window.OpenSessionFromLaunch(session);
        }
        foreach (var name in request.SessionNames)
        {
            if (Store.Sessions.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } session)
                window.OpenSessionFromLaunch(session);
        }
        foreach (var path in request.RecordingPaths)
        {
            try
            {
                window.OpenRecordingFromLaunch(path);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
            }
        }
    }

    private JumpListPlan? _jumpList;
    private bool _jumpListQueued;
    private readonly HashSet<string> _removedJumpListItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rebuilds the taskbar jump list from pinned and recent sessions, once per burst of
    /// changes. Skipped in demo mode and with --data-dir: its items launch the default data.</summary>
    internal static void RefreshJumpList()
    {
        if (Current is not App app || app._jumpListQueued || !Program.UsesDefaultDataDirectory)
            return;
        app._jumpListQueued = app._dispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, app.UpdateJumpList) == true;
    }

    private void UpdateJumpList()
    {
        _jumpListQueued = false;
        var settings = Settings.Current;
        // Recent here lists what the Recent pane lists: saved sessions that are visible.
        var plan = JumpListPlan.For(settings.PinnedSessionIds, settings.RecentSessionIds,
                id => Store.Find(id) is { } session && IsSessionVisible(session) ? session : null,
                LoginUsername)
            .Without(_removedJumpListItems);
        if (plan.SameAs(_jumpList))
            return;
        _jumpList = plan;
        var removed = TaskbarIntegration.UpdateJumpList(plan, Environment.ProcessPath!);
        if (removed.Count == 0)
            return;

        // The user removed items from the jump list; they leave Recent too, and a pinned
        // tab's item stays off the list until resesh restarts.
        _removedJumpListItems.UnionWith(removed);
        var removedIds = LaunchRequest.Parse(removed.SelectMany(SplitArguments).ToList()).SessionIds.ToHashSet();
        var recent = settings.RecentSessionIds.Where(id => !removedIds.Contains(id)).ToList();
        if (recent.Count != settings.RecentSessionIds.Count
            && SaveSettings(Settings.Current with { RecentSessionIds = recent }))
        {
            RefreshRecentSessionsInAllWindows();
        }
        // The committed list already left them out; the next change rebuilds it from Recent.
        _jumpList = null;
    }

    // Jump list arguments are written by JumpListPlan: an option and a GUID, no quoting.
    private static string[] SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
