using Microsoft.UI.Dispatching;

namespace Resesh.Terminal;

/// <summary>
/// Queue work and timer ticks on a <see cref="DispatcherQueue"/> so an exception they throw is
/// reported before the process ends. WinUI fails fast when a DispatcherQueue callback or a
/// DispatcherQueueTimer tick throws, without raising Application.UnhandledException or
/// AppDomain.UnhandledException (microsoft-ui-xaml#8940): the crash showed only as a stowed
/// exception in CoreMessagingXP.dll and never reached crash.log. Use these instead of
/// TryEnqueue and Tick, which the BannedApiAnalyzers forbid.
/// </summary>
public static class DispatcherQueueReporting
{
    /// <summary>Receives the exception, with its stack still intact, before WinUI fails fast as
    /// it did before. Set once at startup (App writes crash.log).</summary>
    public static Action<Exception>? UnhandledException { get; set; }

    /// <summary><see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/>, reporting what
    /// <paramref name="callback"/> throws.</summary>
    public static bool TryPost(this DispatcherQueue queue, Action callback) =>
#pragma warning disable RS0030 // the one place the raw API is wrapped
        queue.TryEnqueue(() => Run(callback));
#pragma warning restore RS0030

    /// <summary><see cref="DispatcherQueue.TryEnqueue(DispatcherQueuePriority, DispatcherQueueHandler)"/>,
    /// reporting what <paramref name="callback"/> throws.</summary>
    public static bool TryPost(this DispatcherQueue queue, DispatcherQueuePriority priority, Action callback) =>
#pragma warning disable RS0030 // the one place the raw API is wrapped
        queue.TryEnqueue(priority, () => Run(callback));
#pragma warning restore RS0030

    /// <summary>Runs <paramref name="tick"/> on every tick of <paramref name="timer"/>, reporting
    /// what it throws.</summary>
    public static void OnTick(this DispatcherQueueTimer timer, Action tick)
    {
#pragma warning disable RS0030 // the one place the raw API is wrapped
        timer.Tick += (_, _) => Run(tick);
#pragma warning restore RS0030
    }

    private static void Run(Action callback)
    {
        try
        {
            callback();
        }
        // The filter runs before the stack unwinds, so the report has the throwing frames, and
        // never catches: the exception goes on to fail fast exactly as it did unwrapped.
        catch (Exception exception) when (Report(exception))
        {
        }
    }

    private static bool Report(Exception exception)
    {
        try
        {
            UnhandledException?.Invoke(exception);
        }
        catch
        {
            // Reporting must never replace the exception being reported.
        }
        return false;
    }
}
