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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        TaskbarIntegration.SetProcessIdentity();
        var key = ActivationKey();
        CommandCompletionNotifications.TraceHook = message => MainWindow.Trace(message);
        CommandCompletionNotifications.Register(key);
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

    internal static string StorePath(string fileName, string defaultPath)
    {
        if (DemoMode.IsEnabled)
            return DemoMode.StorePath(fileName);

        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
                return Path.Combine(Path.GetFullPath(args[i + 1]), fileName);
        }
        return defaultPath;
    }

    /// <summary>False in demo mode or with --data-dir: a jump list item or a sign-in launch
    /// starts without those options, so it would open a different data folder.</summary>
    internal static bool UsesDefaultDataDirectory =>
        !DemoMode.IsEnabled && !Environment.GetCommandLineArgs().Contains("--data-dir");

    internal static string RelaunchCommand()
    {
        var parts = new List<string> { QuoteCommandLineArgument(Environment.ProcessPath!) };
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--demo")
            {
                parts.Add("--demo");
            }
            else if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                parts.Add("--data-dir");
                parts.Add(QuoteCommandLineArgument(args[++i]));
            }
        }
        return string.Join(' ', parts);
    }

    private static string QuoteCommandLineArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            result.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }

    private static string? ActivationKey()
    {
        if (DemoMode.IsEnabled)
            return null;

        var dataDirectory = Path.GetDirectoryName(StorePath("sessions.json", SessionStore.DefaultPath))!;
        var normalized = Path.GetFullPath(dataDirectory)
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
}
