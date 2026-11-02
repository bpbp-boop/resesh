using Renci.SshNet;
using Renci.SshNet.Common;
using Resesh.Core.Models;

namespace Resesh.Core.Ssh;

public enum TunnelState
{
    Listening,
    Failed,
    Stopped,
}

/// <summary>
/// A tunnel on a live connection. <see cref="Error"/> is why it failed to start, or, while
/// it is listening, the most recent connection it could not carry (destination refused).
/// </summary>
public sealed record TunnelStatus(Tunnel Tunnel, TunnelState State, string? Error = null);

/// <summary>
/// The port forwards running on one SSH connection. They end with it.
/// <para>
/// Starting a forward blocks outside the lock (a remote forward waits for the server), so
/// an entry has a Starting phase the public <see cref="TunnelState"/> does not show: it
/// reads as Stopped until the start settles. A Stop, Remove or Dispose that arrives while
/// it starts only marks it; the starting thread, which alone holds the new port until then,
/// checks the mark under the lock when it finishes and closes the port itself.
/// </para>
/// </summary>
internal sealed class SshTunnelSet(SshClient client, Action<TunnelStatus> changed) : IDisposable
{
    internal const string AlreadyStarting = "This tunnel is already starting.";
    internal const string NotConnected = "The session is not connected.";

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private bool _disposed;

    /// <summary>Test hook: runs on the starting thread just before the port starts listening,
    /// where a real start blocks outside the lock.</summary>
    internal Action<Tunnel>? BeforeListen { get; init; }

    private sealed class Entry(Tunnel tunnel)
    {
        public Tunnel Tunnel { get; } = tunnel;
        /// <summary>Written only under the gate, and only once a start has settled.</summary>
        public ForwardedPort? Port { get; set; }
        public TunnelStatus Status { get; set; } = new(tunnel, TunnelState.Stopped);
        public bool Starting { get; set; }
        /// <summary>Stop, Remove or Dispose arrived while starting.</summary>
        public bool StopRequested { get; set; }
    }

    public IReadOnlyList<TunnelStatus> Snapshot()
    {
        lock (_gate) return _entries.Select(entry => entry.Status).ToList();
    }

    public TunnelStatus Start(Tunnel tunnel)
    {
        Entry entry;
        ForwardedPort? replaced;
        lock (_gate)
        {
            if (_disposed)
                return new TunnelStatus(tunnel, TunnelState.Failed, NotConnected);
            // Two starts of one tunnel would both create a listener for it.
            if (Find(tunnel.Id) is { Starting: true })
                return new TunnelStatus(tunnel, TunnelState.Failed, AlreadyStarting);
            // Starting a tunnel again (after a failure, or an edit) replaces its old entry.
            (_, replaced) = StopLocked(tunnel.Id, remove: true);
            entry = new Entry(tunnel) { Starting = true };
            _entries.Add(entry);
        }
        // Before the new one binds: an edit may keep the same local port.
        Release(replaced);

        ForwardedPort? port = null;
        string? error = null;
        try
        {
            if (tunnel.Validate() is { } invalid)
                throw new ArgumentException(invalid);
            port = Create(tunnel);
            port.Exception += (_, e) => Report(entry, e.Exception.Message);
            client.AddForwardedPort(port);
            BeforeListen?.Invoke(tunnel);
            port.Start();
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or ArgumentException
            or InvalidOperationException or ObjectDisposedException or IOException)
        {
            error = Explain(tunnel, ex);
        }

        TunnelStatus status;
        bool silent;
        lock (_gate)
        {
            entry.Starting = false;
            silent = _disposed;
            if (error is null && !entry.StopRequested && !_disposed)
            {
                entry.Port = port;
                port = null; // the entry owns it now
                status = entry.Status = new TunnelStatus(tunnel, TunnelState.Listening);
            }
            else
            {
                // Stopped meanwhile: it stays Stopped (or gone, if removed), whether or not
                // the start itself worked.
                status = entry.StopRequested || _disposed
                    ? new TunnelStatus(tunnel, TunnelState.Stopped)
                    : new TunnelStatus(tunnel, TunnelState.Failed, error);
                entry.Status = status;
            }
        }
        Release(port);
        if (!silent)
            changed(status);
        return status;
    }

