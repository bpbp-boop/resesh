using Renci.SshNet;
using Renci.SshNet.Common;
using Session = Resesh.Core.Models.Session;

namespace Resesh.Core.Ssh;

public sealed record KeyboardInteractivePrompt(string Text, bool IsSecret);

/// <summary>
/// Connection plumbing shared by the terminal (SshClient) and the file pane (SftpClient):
/// auth-method construction, failure classification, and the fast TCP preflight.
/// Auth methods are stateful in SSH.NET — build a fresh set per client, never share them.
/// </summary>
internal static class SshConnectionFactory
{
    internal static AuthenticationMethod[] BuildAuthMethods(
        Session session,
        string? secret,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder = null)
    {
        var user = session.Username;
        switch (session.AuthMethod)
        {
            case Models.AuthMethod.Password:
            {
                var password = new PasswordAuthenticationMethod(user, secret ?? "");
                // Some servers demand keyboard-interactive. Every prompt must be answered
                // explicitly; never send the saved password to an arbitrary challenge.
                return [password, Interactive(user, interactiveResponder)];
            }
            case Models.AuthMethod.PrivateKey:
            {
                var keyFile = string.IsNullOrEmpty(secret)
                    ? new PrivateKeyFile(session.PrivateKeyPath!)
                    : new PrivateKeyFile(session.PrivateKeyPath!, secret);
                // Like SecureCRT's publickey-then-keyboard-interactive order: a host that
                // refuses the key can still ask for a password, answered by the user.
                return interactiveResponder is null
                    ? [new PrivateKeyAuthenticationMethod(user, keyFile)]
                    : [new PrivateKeyAuthenticationMethod(user, keyFile), Interactive(user, interactiveResponder)];
            }
            default:
                return [new NoneAuthenticationMethod(user)];
        }
    }

    // Clients built with an external ConnectionInfo do not own the authentication
    // methods. PrivateKeyAuthenticationMethod also does not own its key files.
    internal static void DisposeAuthMethods(AuthenticationMethod[] methods)
    {
        foreach (var method in methods)
        {
            method.Dispose();
            if (method is PrivateKeyAuthenticationMethod privateKey)
            {
                foreach (var keyFile in privateKey.KeyFiles)
                    if (keyFile is IDisposable disposable)
                        disposable.Dispose();
            }
        }
    }

