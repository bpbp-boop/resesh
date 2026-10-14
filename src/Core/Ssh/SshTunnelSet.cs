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

/// <summary>The port forwards running on one SSH connection. They end with it.</summary>
internal sealed class SshTunnelSet(SshClient client, Action<TunnelStatus> changed) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private bool _disposed;

    private sealed class Entry(Tunnel tunnel)
    {
        public Tunnel Tunnel { get; } = tunnel;
        public ForwardedPort? Port { get; set; }
        public TunnelStatus Status { get; set; } = new(tunnel, TunnelState.Stopped);
    }

    public IReadOnlyList<TunnelStatus> Snapshot()
    {
        lock (_gate) return _entries.Select(entry => entry.Status).ToList();
    }

    public TunnelStatus Start(Tunnel tunnel)
    {
        Entry entry;
        lock (_gate)
        {
            if (_disposed)
                return new TunnelStatus(tunnel, TunnelState.Failed, "The session is not connected.");
            // Starting a tunnel again (after a failure, or an edit) replaces its old entry.
            StopLocked(tunnel.Id, remove: true);
            entry = new Entry(tunnel);
            _entries.Add(entry);
        }

        TunnelStatus status;
        try
        {
            if (tunnel.Validate() is { } invalid)
                throw new ArgumentException(invalid);
            var port = Create(tunnel);
            port.Exception += (_, e) => Report(entry, e.Exception.Message);
            client.AddForwardedPort(port);
            entry.Port = port;
            port.Start();
            status = new TunnelStatus(tunnel, TunnelState.Listening);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or ArgumentException
            or InvalidOperationException or ObjectDisposedException or IOException)
        {
            Release(entry);
            status = new TunnelStatus(tunnel, TunnelState.Failed, Explain(tunnel, ex));
        }
        lock (_gate) entry.Status = status;
        changed(status);
        return status;
    }

    public void Stop(Guid tunnelId)
    {
        TunnelStatus? status;
        lock (_gate) status = StopLocked(tunnelId, remove: false);
        if (status is not null)
            changed(status);
    }

    /// <summary>Stops a tunnel and drops it from the list.</summary>
    public void Remove(Guid tunnelId)
    {
        TunnelStatus? status;
        lock (_gate) status = StopLocked(tunnelId, remove: true);
        if (status is not null)
            changed(status);
    }

    private TunnelStatus? StopLocked(Guid tunnelId, bool remove)
    {
        var entry = _entries.FirstOrDefault(candidate => candidate.Tunnel.Id == tunnelId);
        if (entry is null)
            return null;
        Release(entry);
        if (remove)
            _entries.Remove(entry);
        if (entry.Status.State == TunnelState.Stopped)
            return null;
        entry.Status = new TunnelStatus(entry.Tunnel, TunnelState.Stopped);
        return entry.Status;
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

    private void Release(Entry entry)
    {
        var port = entry.Port;
        entry.Port = null;
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

    /// <summary>Closes every listener. Silent: the connection is going away with them.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries)
                Release(entry);
        }
    }
}
