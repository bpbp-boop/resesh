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
    private bool _disposed;

    private SshJumpChain()
    {
    }

    /// <summary>Always loopback: the forwarded port is never offered to the network.</summary>
    public string Host => Tunnel.Loopback;

    public int Port { get; private set; }

    /// <summary>Why the last jump could not open the forwarded connection, if it could not
    /// (for example the target refused, or the server forbids forwarding).</summary>
    public string? ForwardFailure => _forwardFailure?.Message;

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
                    ? (jumps[index + 1].Session.Host, jumps[index + 1].Session.Port)
                    : (targetHost, targetPort);
                try
                {
                    var client = chain.ConnectHop(hop, index == 0, knownHosts, hostKeyDecision, interactiveResponder, cancellationToken);
                    // Port 0: the system picks a free loopback port, read back after Start.
                    var forward = new ForwardedPortLocal(Tunnel.Loopback, 0, nextHost, (uint)nextPort);
                    chain._links.Add((client, forward));
                    forward.Exception += (_, e) => chain._forwardFailure = e.Exception;
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
            session, auth, TimeSpan.FromMinutes(2), via, hop.ProxyPassword);
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
                $"{session.Host}:{session.Port} did not answer through the jump host within {timeout.TotalSeconds:0} seconds.");
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
            $"The jump host could not reach {session.Host}:{session.Port}"
            + (ForwardFailure is { } reason ? $": {reason}" : "."));
    }

    /// <summary>Closes the jumps, last first, which ends anything connected through them.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
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
