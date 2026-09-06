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
    private DispatcherQueue? _dispatcherQueue;

    public static SessionStore Store { get; } = new(StorePath("sessions.json", SessionStore.DefaultPath));
    public static SshKeyStore SshKeys { get; } = new(StorePath("ssh-keys.json", SshKeyStore.DefaultPath));
    public static ICredentialService Credentials { get; } = DemoMode.CreateCredentialService();
    public static KnownHostsStore KnownHosts { get; } = new(StorePath("known_hosts.json", KnownHostsStore.DefaultPath));
    public static SettingsStore Settings { get; } = new(StorePath("settings.json", SettingsStore.DefaultPath));
    public static HighlightsStore Highlights { get; } = new(StorePath("highlights.json", HighlightsStore.DefaultPath));
    public static WorkspaceStore Workspaces { get; } = new(StorePath("workspaces.json", WorkspaceStore.DefaultPath));
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
            e.Handled = true; // keep the app alive; the failure is logged
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

    private static void LogCrash(Exception? ex)
    {
        if (!Settings.Current.WriteCrashReports)
            return;

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resesh");
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
        Resesh.Terminal.TerminalControl.TraceHook = message => MainWindow.Trace(message);
        Resesh.Terminal.NativeTerminalSurface.TraceHook = message => MainWindow.Trace(message);
#endif
        var window = CreateWindowCore();
        if (Settings.Current.ReopenLastLayoutAtStartup)
            window.RestoreLastLayout();
        else
            window.RestorePinnedSessions();
        window.OpenWelcomeIfNeeded();

        ApplyLaunchArguments(window, Environment.GetCommandLineArgs());

        Program.SetActivationTarget(this);
    }


    internal void HandleRedirectedActivation()
    {
        _dispatcherQueue?.TryEnqueue(() => CreateWindowCore());
    }

    /// <summary>Creates a blank, app-owned window. Keeping every window rooted here is
    /// required because WinUI does not expose an application window collection.</summary>
    public static MainWindow OpenNewWindow() => ((App)Current).CreateWindowCore();

    internal static void RefreshWindowTitles()
    {
        if (Current is not App app)
            return;
        var showContext = app._windows.Count > 1;
        foreach (var window in app._windows.ToList())
            window.RefreshWindowTitle(showContext);
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
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            RefreshWindowTitles();
        };
        window.Activate();
        RefreshWindowTitles();
        return window;
    }

    private static void ApplyLaunchArguments(MainWindow window, IReadOnlyList<string> args)
    {
        // `--open <session name>` (repeatable): open saved sessions at launch. Used by
        // the automated UI test rig; harmless for normal launches.
        for (var i = 1; i < args.Count - 1; i++)
        {
            if (args[i] == "--open"
                && Store.Sessions.FirstOrDefault(s =>
                    s.Name.Equals(args[i + 1], StringComparison.OrdinalIgnoreCase)) is { } session)
            {
                window.OpenSessionFromLaunch(session);
            }
            else if (args[i] == "--open-recording")
            {
                try
                {
                    window.OpenRecordingFromLaunch(args[i + 1]);
                }
                catch (Exception ex)
                {
                    LogCrash(ex);
                }
            }
        }
    }
}