    public void Stop(Guid tunnelId)
    {
        TunnelStatus? status;
        ForwardedPort? port;
        lock (_gate) (status, port) = StopLocked(tunnelId, remove: false);
        Release(port);
        if (status is not null)
            changed(status);
    }

    /// <summary>Stops a tunnel and drops it from the list.</summary>
    public void Remove(Guid tunnelId)
    {
        TunnelStatus? status;
        ForwardedPort? port;
        lock (_gate) (status, port) = StopLocked(tunnelId, remove: true);
        Release(port);
        if (status is not null)
            changed(status);
    }

    private Entry? Find(Guid tunnelId) => _entries.FirstOrDefault(candidate => candidate.Tunnel.Id == tunnelId);

    /// <summary>Detaches the tunnel's port for the caller to release outside the lock. A
    /// starting tunnel is only marked: its start reports the stop when it settles.</summary>
    private (TunnelStatus? Status, ForwardedPort? Port) StopLocked(Guid tunnelId, bool remove)
    {
        var entry = Find(tunnelId);
        if (entry is null)
            return (null, null);
        if (remove)
            _entries.Remove(entry);
        if (entry.Starting)
        {
            entry.StopRequested = true;
            return (null, null);
        }
        var port = entry.Port;
        entry.Port = null;
        if (entry.Status.State == TunnelState.Stopped)
            return (null, port);
        entry.Status = new TunnelStatus(entry.Tunnel, TunnelState.Stopped);
        return (entry.Status, port);
    }

    private static ForwardedPort Create(Tunnel tunnel) => tunnel.Kind switch
    {
        TunnelKind.Remote => new ForwardedPortRemote(
            tunnel.BindAddress.Trim(), (uint)tunnel.BindPort, tunnel.DestinationHost.Trim(), (uint)tunnel.DestinationPort),
        TunnelKind.Dynamic => new ForwardedPortDynamic(tunnel.BindAddress.Trim(), (uint)tunnel.BindPort),
        _ => new ForwardedPortLocal(
            tunnel.BindAddress.Trim(), (uint)tunnel.BindPort, tunnel.DestinationHost.Trim(), (uint)tunnel.DestinationPort),
    };

    private static string Explain(Tunnel tunnel, Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse } =>
            $"Port {tunnel.BindPort} is already in use on this computer.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied } =>
            $"Windows does not allow listening on port {tunnel.BindPort}.",
        SshException when tunnel.Kind == TunnelKind.Remote =>
            $"The server refused to listen on {tunnel.BindAddress}:{tunnel.BindPort}: {ex.Message}",
        _ => ex.Message,
    };

    // A forward that is listening can still fail one connection at a time.
    private void Report(Entry entry, string error)
    {
        TunnelStatus status;
        lock (_gate)
        {
            if (_disposed || entry.Status.State != TunnelState.Listening || entry.Status.Error == error)
                return;
            status = entry.Status = entry.Status with { Error = error };
        }
        changed(status);
    }

    private void Release(ForwardedPort? port)
    {
        if (port is null)
            return;
        try
        {
            if (port.IsStarted)
                port.Stop();
        }
        catch (Exception e) when (e is SshException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException) { }
        try { client.RemoveForwardedPort(port); }
        catch (Exception e) when (e is SshException or ObjectDisposedException or InvalidOperationException) { }
        try { port.Dispose(); } catch (Exception e) when (e is SshException or ObjectDisposedException) { }
    }

    /// <summary>Closes every listener. Silent: the connection is going away with them. A
    /// tunnel still starting closes its own port when its start settles.</summary>
    public void Dispose()
    {
        List<ForwardedPort> ports = [];
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries)
            {
                if (entry.Port is { } port)
                    ports.Add(port);
                entry.Port = null;
            }
        }
        foreach (var port in ports)
            Release(port);
    }
}
