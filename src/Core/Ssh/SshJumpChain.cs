using Renci.SshNet;
using Renci.SshNet.Common;
using Resesh.Core.Models;
using Session = Resesh.Core.Models.Session;

namespace Resesh.Core.Ssh;

/// <summary>One jump host on the way to a connection's target: the session to connect as
/// (login already applied) and its secrets.</summary>
public sealed record SshHop(Session Session, string? Secret, string? ProxyPassword = null);

/// <summary>
/// The live jump connections a target is reached through. Each jump forwards a loopback
/// port chosen by the system to the next host, so the next connection (SSH, SFTP or
/// telnet) dials <see cref="Host"/>:<see cref="Port"/> as if it were the real one.
/// Owned by the connection that opened it and closed with it. Blocking.
/// </summary>
public sealed class SshJumpChain : IDisposable
{
    private readonly List<(SshClient Client, ForwardedPortLocal Forward)> _links = [];
    private volatile Exception? _forwardFailure;
    // The last link's forward: only its failures explain a connection to the target.
    private volatile ForwardedPortLocal? _lastForward;
    private int _disposed;
    private int _lost;
    private Timer? _watchdog;
    private readonly Lock _lostGate = new();
    private Action<string>? _lostHandlers;
    private string? _lostReason;

    private SshJumpChain()
    {
    }

    /// <summary>
    /// Raised once, on a background thread, when a jump's own SSH connection fails or drops
    /// after the chain opened; the argument says which jump and why. What runs through the
    /// chain does not always hear of it: the forwarded loopback socket stays up when a jump
    /// dies without closing (SSH.NET does not always notice either, so the links are also
    /// polled), and TCP keepalive on that socket only ever reaches this machine. Never raised
    /// after <see cref="Dispose"/>.
    /// A jump can drop while <see cref="Open"/> is still returning, before the connection
    /// that runs through the chain subscribes, so a handler added after the loss is called at
    /// once, on the adding thread, with the same reason.
    /// </summary>
    public event Action<string>? Lost
    {
        add
        {
            if (value is null)
                return;
            string? reason;
            lock (_lostGate)
            {
                reason = _lostReason;
                if (reason is null)
                {
                    _lostHandlers += value;
                    return;
                }
            }
            if (Volatile.Read(ref _disposed) == 0)
                value(reason);
        }
        remove
        {
            lock (_lostGate)
                _lostHandlers -= value;
        }
    }

