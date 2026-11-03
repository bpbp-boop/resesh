using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Resesh.Core.Storage;
using Resesh.App.Interop;

namespace Resesh.App;

internal static class Program
{
    private static readonly object ActivationGate = new();
    private static App? _activationTarget;
    private static readonly List<IReadOnlyList<string>> PendingActivations = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (Profile.Error is { } error)
        {
            // Never fall back to the real data when a scratch folder was asked for.
            MessageBox(IntPtr.Zero, error, "resesh", 0x10 /* MB_ICONERROR */);
            return 2;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        TaskbarIntegration.SetProcessIdentity();
        var key = ActivationKey();
        CommandCompletionNotifications.TraceHook = message => MainWindow.Trace(message);
        CommandCompletionNotifications.SetOwner(key);
        // A notification launch must register before GetActivatedEventArgs; any other
        // launch registers after its first window is up (App.OnLaunched).
        if (CommandCompletionNotifications.IsActivationLaunch(args))
            CommandCompletionNotifications.Register();
        try
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activationArgs.Kind == ExtendedActivationKind.AppNotification)
            {
                // A notification can outlive its originating process. Only redirect to
                // an existing instance; do not restore sessions or replay launch options.
                var owner = CommandCompletionNotifications.ActivationOwner(activationArgs);
                var existing = owner is null ? null : AppInstance.GetInstances()
                    .FirstOrDefault(instance => !instance.IsCurrent && instance.Key == owner);
                if (existing is not null)
                    Task.Run(async () => await existing.RedirectActivationToAsync(activationArgs)).GetAwaiter().GetResult();
                return 0;
            }

            if (key is not null)
            {
                var registered = AppInstance.FindOrRegisterForKey(key);
                if (!registered.IsCurrent)
                {
                    // Let the running instance bring its window forward for this launch.
                    AllowSetForegroundWindow(registered.ProcessId);
                    Task.Run(async () => await registered.RedirectActivationToAsync(activationArgs))
                        .GetAwaiter()
                        .GetResult();
                    return 0;
                }
                registered.Activated += OnActivated;
            }

            App.StartLoadingStores();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App();
            });
            return 0;
        }
        finally { CommandCompletionNotifications.Unregister(); }
    }

    /// <summary>This run's data set, parsed once from the command line.</summary>
    internal static DataProfile Profile { get; } = DataProfile.Parse(Environment.GetCommandLineArgs());

    /// <summary>The folder this run keeps its data in: the demo scratch folder, the
    /// --data-dir folder, or %APPDATA%\Resesh. The stores, custom icons, backups and the
    /// single-instance key all derive from this one answer, so they cannot disagree.</summary>
    internal static string DataDirectory => DataDirectoryOverride ?? AppDataPaths.Roaming();

    /// <summary>A file in the data folder, or <paramref name="defaultPath"/> (which may live
    /// elsewhere, like command history in local app data) when no folder was chosen.</summary>
    internal static string StorePath(string fileName, string defaultPath) =>
        DataDirectoryOverride is { } directory ? Path.Combine(directory, fileName) : defaultPath;

    /// <summary>Where crash.log and trace.log go: %LOCALAPPDATA%\Resesh for the real profile,
    /// the data folder otherwise, so a scratch or demo run leaves the real logs alone.</summary>
    internal static string LogDirectory => DataDirectoryOverride ?? AppDataPaths.Local();

    private static string? DataDirectoryOverride =>
        Profile.IsDemo ? DemoMode.DataFolder : Profile.DataDirectory;

    /// <summary>False in demo mode or with --data-dir: a jump list item or a sign-in launch
    /// starts without those options, so it would open a different data folder.</summary>
    internal static bool UsesDefaultDataDirectory => Profile.IsDefault;

    internal static string RelaunchCommand() => Profile.RelaunchCommand(Environment.ProcessPath!);

    private static string? ActivationKey()
    {
        if (Profile.IsDemo)
            return null;

        var normalized = Path.GetFullPath(DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"Resesh-{Convert.ToHexString(hash)}";
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        if (CommandCompletionNotifications.HandleActivation(args)) return;
        var commandLine = args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                ? SplitCommandLine(launch.Arguments)
                : [];
        App? target;
        lock (ActivationGate)
        {
            target = _activationTarget;
            if (target is null)
            {
                PendingActivations.Add(commandLine);
                return;
            }
        }

        target.HandleRedirectedActivation(commandLine);
    }

    internal static void SetActivationTarget(App app)
    {
        List<IReadOnlyList<string>> pending;
        lock (ActivationGate)
        {
            _activationTarget = app;
            pending = [.. PendingActivations];
            PendingActivations.Clear();
        }

        foreach (var commandLine in pending)
            app.HandleRedirectedActivation(commandLine);
    }

    /// <summary>Splits a command line the way the C runtime does for Main's args.</summary>
    private static string[] SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
            return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
