using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Resesh.Core.Agents;
using Resesh.Core.Backend;

namespace Resesh.App.Interop;

/// <summary>Optional OS notifications. Activation can only invoke a live, in-memory target.</summary>
internal static class CommandCompletionNotifications
{
    private const int MaximumTargets = 128;
    private static readonly TimeSpan TargetLifetime = TimeSpan.FromHours(24);
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, Target> Targets = [];
    private sealed record Target(Action Activate, DateTimeOffset Expires);
    private static DispatcherQueue? _dispatcher;
    private static AppNotificationManager? _manager;
    private static string? _owner;
    internal static Action<string>? TraceHook { get; set; }

    // Must precede AppInstance.GetActivatedEventArgs for unpackaged COM activation.
    internal static void Register(string? owner)
    {
        _owner = owner;
        if (owner is null) return; // demo mode has no persistent instance identity
        try
        {
            var manager = AppNotificationManager.Default;
            TraceStatus("manager acquired");
            manager.NotificationInvoked += OnNotificationInvoked;
            try { manager.Register(); }
            catch { manager.NotificationInvoked -= OnNotificationInvoked; throw; }
            _manager = manager;
            TraceStatus($"registered; setting={manager.Setting}; supported={AppNotificationManager.IsSupported()}");
        }
        catch (Exception error) { Trace("register", error); }
    }

    internal static void SetDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>Call on the UI thread. Only the session label and program name leave the app.</summary>
    internal static bool TryShow(string sessionName, string programName, int? exitCode,
        TimeSpan duration, Action activate)
    {
        var manager = _manager;
        if (manager is null || _owner is null || _dispatcher?.HasThreadAccess != true)
        {
            TraceStatus($"show unavailable; registered={manager is not null}; owner={_owner is not null}; uiThread={_dispatcher?.HasThreadAccess == true}");
            return false;
        }
        var token = Guid.NewGuid();
        try
        {
            if (manager.Setting != AppNotificationSetting.Enabled)
            {
                TraceStatus($"show disabled; setting={manager.Setting}");
                return false;
            }
            var session = AgentText.Sanitize(sessionName, 80) ?? "Session";
            var program = AgentText.Sanitize(CommandTitle.ProgramName(programName), 64) ?? "Command";
            var status = exitCode is null ? "finished" : exitCode == 0 ? "completed" : $"failed (exit {exitCode.Value})";
            var seconds = Math.Clamp(duration.TotalSeconds, 0, 365 * 24 * 60 * 60);
            var elapsed = seconds < 60 ? $"{Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture)}s"
                : seconds < 3600 ? $"{Math.Floor(seconds / 60).ToString(CultureInfo.InvariantCulture)}m {Math.Floor(seconds % 60).ToString(CultureInfo.InvariantCulture)}s"
                : $"{Math.Floor(seconds / 3600).ToString(CultureInfo.InvariantCulture)}h {Math.Floor(seconds % 3600 / 60).ToString(CultureInfo.InvariantCulture)}m";
            var expires = DateTimeOffset.UtcNow + TargetLifetime;
            var notification = new AppNotificationBuilder()
                .AddArgument("completion", token.ToString("N"))
                .AddArgument("owner", _owner)
                .AddText(session)
                .AddText($"{program} {status} after {elapsed}")
                .MuteAudio()
                .BuildNotification();
            notification.Expiration = expires;
            lock (Gate)
            {
                RemoveExpired();
                while (Targets.Count >= MaximumTargets) Targets.Remove(Targets.First().Key);
                Targets.Add(token, new Target(activate, expires));
            }
            manager.Show(notification);
            TraceStatus($"show result; id={notification.Id}");
            if (notification.Id != 0) return true;
        }
        catch (Exception error) { Trace("show", error); }
        lock (Gate) Targets.Remove(token);
        return false;
    }

    internal static string? ActivationOwner(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.AppNotification || args.Data is not AppNotificationActivatedEventArgs notification)
            return null;
        return TryParseActivation(notification.Argument, out _, out var owner) ? owner : null;
    }

    // Return true for every notification activation, even stale/invalid tokens: never
    // convert a dismissed/old notification into a new-window or session launch request.
    internal static bool HandleActivation(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.AppNotification) return false;
        if (args.Data is AppNotificationActivatedEventArgs notification)
            Activate(notification.Argument);
        return true;
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Activate(args.Argument);

    private static void Activate(string argument)
    {
        try
        {
            if (!TryParseActivation(argument, out var token, out var owner))
            {
                TraceStatus("activation rejected; invalid arguments");
                return;
            }
            if (owner != _owner)
            {
                TraceStatus("activation rejected; owner mismatch");
                return;
            }
            Target? target;
            lock (Gate)
            {
                RemoveExpired();
                if (!Targets.Remove(token, out target))
                {
                    TraceStatus("activation ignored; target missing or expired");
                    return;
                }
            }
            var queued = _dispatcher?.TryEnqueue(() =>
            {
                try { target.Activate(); }
                catch (Exception error) { Trace("activate", error); }
            });
            TraceStatus($"activation queued={queued == true}");
        }
        catch (Exception error) { Trace("activation arguments", error); }
    }

    internal static bool TryParseActivation(string argument, out Guid token, out string? owner)
    {
        token = default;
        owner = null;
        var hasToken = false;
        if (argument.Length > 256) return false;
        foreach (var field in argument.Split(';'))
        {
            var pair = field.Split('=', 2);
            if (pair.Length != 2) return false;
            if (pair[0] == "completion")
            {
                if (hasToken || !Guid.TryParseExact(pair[1], "N", out token)) return false;
                hasToken = true;
            }
            else if (pair[0] == "owner")
            {
                if (owner is not null || pair[1].Length != 71 || !pair[1].StartsWith("Resesh-", StringComparison.Ordinal)
                    || pair[1][7..].Any(c => !Uri.IsHexDigit(c))) return false;
                owner = pair[1];
            }
            else return false;
        }
        return token != default && owner is not null;
    }

    private static void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in Targets.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            Targets.Remove(key);
    }

    internal static void Unregister()
    {
        lock (Gate) Targets.Clear();
        _dispatcher = null;
        var manager = _manager;
        _manager = null;
        if (manager is null) return;
        try { manager.NotificationInvoked -= OnNotificationInvoked; manager.Unregister(); }
        catch (Exception error) { Trace("unregister", error); }
    }

    private static void Trace(string operation, Exception error) =>
        TraceStatus($"{operation}: {error.GetType().Name} (0x{error.HResult:X8})");

    private static void TraceStatus(string message)
    {
        var text = "Command completion notification " + message;
        Debug.WriteLine(text);
        try { TraceHook?.Invoke(text); }
        catch { } // Diagnostics must not affect command completion or app startup.
    }
}
