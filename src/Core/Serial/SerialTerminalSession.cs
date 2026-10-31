using System.Collections.Concurrent;
using System.IO.Ports;
using Resesh.Core.Backend;
using Resesh.Core.Models;

namespace Resesh.Core.Serial;

/// <summary>Opening the port failed (missing, in use, or settings the driver refuses).</summary>
public sealed class SerialSessionException(string message, bool portMissing = false, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The port does not exist right now: a USB adapter that is unplugged.</summary>
    public bool PortMissing { get; } = portMissing;
}

/// <summary>
/// One open serial port: a reader thread and a sender thread, like telnet. Sends are queued
/// so a device holding off flow control (XOFF, CTS low) can never stall the UI, and so a
/// break is sent after the bytes typed before it. Bytes pass through unchanged. All calls
/// are safe from any thread and return without waiting on the device; events fire on the
/// reader or sender thread.
/// </summary>
public sealed class SerialTerminalSession : ITerminalBackend, IBreakSender
{
    /// <summary>How long the line is held in the break state. Cisco ROMMON and Juniper
    /// loader want at least a quarter second; most UARTs accept anything longer.</summary>
    public static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(300);

    // Queued in place of data to send a break, compared by reference.
    private static readonly byte[] BreakMarker = [];

    private readonly object _gate = new();
    private readonly BlockingCollection<byte[]> _outgoing = new();
    private SerialPort? _port;
    private Stream? _stream;
    private Thread? _reader;
    private Thread? _sender;
    private volatile bool _stopped;
    private int _closedRaised;

    public event TerminalOutputHandler? OutputReceived;

    /// <summary>Raised once when the port fails: an unplugged adapter reports an I/O error,
    /// or a write or break is refused. Never raised for a local <see cref="Stop"/>/<see cref="Dispose"/>.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    public bool IsConnected => !_stopped && _closedRaised == 0 && _port is { IsOpen: true };

    /// <summary>The port and line settings in use, for the status bar.</summary>
    public string Description { get; private set; } = "";

    /// <summary>Opens the port. Blocks briefly; run off the UI thread.</summary>
    public void Open(SerialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = SerialTarget.NormalizePortName(target.PortName)
            ?? throw new SerialSessionException(string.IsNullOrWhiteSpace(target.PortName)
                ? "No serial port set for this session."
                : $"\"{target.PortName}\" is not a serial port name. Use a name like COM3.");

        var port = new SerialPort(name)
        {
            BaudRate = target.BaudRate,
            DataBits = target.DataBits,
            Parity = target.Parity switch
            {
                SerialParity.Odd => Parity.Odd,
                SerialParity.Even => Parity.Even,
                SerialParity.Mark => Parity.Mark,
                SerialParity.Space => Parity.Space,
                _ => Parity.None,
            },
            StopBits = target.StopBits switch
            {
                SerialStopBits.OnePointFive => StopBits.OnePointFive,
                SerialStopBits.Two => StopBits.Two,
                _ => StopBits.One,
            },
            Handshake = target.FlowControl switch
            {
                SerialFlowControl.XonXoff => Handshake.XOnXOff,
                SerialFlowControl.RtsCts => Handshake.RequestToSend,
                _ => Handshake.None,
            },
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout,
            ReadBufferSize = 64 * 1024,
        };
        try
        {
            port.Open();
            // Assert DTR and RTS as PuTTY and SecureCRT do: many USB adapters and console
            // ports send nothing until they see them. With RTS/CTS the driver owns RTS.
            port.DtrEnable = true;
            if (target.FlowControl != SerialFlowControl.RtsCts)
                port.RtsEnable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            var missing = !SerialPortList.Exists(name);
            throw new SerialSessionException(ex switch
            {
                _ when missing => $"{name} is not connected.",
                UnauthorizedAccessException => $"{name} is in use by another program.",
                ArgumentException or InvalidOperationException => $"{name} does not accept {target.Framing}: {ex.Message}",
                _ => $"Could not open {name}: {ex.Message}",
            }, missing, ex);
        }

        lock (_gate)
        {
            if (_stopped)
            {
                port.Dispose();
                throw new OperationCanceledException();
            }
            _port = port;
            _stream = port.BaseStream;
            Description = (target with { PortName = name }).Describe();
        }
        TraceHook?.Invoke($"serial opened {Description}");

        _sender = new Thread(SendLoop) { IsBackground = true, Name = "serial-sender" };
        _sender.Start();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "serial-reader" };
        _reader.Start();
    }

    public void Write(byte[] data)
    {
        if (data.Length == 0)
            return;
        lock (_gate)
        {
            if (_port is not null && !_stopped && !_outgoing.IsAddingCompleted)
                _outgoing.Add(data);
        }
    }

    /// <summary>A serial line has no window size.</summary>
    public void Resize(int columns, int rows) { }

    /// <summary>Holds the line in the break state for <see cref="BreakDuration"/>, after
    /// whatever was typed before it has been sent.</summary>
    public void SendBreak()
    {
        lock (_gate)
        {
            if (_port is not null && !_stopped && !_outgoing.IsAddingCompleted)
                _outgoing.Add(BreakMarker);
        }
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        SerialPort? port;
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            port = _port;
            _outgoing.CompleteAdding();
        }
        if (port is null)
            return;
        // SerialPort.Dispose flushes pending output, and Windows waits for flow control
        // with no timeout: a device holding CTS low or sitting on XOFF would hang the
        // caller (the UI thread, for Disconnect and tab close). Close on a thread of its own.
        new Thread(() => Close(port)) { IsBackground = true, Name = "serial-close" }.Start();
    }

    // Purge first so neither a blocked write on the sender thread nor the flush inside
    // Dispose waits on the device. Closing a port whose adapter was unplugged throws from
    // the driver; the handle is released either way.
    private static void Close(SerialPort? port)
    {
        if (port is null)
            return;
        try { if (port.IsOpen) port.DiscardOutBuffer(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
        try { port.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>The one close path for a failure seen by either thread: stops the sender,
    /// closes the port (which ends the other thread's blocked read or write) and raises
    /// <see cref="Closed"/> once. Does nothing after a local stop.</summary>
    private void Fail(Exception? failure)
    {
        SerialPort? port;
        lock (_gate)
        {
            _outgoing.CompleteAdding(); // releases the sender thread
            port = _port;
        }
        if (_stopped || Interlocked.Exchange(ref _closedRaised, 1) != 0)
            return;
        TraceHook?.Invoke($"serial closed: {failure?.Message}");
        Close(port);
        Closed?.Invoke(failure);
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        Exception? failure = null;
        try
        {
            var stream = _stream!;
            while (!_stopped)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    continue;
                OutputReceived?.Invoke(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            failure = ex;
        }
        Fail(failure);
    }

    private void SendLoop()
    {
        try
        {
            foreach (var bytes in _outgoing.GetConsumingEnumerable())
            {
                if (ReferenceEquals(bytes, BreakMarker))
                {
                    var port = _port!;
                    port.BreakState = true;
                    try { Thread.Sleep(BreakDuration); }
                    finally { port.BreakState = false; }
                    continue;
                }
                _stream!.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException
            or InvalidOperationException or TimeoutException)
        {
            // A refused write or break does not always fail the reader too, so close here
            // rather than leave a connected tab whose input goes nowhere.
            if (!_stopped)
                TraceHook?.Invoke($"serial send failed: {ex.Message}");
            Fail(ex);
        }
    }
}
