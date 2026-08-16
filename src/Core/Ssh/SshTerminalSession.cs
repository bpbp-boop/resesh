using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sessions.Core.Models;
using Session = Sessions.Core.Models.Session;

namespace Sessions.Core.Ssh;

public sealed record HostKeyInfo(
    string Host, int Port, string KeyType, string Sha256Fingerprint, HostKeyVerdict Verdict,
    KnownHostEntry? Previous = null);

public enum SshFailureKind
{
    HostUnreachable,
    AuthenticationFailed,
    HostKeyRejected,
    HostKeyMismatch,
    Other,
}

public sealed class SshSessionException(SshFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SshFailureKind Kind { get; } = kind;
}

/// <summary>Outcome of a one-off exec-channel command (see <see cref="SshTerminalSession.RunCommand"/>).</summary>
public sealed record SshCommandResult(bool Success, string Output, string Error);

/// <summary>
/// One live SSH shell: SshClient + ShellStream plus a background reader.
/// All calls are safe from any thread; events fire on background threads.
/// </summary>
public sealed class SshTerminalSession : Backend.ITerminalBackend
{
    private readonly KnownHostsStore _knownHosts;
    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private System.Threading.Timer? _watchdog;
    private int _closedRaised;
    private volatile bool _disposed;

    public event Action<byte[]>? OutputReceived;

    /// <summary>Raised once when the connection ends, with null for a clean local disconnect.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>
    /// Called (on the connect thread) when a host key needs a user decision — an unknown
    /// key, or a CHANGED key (Verdict = Mismatch, with the previously trusted key in
    /// <see cref="HostKeyInfo.Previous"/>). Return true to trust and persist the key.
    /// No handler = deny.
    /// </summary>
    public Func<HostKeyInfo, bool>? HostKeyDecision { get; set; }

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    public bool IsConnected => _client?.IsConnected == true && _shell is not null;

    /// <summary>Negotiated server-to-client cipher, once connected.</summary>
    public string? Encryption => _client?.ConnectionInfo?.CurrentServerEncryption;

    /// <summary>"keytype SHA256:fingerprint" of the server key seen during connect.</summary>
    public string? HostKeyFingerprint { get; private set; }

    /// <summary>The server's SSH version banner (e.g. "SSH-2.0-OpenSSH_8.9p1 Ubuntu-…"),
    /// once connected; feeds the icon auto-suggestion.</summary>
    public string? ServerBanner => _client?.ConnectionInfo?.ServerVersion;

    public SshTerminalSession(KnownHostsStore knownHosts)
    {
        _knownHosts = knownHosts;
    }

    /// <summary>
    /// Connects and opens the shell. Blocking — run on a background thread.
    /// A non-null <paramref name="bootstrapCommand"/> is typed into the shell as its first
    /// input (persistent sessions use this to exec into tmux).
    /// </summary>
    public void Connect(Session session, string? secret, string terminalType, int columns, int rows,
        string? bootstrapCommand = null)
    {
        if (_client is not null)
            throw new InvalidOperationException("Session already used; create a new instance per connection.");

        // Fast pre-flight so unreachable hosts fail in seconds; the real connect then gets a
        // generous timeout because the first-connect host key dialog sits inside the handshake.
        SshConnectionFactory.PreflightTcp(session.Host, session.Port, TimeSpan.FromSeconds(10));

        var auth = SshConnectionFactory.BuildAuthMethods(session, secret);
        var connectionInfo = new ConnectionInfo(session.Host, session.Port, session.Username, auth)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        var client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };

        SshSessionException? hostKeyFailure = null;
        client.HostKeyReceived += (_, e) =>
        {
            var sha256 = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            HostKeyFingerprint = $"{e.HostKeyName} SHA256:{sha256}";
            var verdict = _knownHosts.Check(session.Host, session.Port, e.HostKeyName, sha256);
            switch (verdict)
            {
                case HostKeyVerdict.Match:
                    e.CanTrust = true;
                    break;
                default:
                    // Unknown key, or a changed key (the dialog demands typed confirmation
                    // for the latter). Default-deny when no handler is wired.
                    var previous = verdict == HostKeyVerdict.Mismatch
                        ? _knownHosts.Lookup(session.Host, session.Port)
                        : null;
                    var info = new HostKeyInfo(session.Host, session.Port, e.HostKeyName, sha256, verdict, previous);
                    var trusted = HostKeyDecision?.Invoke(info) ?? false;
                    if (trusted)
                        _knownHosts.Accept(session.Host, session.Port, e.HostKeyName, sha256);
                    else
                        hostKeyFailure = verdict == HostKeyVerdict.Mismatch
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

        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (hostKeyFailure is not null)
                throw hostKeyFailure;
            throw SshConnectionFactory.Classify(ex);
        }

        _client = client;
        _shell = client.CreateShellStream(terminalType, (uint)columns, (uint)rows, 0, 0, 64 * 1024);
        if (bootstrapCommand is not null)
        {
            // The pty buffers this until the login shell reads stdin; its echo is short-lived
            // (the persistent-session bootstrap clears the screen as its first action).
            var bytes = System.Text.Encoding.UTF8.GetBytes(bootstrapCommand + "\n");
            _shell.Write(bytes, 0, bytes.Length);
            _shell.Flush();
        }
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoop(_shell, _readerCts.Token));

