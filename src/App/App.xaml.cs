using Microsoft.UI.Xaml;
using Resesh.Core.Credentials;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.App;

public partial class App : Application
{
    private Window? _window;

    public static SessionStore Store { get; } = new(StorePath("sessions.json", SessionStore.DefaultPath));
    public static SshKeyStore SshKeys { get; } = new(StorePath("ssh-keys.json", SshKeyStore.DefaultPath));
    public static ICredentialService Credentials { get; } = new WindowsCredentialService();
    public static KnownHostsStore KnownHosts { get; } = new(StorePath("known_hosts.json", KnownHostsStore.DefaultPath));
    public static SettingsStore Settings { get; } = new(StorePath("settings.json", SettingsStore.DefaultPath));
    public static HighlightsStore Highlights { get; } = new(StorePath("highlights.json", HighlightsStore.DefaultPath));

    /// <summary>`--data-dir <path>`: keep every JSON store in an alternate directory.
    /// Used by the automated UI test rig; normal launches use the %APPDATA% defaults.
    /// Secrets stay in Windows Credential Manager either way.</summary>
    private static string StorePath(string fileName, string defaultPath)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
                return Path.Combine(Path.GetFullPath(args[i + 1]), fileName);
        }
        return defaultPath;
    }
    public static Resesh.App.Icons.SessionIconCatalog Icons { get; } = new();

    /// <summary>Built-in local profiles whose shell is installed right now (set once at
    /// launch by discovery). Built-ins outside this set are hidden, not deleted.</summary>
    public static IReadOnlySet<Guid> AvailableLocalShells { get; private set; } = new HashSet<Guid>();

    public App()
    {
        MigrateLegacyDataDir();

        // Application-level theme must be set before any UI exists; it also themes
        // popups/dialogs, which don't inherit element-level RequestedTheme.
        Settings.Load();
        RequestedTheme = ThemeCatalog.IsLight(Settings.Current.Theme) ? ApplicationTheme.Light : ApplicationTheme.Dark;

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
        Store.Load();
        SshKeys.Load();
        KnownHosts.Load();
        Highlights.Load();
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
#if DEBUG
        Resesh.Core.Ssh.SshTerminalSession.TraceHook = message => MainWindow.Trace(message);
        Resesh.Core.Local.LocalTerminalSession.TraceHook = message => MainWindow.Trace(message);
        Resesh.Terminal.TerminalControl.TraceHook = message => MainWindow.Trace(message);
#endif
        var window = new MainWindow();
        _window = window;
        window.Activate();
        window.RestorePinnedSessions();

        // `--open <session name>` (repeatable): open saved sessions at launch. Used by
        // the automated UI test rig; harmless for normal launches.
        var args2 = Environment.GetCommandLineArgs();
        for (var i = 1; i < args2.Length - 1; i++)
        {
            if (args2[i] == "--open"
                && Store.Sessions.FirstOrDefault(s =>
                    s.Name.Equals(args2[i + 1], StringComparison.OrdinalIgnoreCase)) is { } session)
            {
                window.OpenSessionFromLaunch(session);
            }
        }
    }
}
