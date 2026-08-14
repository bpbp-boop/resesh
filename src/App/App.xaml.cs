using Microsoft.UI.Xaml;
using Sessions.Core.Credentials;
using Sessions.Core.Ssh;
using Sessions.Core.Storage;

namespace Sessions.App;

public partial class App : Application
{
    private Window? _window;

    public static SessionStore Store { get; } = new(SessionStore.DefaultPath);
    public static ICredentialService Credentials { get; } = new WindowsCredentialService();
    public static KnownHostsStore KnownHosts { get; } = new(KnownHostsStore.DefaultPath);
    public static SettingsStore Settings { get; } = new(SettingsStore.DefaultPath);

    public App()
    {
        // Application-level theme must be set before any UI exists; it also themes
        // popups/dialogs, which don't inherit element-level RequestedTheme.
        Settings.Load();
        RequestedTheme = Settings.Current.Theme == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark;

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = true; // keep the app alive; the failure is logged
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sessions");
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
        KnownHosts.Load();
#if DEBUG
        Sessions.Core.Ssh.SshTerminalSession.TraceHook = message => MainWindow.Trace(message);
#endif
        var window = new MainWindow();
        _window = window;
        window.Activate();
        window.RestorePinnedSessions();
    }
}
