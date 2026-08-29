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
    private static int _pendingActivations;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        TaskbarIntegration.SetProcessIdentity();

        if (ActivationKey() is { } key)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var registered = AppInstance.FindOrRegisterForKey(key);
            if (!registered.IsCurrent)
            {
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
        App? target;
        lock (ActivationGate)
        {
            target = _activationTarget;
            if (target is null)
            {
                _pendingActivations++;
                return;
            }
        }

        target.HandleRedirectedActivation();
    }

    internal static void SetActivationTarget(App app)
    {
        int pending;
        lock (ActivationGate)
        {
            _activationTarget = app;
            pending = _pendingActivations;
            _pendingActivations = 0;
        }

        for (var i = 0; i < pending; i++)
            app.HandleRedirectedActivation();
    }
}
