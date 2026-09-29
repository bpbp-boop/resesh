using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Resesh.App.Interop;
using Resesh.Core.Credentials;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Windows.UI.ViewManagement;

namespace Resesh.App;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    private MainWindow? _lastActiveWindow;
    private DispatcherQueue? _dispatcherQueue;

    public static SessionStore Store { get; } = new(StorePath("sessions.json", SessionStore.DefaultPath));
    public static SshKeyStore SshKeys { get; } = new(StorePath("ssh-keys.json", SshKeyStore.DefaultPath));
    public static ICredentialService Credentials { get; } = DemoMode.CreateCredentialService();
    public static KnownHostsStore KnownHosts { get; } = new(StorePath("known_hosts.json", KnownHostsStore.DefaultPath));
    public static SettingsStore Settings { get; } = new(StorePath("settings.json", SettingsStore.DefaultPath));
    public static HighlightsStore Highlights { get; } = new(StorePath("highlights.json", HighlightsStore.DefaultPath));
    public static WorkspaceStore Workspaces { get; } = new(StorePath("workspaces.json", WorkspaceStore.DefaultPath));
    public static Resesh.Core.History.CommandHistoryStore History { get; } =
        new(StorePath("history", Resesh.Core.History.CommandHistoryStore.DefaultDirectory));
    private static AccessibilitySettings Accessibility { get; } = new();
    public static bool IsHighContrast => Accessibility.HighContrast;


    /// <summary>Resolves the shared data location used for stores and app instancing.</summary>
    private static string StorePath(string fileName, string defaultPath) =>
        Program.StorePath(fileName, defaultPath);
    public static Resesh.App.Icons.SessionIconCatalog Icons { get; } = new();

    /// <summary>Built-in local profiles whose shell is installed right now (set once at
    /// launch by discovery). Built-ins outside this set are hidden, not deleted.</summary>
    public static IReadOnlySet<Guid> AvailableLocalShells { get; private set; } = new HashSet<Guid>();

    /// <summary>Resolves the System choice to the Windows color mode used right now.</summary>
    public static string ResolveTheme(string? theme)
    {
        if (!string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase))
            return ThemeCatalog.Find(theme).Id;

        var background = new UISettings().GetColorValue(UIColorType.Background);
        var luminance = (0.2126 * background.R) + (0.7152 * background.G) + (0.0722 * background.B);
        return luminance >= 128 ? "light" : "dark";
    }

    public App()
    {
        if (!DemoMode.IsEnabled)
            MigrateLegacyDataDir();

        // Application-level theme must be set before any UI exists; it also themes
        // popups/dialogs, which don't inherit element-level RequestedTheme.
        Settings.Load();
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
        try { Settings.Save(settings); return true; }
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
        CommandCompletionNotifications.SetDispatcher(_dispatcherQueue);

        Store.Load();
        SshKeys.Load();
        KnownHosts.Load();
        Highlights.Load();
        Workspaces.Load();
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
        Resesh.Terminal.TerminalControl.TraceHook = message => MainWindow.Trace(message);
        TaskbarIntegration.TraceHook = message => MainWindow.Trace(message);
        Resesh.Terminal.NativeTerminalSurface.TraceHook = message => MainWindow.Trace(message);
#endif
        Resesh.Terminal.TerminalSurface.Shortcuts = AppShortcuts.ForTerminals();
        PruneCommandHistory();
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
                Settings.LoadWarning, Store.LoadWarning, SshKeys.LoadWarning, Highlights.LoadWarning,
                Workspaces.LoadWarning, KnownHosts.LoadWarning, KnownHosts.LoadError,
            }
            .Where(message => message is not null).ToList();
        if (loadMessages.Count > 0)
            window.ShowOperationNotice("Stored data needs attention", string.Join("\n", loadMessages));
        Program.SetActivationTarget(this);
    }


    /// <summary>Deletes history past the retention period, off the UI thread. History is
    /// pruned even while it is turned off, so the retention promise still holds.</summary>
    internal static void PruneCommandHistory()
    {
        var days = Math.Clamp(Settings.Current.CommandHistoryDays, 1, 3650);
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
            RefreshWindowTitles();
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
        var plan = JumpListPlan.For(settings.PinnedSessionIds, settings.RecentSessionIds, Store.Find)
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
            foreach (var window in _windows.ToList())
                window.ViewModel.RefreshRecentSessions();
        }
        // The committed list already left them out; the next change rebuilds it from Recent.
        _jumpList = null;
    }

    // Jump list arguments are written by JumpListPlan: an option and a GUID, no quoting.
    private static string[] SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
