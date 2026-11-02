namespace Resesh.Core.Backend;

internal enum BackendLifecycleState
{
    /// <summary>Created; Connect/Start has not been called.</summary>
    NotStarted,

    /// <summary>The connect thread is building the connection. Nothing it builds is live yet.</summary>
    Connecting,

    /// <summary>Connected; the remote side may still end it.</summary>
    Open,

    /// <summary>The remote side ended it and Closed was raised. A local stop still tears down.</summary>
    RemoteClosed,

    /// <summary>A local stop began (or a connect failed). Final.</summary>
    Stopped,
}

/// <summary>
/// The lifecycle every connection backend shares, as one atomically changed state. It
/// exists because each backend used to keep its own mix of flags (disposed, closed raised,
/// stopped), and the gaps between them lost races: a stop during connect tore down nothing
/// and let the connect publish a live connection nobody owned, and a remote close racing
/// a local stop could raise Closed after the stop.
/// <list type="bullet">
/// <item>One connect per instance: <see cref="TryBeginConnect"/> succeeds once.</item>
/// <item>A connect only goes live through <see cref="TryPublishOpen"/>, which fails if a stop
/// arrived meanwhile; the connect thread then tears down what it built.</item>
/// <item>A remote close and a local stop race through one compare-and-swap: exactly one
/// wins. <see cref="TryRaiseRemoteClose"/> runs its callback under the same lock
/// <see cref="BeginStop()"/> takes, so once BeginStop returns no Closed is running or still
/// to come (a handler that stops from inside Closed re-enters the lock on its own thread).</item>
/// </list>
/// </summary>
internal sealed class BackendLifecycle
{
    private readonly object _closeGate = new();
    private int _state;

    public BackendLifecycleState State => (BackendLifecycleState)Volatile.Read(ref _state);

    public bool IsOpen => State == BackendLifecycleState.Open;

    /// <summary>A local stop began, or the connect failed.</summary>
    public bool IsStopped => State == BackendLifecycleState.Stopped;

    /// <summary>NotStarted → Connecting. False when the instance was already used or stopped.</summary>
    public bool TryBeginConnect() => Transition(BackendLifecycleState.NotStarted, BackendLifecycleState.Connecting);

    /// <summary>Connecting → Open. False when a stop arrived during the connect: the caller
    /// owns everything it built and must tear it down.</summary>
    public bool TryPublishOpen() => Transition(BackendLifecycleState.Connecting, BackendLifecycleState.Open);

    /// <summary>
    /// Open → RemoteClosed, then <paramref name="raise"/> under the stop lock. Only the first
    /// detector of an open connection wins; never after a local stop, and never while still
    /// connecting (the connect thread reports those failures by throwing).
    /// </summary>
    public bool TryRaiseRemoteClose(Action raise)
    {
        lock (_closeGate)
        {
            if (!Transition(BackendLifecycleState.Open, BackendLifecycleState.RemoteClosed))
                return false;
            raise();
            return true;
        }
    }

    /// <summary>Any state → Stopped. True for the one call that stopped it.</summary>
    public bool BeginStop() => BeginStop(out _);

    /// <summary>
    /// Any state → Stopped. True for the one call that stopped it; <paramref name="previous"/>
    /// says what was interrupted, so a stop of a Connecting backend can leave the teardown to
    /// the connect thread when it cannot interrupt the connect safely.
    /// </summary>
    public bool BeginStop(out BackendLifecycleState previous)
    {
        lock (_closeGate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _state);
                previous = (BackendLifecycleState)current;
                if (previous == BackendLifecycleState.Stopped)
                    return false;
                if (Interlocked.CompareExchange(ref _state, (int)BackendLifecycleState.Stopped, current) == current)
                    return true;
            }
        }
    }

    /// <summary>The exception for a connect that cannot go ahead: cancelled when a stop got
    /// there first (callers already treat that as quiet), otherwise a reuse bug.</summary>
    public Exception CannotConnect() => IsStopped
        ? new OperationCanceledException("The connection was closed before it opened.")
        : new InvalidOperationException("Session already used; create a new instance per connection.");

    private bool Transition(BackendLifecycleState from, BackendLifecycleState to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
}