    private void RaiseLost(Session jump, Exception? failure)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _lost, 1) != 0)
            return;
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
        var reason = failure is null
            ? $"The connection to the jump host {jump.Name} was lost."
            : $"The connection to the jump host {jump.Name} was lost: {failure.Message}";
        Action<string>? handlers;
        lock (_lostGate)
        {
            _lostReason = reason;
            handlers = _lostHandlers;
        }
        handlers?.Invoke(reason);
    }

    /// <summary>Starts watching every link once the chain is complete.</summary>
    private void Watch(IReadOnlyList<Session> jumps)
    {
        for (var index = 0; index < _links.Count; index++)
        {
            var jump = jumps[index];
            _links[index].Client.ErrorOccurred += (_, e) => RaiseLost(jump, e.Exception);
        }
        var links = _links.Select(link => link.Client).ToList();
        _watchdog = new Timer(_ =>
        {
            try
            {
                for (var index = 0; index < links.Count && Volatile.Read(ref _disposed) == 0; index++)
                    if (!links[index].IsConnected)
                    {
                        RaiseLost(jumps[index], null);
                        return;
                    }
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the check and the poll: nothing to report.
            }
        }, null, dueTime: 5000, period: 5000);
        // A Dispose that ran while this was being set up could not see the timer.
        if (Volatile.Read(ref _disposed) != 0)
            Interlocked.Exchange(ref _watchdog, null)?.Dispose();
    }

    /// <summary>Always loopback: the forwarded port is never offered to the network.</summary>
    public string Host => Tunnel.Loopback;

    public int Port { get; private set; }

    /// <summary>Why the last jump could not open the forwarded connection in the current
    /// attempt, if it could not (for example the target refused, or the server forbids
    /// forwarding). Each attempt starts with <see cref="Probe"/>, which clears it, so a
    /// failure from an earlier attempt is never reported as this one's cause.</summary>
    public string? ForwardFailure => _forwardFailure?.Message;

    /// <summary>A forward reported a failure. Only the last link's count: an earlier link's
    /// forward carries the next jump's SSH session, not connections to the target.</summary>
    internal void RecordForwardFailure(Exception failure, ForwardedPortLocal? from = null)
    {
        if (from is null || ReferenceEquals(from, _lastForward))
            _forwardFailure = failure;
    }

    /// <summary>Connects every jump in order and opens the forward to
    /// <paramref name="targetHost"/>. A null <paramref name="hostKeyDecision"/> accepts
    /// only host keys that are already trusted.</summary>
    public static SshJumpChain Open(
        IReadOnlyList<SshHop> jumps, string targetHost, int targetPort,
        KnownHostsStore knownHosts,
        Func<HostKeyInfo, bool>? hostKeyDecision = null,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder = null,
        CancellationToken cancellationToken = default,
        Action<Session>? connecting = null,
        Func<SshHop, SshHop?>? resolveLogin = null)
    {
        if (jumps.Count == 0)
            throw new ArgumentException("A jump chain needs at least one jump host.", nameof(jumps));

        var chain = new SshJumpChain();
        try
        {
            for (var index = 0; index < jumps.Count; index++)
            {
                connecting?.Invoke(jumps[index].Session);
                // Asked for only now, so a jump that fails stops before the next login prompt.
                var hop = ResolveLogin(jumps[index], resolveLogin);
                var (nextHost, nextPort) = index + 1 < jumps.Count
                    ? (jumps[index + 1].Session.Host, SessionEndpoint.EffectivePort(jumps[index + 1].Session))
                    : (targetHost, targetPort);
                try
                {
                    var client = chain.ConnectHop(hop, index == 0, knownHosts, hostKeyDecision, interactiveResponder, cancellationToken);
                    // Port 0: the system picks a free loopback port, read back after Start.
                    var forward = new ForwardedPortLocal(Tunnel.Loopback, 0, nextHost, (uint)nextPort);
                    chain._links.Add((client, forward));
                    forward.Exception += (_, e) => chain.RecordForwardFailure(e.Exception, forward);
                    chain._lastForward = forward;
                    client.AddForwardedPort(forward);
                    forward.Start();
                    chain.Port = (int)forward.BoundPort;
                }
                catch (SshSessionException ex)
                {
                    throw new SshSessionException(ex.Kind, $"Jump host {hop.Session.Name}: {ex.Message}", ex)
                    {
                        JumpSessionId = hop.Session.Id,
                    };
                }
                catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or InvalidOperationException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new SshSessionException(
                        SshFailureKind.Other, $"Jump host {hop.Session.Name}: could not forward to {nextHost}:{nextPort}: {ex.Message}", ex)
                    {
                        JumpSessionId = hop.Session.Id,
                    };
                }
            }
            chain.Watch(jumps.Select(jump => jump.Session).ToList());
            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    /// <summary>Fills in a jump's login when the caller resolves logins hop by hop. A null
    /// answer means the user declined; any other failure is reported against the jump.</summary>
    private static SshHop ResolveLogin(SshHop hop, Func<SshHop, SshHop?>? resolveLogin)
    {
        if (resolveLogin is null)
            return hop;
        try
        {
            return resolveLogin(hop) ?? throw new OperationCanceledException("No credential provided.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SshSessionException)
        {
            throw new SshSessionException(SshFailureKind.Other, $"Jump host {hop.Session.Name}: {ex.Message}", ex)
            {
                JumpSessionId = hop.Session.Id,
            };
        }
    }

    private SshClient ConnectHop(
        SshHop hop, bool first, KnownHostsStore knownHosts,
        Func<HostKeyInfo, bool>? hostKeyDecision,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = hop.Session;
        // Only the first jump is dialled from this machine; later ones go through the
        // forward opened by the jump before them.
        var via = first ? null : this;

        var auth = SshConnectionFactory.BuildAuthMethods(session, hop.Secret, interactiveResponder);
        var connectionInfo = SshConnectionFactory.CreateConnectionInfo(
            session, auth, TimeSpan.FromMinutes(2), via, hop.ProxyPassword, knownHosts);
        var client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var hostKey = SshConnectionFactory.VerifyHostKey(client, session, knownHosts, hostKeyDecision, cancellationToken);
        try
        {
            SshConnectionFactory.Connect(client, session, via, TimeSpan.FromSeconds(10), cancellationToken);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            if (hostKey.Failure is { } hostKeyFailure)
                throw hostKeyFailure;
            throw ex as SshSessionException ?? SshConnectionFactory.Classify(ex, session, via);
        }
        finally
        {
            SshConnectionFactory.DisposeAuthMethods(auth);
        }
    }

    /// <summary>
    /// The fast check a direct connection gets from its TCP preflight. The forwarded port
    /// always accepts, so reachability shows one step later: a host the last jump cannot
    /// reach closes the connection, and an SSH server that is there greets first.
    /// </summary>
    internal void Probe(Session session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A new attempt: whatever an earlier connection through this chain hit is not news.
        _forwardFailure = null;
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        var answered = false;
        try
        {
            socket.Connect(System.Net.IPAddress.Loopback, Port);
            var greeting = new byte[1];
            answered = SshConnectionFactory.WaitObserved(
                socket.ReceiveAsync(greeting, System.Net.Sockets.SocketFlags.None, cancellationToken).AsTask(),
                timeout, cancellationToken) > 0;
        }
        catch (TimeoutException)
        {
            throw new SshSessionException(SshFailureKind.HostUnreachable,
                $"{session.Host}:{SessionEndpoint.EffectivePort(session)} did not answer through the jump host within {timeout.TotalSeconds:0} seconds.");
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
        if (answered)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        // The jump reports why on its own thread, a moment after it closes the connection.
        SpinWait.SpinUntil(() => _forwardFailure is not null, TimeSpan.FromMilliseconds(250));
        throw new SshSessionException(SshFailureKind.HostUnreachable,
            $"The jump host could not reach {session.Host}:{SessionEndpoint.EffectivePort(session)}"
            + (ForwardFailure is { } reason ? $": {reason}" : "."));
    }

    /// <summary>Closes the jumps, last first, which ends anything connected through them.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
        for (var index = _links.Count - 1; index >= 0; index--)
        {
            var (client, forward) = _links[index];
            try
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
            catch (Exception e) when (e is SshException or ObjectDisposedException or IOException or System.Net.Sockets.SocketException) { }
            try { forward.Dispose(); } catch (Exception e) when (e is SshException or ObjectDisposedException) { }
            client.Dispose();
        }
        _links.Clear();
    }
}