    private static KeyboardInteractiveAuthenticationMethod Interactive(
        string user, Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? responder)
    {
        var interactive = new KeyboardInteractiveAuthenticationMethod(user);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            var requests = e.Prompts
                .Select(prompt => new KeyboardInteractivePrompt(prompt.Request, !prompt.IsEchoed))
                .ToList();
            var responses = responder?.Invoke(requests);
            if (responses is null || responses.Count != e.Prompts.Count)
                return;
            for (var index = 0; index < e.Prompts.Count; index++)
                e.Prompts[index].Response = responses[index];
        };
        return interactive;
    }

    internal static SshSessionException Classify(Exception ex) => ex switch
    {
        SshAuthenticationException => new SshSessionException(
            SshFailureKind.AuthenticationFailed, "Authentication failed — check the username and credential.", ex),
        ProxyException => new SshSessionException(
            SshFailureKind.HostUnreachable, $"The proxy refused the connection: {ex.Message}", ex),
        SshConnectionException or System.Net.Sockets.SocketException => new SshSessionException(
            SshFailureKind.HostUnreachable, $"Could not reach the host: {ex.Message}", ex),
        _ => new SshSessionException(SshFailureKind.Other, ex.Message, ex),
    };

    /// <summary>Like <see cref="Classify(Exception)"/>, but a connection made through a jump
    /// that never got as far as the host reports what the jump said about reaching it.</summary>
    internal static SshSessionException Classify(Exception ex, Session session, SshJumpChain? via)
    {
        if (via is not null && ex is (SshConnectionException or System.Net.Sockets.SocketException))
        {
            var reason = via.ForwardFailure ?? ex.Message;
            return new SshSessionException(
                SshFailureKind.HostUnreachable,
                $"The jump host could not reach {session.Host}:{session.Port}: {reason}", ex);
        }
        return Classify(ex);
    }

    /// <summary>The address SSH.NET dials: the jump chain's forwarded port, the proxy (which
    /// is told the real host), or the host itself.</summary>
    internal static ConnectionInfo CreateConnectionInfo(
        Session session, AuthenticationMethod[] auth, TimeSpan timeout, SshJumpChain? via, string? proxyPassword)
    {
        ConnectionInfo info;
        if (via is not null)
            info = new ConnectionInfo(via.Host, via.Port, session.Username, auth);
        else if (ActiveProxy(session) is { } proxy)
        {
            var type = proxy.Kind switch
            {
                Models.ProxyKind.Socks4 => ProxyTypes.Socks4,
                Models.ProxyKind.Http => ProxyTypes.Http,
                _ => ProxyTypes.Socks5,
            };
            // SSH.NET sends a login to the proxy only when it has a username.
            var proxyUser = string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username.Trim();
            info = new ConnectionInfo(session.Host, session.Port, session.Username, type,
                proxy.Host, proxy.Port, proxyUser, proxyUser is null ? null : proxyPassword ?? "", auth);
        }
        else
            info = new ConnectionInfo(session.Host, session.Port, session.Username, auth);
        info.Timeout = timeout;
        return info;
    }

    /// <summary>The proxy a session connects through, or null when it has none, it is
    /// incomplete, or a jump host reaches the session instead.</summary>
    internal static Models.ProxySettings? ActiveProxy(Session session) =>
        session.JumpHostId is null && session.Proxy is { Host.Length: > 0, Port: > 0 and <= 65535 } proxy ? proxy : null;

    /// <summary>Fast check that the first thing dialled answers, so an unreachable host or
    /// proxy fails in seconds. Behind a jump the check goes through its forwarded port.</summary>
    internal static void Preflight(Session session, SshJumpChain? via, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (via is not null)
        {
            via.Probe(session, timeout + timeout, cancellationToken);
            return;
        }
        if (ActiveProxy(session) is { } proxy)
        {
            try
            {
                PreflightTcp(proxy.Host, proxy.Port, timeout, cancellationToken);
            }
            catch (SshSessionException ex)
            {
                throw new SshSessionException(ex.Kind, $"Proxy: {ex.Message}", ex);
            }
            return;
        }
        PreflightTcp(session.Host, session.Port, timeout, cancellationToken);
    }

    /// <summary>What a host-key check decided, read after the connect attempt.</summary>
    internal sealed class HostKeyCheck
    {
        /// <summary>Set when the key was refused; the connect then fails with this.</summary>
        public SshSessionException? Failure { get; set; }

        /// <summary>"keytype SHA256:fingerprint" of the key the server presented.</summary>
        public string? Fingerprint { get; set; }
    }

    /// <summary>
    /// Checks the server's key against the keys trusted for the session's real host and
    /// port, whatever address was dialled to reach it. An unknown or changed key is put to
    /// <paramref name="decision"/> (deny when there is none) and saved when trusted.
    /// </summary>
    internal static HostKeyCheck VerifyHostKey(
        BaseClient client, Session session, KnownHostsStore knownHosts,
        Func<HostKeyInfo, bool>? decision, CancellationToken cancellationToken)
    {
        var check = new HostKeyCheck();
        client.HostKeyReceived += (_, e) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(e.HostKey)).TrimEnd('=');
            check.Fingerprint = $"{e.HostKeyName} SHA256:{sha256}";
            var verdict = knownHosts.Check(session.Host, session.Port, e.HostKeyName, sha256);
            switch (verdict)
            {
                case HostKeyVerdict.Match:
                    knownHosts.BackfillHostKey(session.Host, session.Port, e.HostKeyName, e.HostKey);
                    e.CanTrust = true;
                    break;
                default:
                    // Unknown key, or a changed key (the dialog demands typed confirmation
                    // for the latter). Default-deny when no handler is wired.
                    var previous = verdict == HostKeyVerdict.Mismatch
                        ? knownHosts.Lookup(session.Host, session.Port)
                        : null;
                    var info = new HostKeyInfo(session.Host, session.Port, e.HostKeyName, sha256, verdict, previous);
                    var trusted = decision?.Invoke(info) ?? false;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (trusted)
                        knownHosts.Accept(session.Host, session.Port, e.HostKeyName, sha256, e.HostKey);
                    else
                        check.Failure = verdict == HostKeyVerdict.Mismatch
                            ? new SshSessionException(
                                SshFailureKind.HostKeyMismatch,
                                $"Host key for {session.Host}:{session.Port} has CHANGED (now {e.HostKeyName} SHA256:{sha256}). " +
                                "This may indicate a man-in-the-middle attack. Connection refused.")
                            : new SshSessionException(
                                SshFailureKind.HostKeyRejected, "Host key was not accepted.");
                    e.CanTrust = trusted;
                    break;
            }
        };
        return check;
    }

    /// <summary>
    /// Connects <paramref name="client"/>. A connection made straight to the host has no
    /// separate TCP preflight, which saves a round trip: instead it must reach the host and
    /// exchange versions within <paramref name="reachTimeout"/>, so an unreachable host still
    /// fails in seconds with the same message. SSH.NET stops watching the token once versions
    /// are exchanged, so the key exchange and login, which can wait on the user, keep the
    /// connection's own timeout. Connections through a jump host or proxy keep their preflight,
    /// which reports on the hop that failed.
    /// </summary>
    internal static void Connect(BaseClient client, Session session, SshJumpChain? via, TimeSpan reachTimeout,
        CancellationToken cancellationToken)
    {
        if (via is not null || ActiveProxy(session) is not null)
        {
            Preflight(session, via, reachTimeout, cancellationToken);
            client.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(reachTimeout);
        var reached = false;
        void Reached(object? sender, HostKeyEventArgs e) => reached = true;
        client.HostKeyReceived += Reached;
        try
        {
            client.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (!reached && !cancellationToken.IsCancellationRequested)
        {
            throw new SshSessionException(
                SshFailureKind.HostUnreachable, $"Could not reach {session.Host}:{session.Port} (connection timed out).", ex);
        }
        catch (System.Net.Sockets.SocketException ex) when (!reached)
        {
            throw new SshSessionException(
                SshFailureKind.HostUnreachable, $"Could not reach {session.Host}:{session.Port}: {ex.Message}", ex);
        }
        finally
        {
            client.HostKeyReceived -= Reached;
        }
    }

    internal static void PreflightTcp(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            socket.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(timeout, cancellationToken).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new SshSessionException(
                SshFailureKind.HostUnreachable, $"Could not reach {host}:{port} (connection timed out).");
        }
        catch (System.Net.Sockets.SocketException se)
        {
            throw new SshSessionException(
                SshFailureKind.HostUnreachable, $"Could not reach {host}:{port}: {se.Message}", se);
        }
    }
}