        // A dead peer is not always noticed by the blocked ShellStream read or the keepalive
        // (observed: a killed server left the session "connected" indefinitely), so:
        // 1. surface transport errors immediately, 2. poll the socket state as a fallback.
        client.ErrorOccurred += (_, e) =>
        {
            TraceHook?.Invoke($"ErrorOccurred: {e.Exception.Message}");
            RaiseClosed(e.Exception);
        };
        _watchdog = new System.Threading.Timer(_ =>
        {
            if (!_disposed && _client?.IsConnected != true)
            {
                TraceHook?.Invoke("watchdog: client no longer connected");
                RaiseClosed(null);
            }
        }, null, dueTime: 5000, period: 5000);
    }

    /// <summary>Closed fires exactly once, from whichever detector notices first.</summary>
    private void RaiseClosed(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0 && !_disposed)
        {
            _watchdog?.Dispose();
            Closed?.Invoke(failure);
        }
    }

    // Raw-stream trace (DEBUG diagnostics; capped per connection so trace.log stays sane).
    private int _tracedBytes;
    private const int TraceCap = 128 * 1024;

    private void ReadLoop(ShellStream shell, CancellationToken token)
    {
        var buffer = new byte[32 * 1024];
        Exception? failure = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = shell.Read(buffer, 0, buffer.Length);
                if (read < 0)
                    break;
                if (read == 0)
                {
                    if (_client?.IsConnected != true)
                        break;
                    continue;
                }
                if (TraceHook is not null && _tracedBytes < TraceCap)
                {
                    _tracedBytes += read;
                    TraceHook.Invoke("raw: " + EscapeControls(buffer.AsSpan(0, Math.Min(read, 4096))));
                }
                OutputReceived?.Invoke(buffer[..read]);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            failure = ex;
        }

        if (!token.IsCancellationRequested)
        {
            TraceHook?.Invoke($"ReadLoop exit: failure={failure?.GetType().Name} {failure?.Message}; clientConnected={_client?.IsConnected}");
            RaiseClosed(failure);
        }
    }

    private static string EscapeControls(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length + 64);
        foreach (var b in data)
        {
            if (b == 0x1b)
                sb.Append("\\e");
            else if (b is >= 0x20 and < 0x7f)
                sb.Append((char)b);
            else if (b == (byte)'\n')
                sb.Append("\\n");
            else if (b == (byte)'\r')
                sb.Append("\\r");
            else
                sb.Append($"\\x{b:x2}");
        }
        return sb.ToString();
    }

    public void Write(byte[] data)
    {
        try
        {
            var shell = _shell;
            if (shell is null || _disposed || _client?.IsConnected != true)
                return;
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (SshException) { } // remote already gone ("Client not connected."); reader loop reports it
    }

    public void Resize(int columns, int rows) =>
        ShellStreamResizer.TryResize(_shell, columns, rows);

    /// <summary>Runs a one-off command on its own channel (e.g. tmux kill-session). Blocking.</summary>
    public bool TryRunCommand(string command)
    {
        try
        {
            var client = _client;
            if (client?.IsConnected != true || _disposed)
                return false;
            using var cmd = client.CreateCommand(command);
            cmd.Execute();
            return cmd.ExitStatus == 0;
        }
        catch (Exception e) when (e is SshException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Like <see cref="TryRunCommand"/> but captures stdout/stderr so callers can report
    /// WHY a query failed, not just that it did. Null when the channel is unavailable.
    /// Blocking; used for the tmux cwd query.
    /// </summary>
    public SshCommandResult? RunCommand(string command)
    {
        try
        {
            var client = _client;
            if (client?.IsConnected != true || _disposed)
                return null;
            using var cmd = client.CreateCommand(command);
            var output = cmd.Execute();
            return new SshCommandResult(cmd.ExitStatus == 0, output, cmd.Error);
        }
        catch (Exception e) when (e is SshException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            TraceHook?.Invoke($"RunCommand failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Clean local disconnect: suppresses Closed, per the dispose-order plan.</summary>
    public void Disconnect()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Exchange(ref _closedRaised, 1); // a local disconnect never raises Closed
        _watchdog?.Dispose();
        TraceHook?.Invoke("Disconnect called");

        _readerCts?.Cancel();
        try { _shell?.Dispose(); } catch (ObjectDisposedException) { }
        _shell = null;
        try { _readerTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }

        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
            catch (Exception e) when (e is SshConnectionException or ObjectDisposedException or IOException) { }
            client.Dispose();
        }
    }

    /// <summary>ITerminalBackend: a user-initiated stop is a clean local disconnect.</summary>
    public void Stop() => Disconnect();

    public void Dispose() => Disconnect();
}
