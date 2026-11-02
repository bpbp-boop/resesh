using System.Net.Sockets;
using Resesh.Core.Backend;
using Resesh.Core.Models;

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
    private readonly BackendLifecycle _lifecycle = new();
    private Socket? _socket;
    private IDisposable? _route;
    private TelnetProtocol? _protocol;
    private TelnetTextCodec? _textCodec;
    private TelnetBbsSizeFilter? _bbsSizeFilter;
    private Thread? _reader;
    private Thread? _sender;
    private Exception? _sendFailure;

    // TCP keepalive: a peer that vanished without a FIN or RST (a dropped VPN, a powered-off
    // console server) is noticed within KeepAliveTime + Interval × RetryCount, about a
    // minute, instead of never: telnet itself has no keepalive and an idle tab sends nothing.
    internal const int KeepAliveTimeSeconds = 30;
    internal const int KeepAliveIntervalSeconds = 10;
    internal const int KeepAliveRetryCount = 3;

    public event TerminalOutputHandler? OutputReceived;

    /// <summary>Raised once when the server closes the connection (null) or it fails
    /// (the error). Never raised for a local <see cref="Stop"/>/<see cref="Dispose"/>.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    public bool IsConnected => _lifecycle.IsOpen && _socket is { Connected: true };

    /// <summary>"address:port" actually connected to, for the status bar.</summary>
    public string RemoteEndPoint { get; private set; } = "";

    /// <summary>Opens the connection and starts negotiation. Blocks; run off the UI thread.</summary>
    public void Connect(string host, int port, string terminalType, int columns, int rows,
        CancellationToken cancellationToken = default, TelnetTextEncoding textEncoding = TelnetTextEncoding.Auto,
        bool report80x25 = false, IDisposable? route = null)
    {
        // Whatever carries this connection (jump hosts) ends with it, connected or not: taken
        // under the gate, so a Dispose before or during the connect releases it either way.
        lock (_gate)
        {
            if (!_lifecycle.TryBeginConnect())
            {
                var refused = _lifecycle.CannotConnect();
                route?.Dispose();
                throw refused;
            }
            _route = route;
        }
        try
        {
            ConnectSocket(host, port, terminalType, columns, rows, cancellationToken, textEncoding, report80x25);
        }
        catch
        {
            // A failed connect is final, like a stop: releases the route and anything built.
            Dispose();
            throw;
        }
    }

    private void ConnectSocket(string host, int port, string terminalType, int columns, int rows,
        CancellationToken cancellationToken, TelnetTextEncoding textEncoding, bool report80x25)
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

        EnableKeepAlive(socket);
        var protocol = new TelnetProtocol(terminalType, report80x25 ? 80 : columns, report80x25 ? 25 : rows);
        lock (_gate)
        {
            // Dispose reads the socket under this gate after it stops, so either the
            // publish fails here or Dispose sees the socket and closes it.
            if (!_lifecycle.TryPublishOpen())
            {
                socket.Dispose();
                throw new OperationCanceledException(cancellationToken);
            }
            _socket = socket;
            _protocol = protocol;
            _textCodec = new TelnetTextCodec(textEncoding);
            _bbsSizeFilter = report80x25 ? new TelnetBbsSizeFilter() : null;
            RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? endpoint;
            EnqueueLocked(protocol.InitialNegotiation());
        }
        TraceHook?.Invoke($"telnet connected {RemoteEndPoint}");

        // Started after the publish: a stop in between leaves them a closed socket and a
        // completed queue, and they end without raising Closed.
        _sender = new Thread(() => SendLoop(socket)) { IsBackground = true, Name = "telnet-sender" };
        _sender.Start();
        _reader = new Thread(() => ReadLoop(socket)) { IsBackground = true, Name = "telnet-reader" };
        _reader.Start();
    }

    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetryCount);
        }
        catch (SocketException ex)
        {
            // Older stacks lack the per-socket timings; the connection works without them.
            TraceHook?.Invoke($"telnet keepalive not set: {ex.Message}");
        }
    }

    public void Write(byte[] data)
    {
        lock (_gate)
        {
            if (_protocol is null || _lifecycle.IsStopped)
                return;
            EnqueueLocked(_protocol.EncodeInput(_textCodec!.EncodeInput(data)));
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_protocol is null || _lifecycle.IsStopped)
                return;
            EnqueueLocked(_protocol.Resize(_bbsSizeFilter is null ? columns : 80, _bbsSizeFilter is null ? rows : 25));
        }
    }

    /// <summary>Sends the telnet BREAK command (IAC BRK). A console server turns it into
    /// a serial break on the attached device; a plain telnetd usually ignores it.</summary>
    public void SendBreak()
    {
        lock (_gate)
        {
            if (_protocol is not null && !_lifecycle.IsStopped)
                EnqueueLocked(TelnetProtocol.BreakCommand());
        }
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        // Outside the gate: a Closed being raised holds the lifecycle's lock, and this waits
        // for it rather than letting it land after the stop.
        if (!_lifecycle.BeginStop())
            return;
        Socket? socket;
        IDisposable? route;
        lock (_gate)
        {
            route = _route;
            _route = null;
            socket = _socket;
            _outgoing.CompleteAdding();
        }
        CloseSocket(socket); // unblocks the reader's Receive
        route?.Dispose();
    }

    private static void CloseSocket(Socket? socket)
    {
        if (socket is null)
            return;
        try { socket.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        socket.Dispose();
    }

    private void ReadLoop(Socket socket)
    {
        var buffer = new byte[16 * 1024];
        var data = new List<byte>(buffer.Length);
        var replies = new List<byte>();
        Exception? failure = null;
        try
        {
            while (!_lifecycle.IsStopped)
            {
                ReleaseHeldOutput(socket);
                var read = socket.Receive(buffer);
                if (read == 0)
                    break;

                data.Clear();
                replies.Clear();
                byte[] output;
                lock (_gate)
                {
                    _protocol!.Receive(buffer.AsSpan(0, read), data, replies);
                    if (replies.Count > 0)
                        EnqueueLocked([.. replies]);
                    output = _textCodec!.DecodeOutput(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data));
                    if (_bbsSizeFilter is not null)
                    {
                        replies.Clear();
                        output = _bbsSizeFilter.Process(output, replies);
                        if (replies.Count > 0)
                            EnqueueLocked(_protocol.EncodeInput([.. replies]));
                    }
                }
                if (replies.Count > 0)
                    TraceHook?.Invoke($"telnet negotiation reply: {replies.Count} bytes");
                if (output.Length > 0)
                    OutputReceived?.Invoke(output);
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
        // A send failure closed the socket under the reader; report the cause, not the symptom.
        failure = Volatile.Read(ref _sendFailure) ?? failure;

        byte[] tail;
        lock (_gate)
        {
            tail = _textCodec!.DecodeOutput([], flush: true);
            if (_bbsSizeFilter is not null)
                tail = _bbsSizeFilter.Process(tail, [], flush: true);
            _outgoing.CompleteAdding(); // releases the sender thread
        }
        if (!_lifecycle.IsStopped && tail.Length > 0)
            OutputReceived?.Invoke(tail);
        _lifecycle.TryRaiseRemoteClose(() =>
        {
            TraceHook?.Invoke($"telnet closed: {failure?.Message ?? "by server"}");
            Closed?.Invoke(failure);
        });
    }

    /// <summary>The BBS size filter holds output that could begin a probe until the rest
    /// arrives. Before blocking for more, give the rest a moment; if nothing comes, the held
    /// bytes go out as they are, so a screen that ends in a cursor move is not frozen.</summary>
    private void ReleaseHeldOutput(Socket socket)
    {
        bool held;
        lock (_gate)
            held = _bbsSizeFilter is { HasHeldOutput: true };
        if (!held || socket.Poll(TelnetBbsSizeFilter.HoldLimit, SelectMode.SelectRead))
            return;
        byte[] output;
        lock (_gate)
            output = _bbsSizeFilter!.FlushHeld();
        if (output.Length > 0 && !_lifecycle.IsStopped)
            OutputReceived?.Invoke(output);
    }

    private void EnqueueLocked(byte[] bytes)
    {
        if (bytes.Length > 0 && !_outgoing.IsAddingCompleted)
            _outgoing.Add(bytes);
    }

    private void SendLoop(Socket socket)
    {
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
            if (_lifecycle.IsStopped)
                return;
            TraceHook?.Invoke($"telnet send failed: {ex.Message}");
            // A half-open connection can fail sends while the reader waits forever, which
            // left the tab "connected" and the queue growing with every keystroke. Close the
            // socket so the reader ends and reports it, and stop queueing.
            Interlocked.CompareExchange(ref _sendFailure, ex, null);
            lock (_gate)
                _outgoing.CompleteAdding();
            CloseSocket(socket);
        }
    }
}
