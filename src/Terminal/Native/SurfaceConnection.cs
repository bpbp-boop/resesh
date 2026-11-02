namespace Resesh.Terminal.Native;

internal enum SurfaceLinkState
{
    Connected,
    Disconnected,
    /// <summary>Enter asked the host to reconnect; it has not yet said how that went.</summary>
    Reconnecting,
}

/// <summary>
/// Whether the surface's input reaches a live connection. Only the host's
/// NotifyConnected makes it live: Enter on a disconnected terminal asks for a reconnect but
/// stays not live, because until the host reports back its backend is the dead one (or
/// none), so keys, pastes and focus reports typed meanwhile have nowhere to go. (This was
/// once a bool set true on Enter "optimistically", which sent that input as if connected.)
/// </summary>
internal sealed class SurfaceConnection
{
    public SurfaceLinkState State { get; private set; } = SurfaceLinkState.Connected;

    /// <summary>Keys, pastes and focus reports are encoded and sent only when connected.</summary>
    public bool IsLive => State == SurfaceLinkState.Connected;

    public void Connected() => State = SurfaceLinkState.Connected;

    public void Disconnected() => State = SurfaceLinkState.Disconnected;

    /// <summary>Enter while not live. True: raise the reconnect request. Enter while already
    /// reconnecting asks again, since a host that declined the first request (one already
    /// in flight, a tab that cannot connect yet) does not report back, and the terminal
    /// must not be left waiting with no way to retry; the host ignores a duplicate.</summary>
    public bool RequestReconnect()
    {
        if (State == SurfaceLinkState.Connected)
            return false;
        State = SurfaceLinkState.Reconnecting;
        return true;
    }
}
