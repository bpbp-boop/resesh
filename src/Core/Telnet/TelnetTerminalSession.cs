using System.Net.Sockets;
using Resesh.Core.Backend;

namespace Resesh.Core.Telnet;

/// <summary>Connecting failed (name resolution, refused, timed out, unreachable).</summary>
public sealed class TelnetSessionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// One live telnet connection: a TCP socket, the <see cref="TelnetProtocol"/> negotiation
/// state, a reader thread, and a sender thread. Sends are queued rather than written
/// inline so a large paste to a slow server can never stall the reader (and with it the
/// server, if it is blocked writing to us). Plaintext by design — meant for console
/// servers and lab/management networks. All calls are safe from any thread; events fire
/// on the reader thread.
/// </summary>
public sealed class TelnetTerminalSession : ITerminalBackend, IBreakSender
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    // Guards the protocol state and the send queue's order: bytes are encoded and
    // enqueued under one lock, so the wire sees them in the order they were produced.
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _outgoing = new();
    private Socket? _socket;
    private TelnetProtocol? _protocol;
    private Thread? _reader;
    private Thread? _sender;
    private volatile bool _stopped;
    private int _closedRaised;

    public event TerminalOutputHandler? OutputReceived;

    /// <summary>Raised once when the server closes the connection (null) or it fails
    /// (the error). Never raised for a local <see cref="Stop"/>/<see cref="Dispose"/>.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    public bool IsConnected => !_stopped && _socket is { Connected: true } && _closedRaised == 0;

    /// <summary>"address:port" actually connected to, for the status bar.</summary>
    public string RemoteEndPoint { get; private set; } = "";

    /// <summary>Opens the connection and starts negotiation. Blocks; run off the UI thread.</summary>
    public void Connect(string host, int port, string terminalType, int columns, int rows,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new TelnetSessionException("No host set for this session.");

        var endpoint = $"{host}:{port}";
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                socket.ConnectAsync(host.Trim(), port, timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TelnetSessionException($"Timed out connecting to {endpoint}.");
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                throw new TelnetSessionException(ex.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                        => $"Host not found: {host}.",
                    SocketError.ConnectionRefused => $"Connection refused by {endpoint} — is a telnet server listening on that port?",
                    SocketError.TimedOut => $"Timed out connecting to {endpoint}.",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"{host} is unreachable.",
                    _ => $"Could not connect to {endpoint}: {ex.Message}",
                }, ex);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        var protocol = new TelnetProtocol(terminalType, columns, rows);
        lock (_gate)
        {
            if (_stopped)
            {
                socket.Dispose();
                throw new OperationCanceledException(cancellationToken);
            }
            _socket = socket;
            _protocol = protocol;
            RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? endpoint;
            EnqueueLocked(protocol.InitialNegotiation());
        }
        TraceHook?.Invoke($"telnet connected {RemoteEndPoint}");

        _sender = new Thread(SendLoop) { IsBackground = true, Name = "telnet-sender" };
        _sender.Start();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "telnet-reader" };
        _reader.Start();
    }

    public void Write(byte[] data)
    {
        lock (_gate)
        {
            if (_protocol is null || _stopped)
                return;
            EnqueueLocked(_protocol.EncodeInput(data));
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_protocol is null || _stopped)
                return;
            EnqueueLocked(_protocol.Resize(columns, rows));
        }
    }

    /// <summary>Sends the telnet BREAK command (IAC BRK). A console server turns it into
    /// a serial break on the attached device; a plain telnetd usually ignores it.</summary>
    public void SendBreak()
    {
        lock (_gate)
        {
            if (_protocol is not null && !_stopped)
                EnqueueLocked(TelnetProtocol.BreakCommand());
        }
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        Socket? socket;
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            socket = _socket;
            _outgoing.CompleteAdding();
        }
        if (socket is null)
            return;
        try { socket.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        socket.Dispose(); // unblocks the reader's Receive
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var data = new List<byte>(buffer.Length);
        var replies = new List<byte>();
        Exception? failure = null;
        try
        {
            var socket = _socket!;
            while (!_stopped)
            {
                var read = socket.Receive(buffer);
                if (read == 0)
                    break;

                data.Clear();
                replies.Clear();
                lock (_gate)
                {
                    _protocol!.Receive(buffer.AsSpan(0, read), data, replies);
                    if (replies.Count > 0)
                        EnqueueLocked([.. replies]);
                }
                if (replies.Count > 0)
                    TraceHook?.Invoke($"telnet negotiation reply: {replies.Count} bytes");
                if (data.Count > 0)
                    OutputReceived?.Invoke(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data));
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Many telnet servers (and terminal servers on logout) end the session with a
            // reset rather than a FIN — that is a normal close, not a failure.
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            failure = ex;
        }

        lock (_gate)
            _outgoing.CompleteAdding(); // releases the sender thread
        if (_stopped || Interlocked.Exchange(ref _closedRaised, 1) != 0)
            return;
        TraceHook?.Invoke($"telnet closed: {failure?.Message ?? "by server"}");
        Closed?.Invoke(failure);
    }

    private void EnqueueLocked(byte[] bytes)
    {
        if (bytes.Length > 0 && !_outgoing.IsAddingCompleted)
            _outgoing.Add(bytes);
    }

    private void SendLoop()
    {
        var socket = _socket!;
        try
        {
            foreach (var bytes in _outgoing.GetConsumingEnumerable())
            {
                var sent = 0;
                while (sent < bytes.Length)
                    sent += socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The reader observes the dead socket and reports the close.
            if (!_stopped)
                TraceHook?.Invoke($"telnet send failed: {ex.Message}");
        }
    }
}
