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

/// <summary>Thin seam over <see cref="SerialPort"/> so the read/send/break/close lifecycle
/// can be tested deterministically without serial hardware.</summary>
internal interface ISerialPortAdapter : IDisposable
{
    bool IsOpen { get; }
    bool BreakState { get; set; }
    void Open();
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
    void DiscardOutBuffer();
}

internal sealed class SystemSerialPortAdapter : ISerialPortAdapter
{
    private readonly SerialPort _port;
    private Stream? _stream;

    public SystemSerialPortAdapter(string name, SerialTarget target)
    {
        var handshake = target.FlowControl switch
        {
            SerialFlowControl.XonXoff => Handshake.XOnXOff,
            SerialFlowControl.RtsCts => Handshake.RequestToSend,
            _ => Handshake.None,
        };
        _port = new SerialPort(name)
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
            Handshake = handshake,
            // Assert DTR and RTS in the initial DCB as PuTTY and SecureCRT do: many USB
            // adapters and console ports send nothing until they see them, and setting them
            // before Open avoids a low->high glitch pulse. With RTS/CTS the driver owns RTS.
            DtrEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout,
            ReadBufferSize = 64 * 1024,
        };
        if (handshake != Handshake.RequestToSend)
            _port.RtsEnable = true;
    }

    public bool IsOpen => _port.IsOpen;

    public bool BreakState
    {
        get => _port.BreakState;
        set => _port.BreakState = value;
    }

    public void Open()
    {
        _port.Open();
        _stream = _port.BaseStream;
    }

    public int Read(byte[] buffer, int offset, int count) => _stream!.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count) => _stream!.Write(buffer, offset, count);

    public void DiscardOutBuffer() => _port.DiscardOutBuffer();

    public void Dispose() => _port.Dispose();
}

/// <summary>Tracks background closes per COM port so an immediate Disconnect -> Reconnect
/// (or tab close -> reopen) waits briefly for our own close thread instead of failing with
/// "in use by another program".</summary>
internal sealed class SerialPortCloseTracker
{
    public static readonly SerialPortCloseTracker Shared = new();

    private readonly ConcurrentDictionary<string, Task> _pending = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable BeginClose(string portName)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[portName] = tcs.Task;
        return new Scope(this, portName, tcs);
    }

    public void WaitForPendingClose(string portName, TimeSpan timeout)
    {
        if (_pending.TryGetValue(portName, out var pending))
            pending.Wait(timeout);
    }

    private sealed class Scope(SerialPortCloseTracker owner, string portName, TaskCompletionSource tcs) : IDisposable
    {
        public void Dispose()
        {
            owner._pending.TryRemove(new KeyValuePair<string, Task>(portName, tcs.Task));
            tcs.TrySetResult();
        }
    }
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

    private static readonly TimeSpan DefaultCloseWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultZeroReadBackoff = TimeSpan.FromMilliseconds(20);
    private const int MaxConsecutiveZeroReads = 5;

    // Queued in place of data to send a break, compared by reference.
    private static readonly byte[] BreakMarker = [];

    private readonly Func<string, SerialTarget, ISerialPortAdapter> _portFactory;
    private readonly Func<string, bool> _portExists;
    private readonly SerialPortCloseTracker _closeTracker;
    private readonly TimeSpan _breakDuration;
    private readonly TimeSpan _closeWaitTimeout;
    private readonly TimeSpan _zeroReadBackoff;

    private readonly object _gate = new();
    private readonly object _closePortLock = new();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly BlockingCollection<byte[]> _outgoing = new();
    private ISerialPortAdapter? _port;
    private string? _portName;
    private Thread? _reader;
    private Thread? _sender;
    private readonly BackendLifecycle _lifecycle = new();
    private bool _portClosed;
    private int _closingPort;
    private int _closeFinished;
    private int _writesInFlight;

    public SerialTerminalSession()
        : this(static (name, target) => new SystemSerialPortAdapter(name, target),
            SerialPortList.Exists,
            SerialPortCloseTracker.Shared,
            BreakDuration,
            DefaultCloseWaitTimeout,
            DefaultZeroReadBackoff)
    {
    }

    internal SerialTerminalSession(
        Func<string, SerialTarget, ISerialPortAdapter> portFactory,
        Func<string, bool> portExists,
        SerialPortCloseTracker? closeTracker = null,
        TimeSpan? breakDuration = null,
        TimeSpan? closeWaitTimeout = null,
        TimeSpan? zeroReadBackoff = null)
    {
        _portFactory = portFactory;
        _portExists = portExists;
        _closeTracker = closeTracker ?? new SerialPortCloseTracker();
        _breakDuration = breakDuration ?? BreakDuration;
        _closeWaitTimeout = closeWaitTimeout ?? DefaultCloseWaitTimeout;
        _zeroReadBackoff = zeroReadBackoff ?? DefaultZeroReadBackoff;
    }

    public event TerminalOutputHandler? OutputReceived;

    /// <summary>Raised once when the port fails: an unplugged adapter reports an I/O error,
    /// or a write or break is refused. Never raised for a local <see cref="Stop"/>/<see cref="Dispose"/>.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    public bool IsConnected => _lifecycle.IsOpen && Volatile.Read(ref _port) is { IsOpen: true };

    /// <summary>The port and line settings in use, for the status bar.</summary>
    public string Description { get; private set; } = "";

    /// <summary>Opens the port. Blocks briefly; run off the UI thread.</summary>
    public void Open(SerialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_lifecycle.TryBeginConnect())
            throw _lifecycle.CannotConnect();
        try
        {
            OpenPort(target);
        }
        catch
        {
            // A failed open is final, like a stop.
            _lifecycle.BeginStop();
            throw;
        }
    }

    private void OpenPort(SerialTarget target)
    {
        var name = SerialTarget.NormalizePortName(target.PortName)
            ?? throw new SerialSessionException(string.IsNullOrWhiteSpace(target.PortName)
                ? "No serial port set for this session."
                : $"\"{target.PortName}\" is not a serial port name. Use a name like COM3.");

        if (target.ValidateLineSettings() is { } settingsError)
            throw new SerialSessionException($"{name} does not accept {target.Framing}: {settingsError}");

        // Wait briefly if a previous Disconnect/close in this process is still releasing the handle.
        _closeTracker.WaitForPendingClose(name, _closeWaitTimeout);
        if (_lifecycle.IsStopped)
            throw new OperationCanceledException();

        ISerialPortAdapter? port = null;
        try
        {
            port = _portFactory(name, target);
            port.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            try { port?.Dispose(); }
            catch (Exception disposeEx) when (disposeEx is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
            var missing = !_portExists(name);
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
            // Dispose reads the port under this gate after it stops, so either the publish
            // fails here or Dispose sees the port and closes it.
            if (!_lifecycle.TryPublishOpen())
            {
                try { port.Dispose(); }
                catch (Exception disposeEx) when (disposeEx is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
                throw new OperationCanceledException();
            }
            _port = port;
            _portName = name;
            Description = (target with { PortName = name }).Describe();
        }
        TraceHook?.Invoke($"serial opened {Description}");

        _sender = new Thread(() => SendLoop(port)) { IsBackground = true, Name = "serial-sender" };
        _sender.Start();
        _reader = new Thread(() => ReadLoop(port)) { IsBackground = true, Name = "serial-reader" };
        _reader.Start();
    }

    public void Write(byte[] data)
    {
        if (data.Length == 0)
            return;
        lock (_gate)
        {
            if (_port is not null && !_lifecycle.IsStopped && !_outgoing.IsAddingCompleted)
                _outgoing.Add(data);
        }
    }

    /// <summary>A serial line has no window size.</summary>
    public bool Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0) => true;

    /// <summary>Holds the line in the break state for <see cref="BreakDuration"/>, after
    /// whatever was typed before it has been sent.</summary>
    public void SendBreak()
    {
        lock (_gate)
        {
            if (_port is not null && !_lifecycle.IsStopped && !_outgoing.IsAddingCompleted)
                _outgoing.Add(BreakMarker);
        }
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        // Outside the gate: a Closed being raised holds the lifecycle's lock, and this waits
        // for it rather than letting it land after the stop.
        if (!_lifecycle.BeginStop())
            return;
        Interlocked.Exchange(ref _closingPort, 1);
        _stopSignal.Set();
        ISerialPortAdapter? port;
        string? portName;
        lock (_gate)
        {
            port = _port;
            portName = _portName;
            _outgoing.CompleteAdding();
        }
        if (port is null || Volatile.Read(ref _closeFinished) != 0)
            return;
        // SerialPort.Dispose flushes pending output, and Windows waits for flow control
        // with no timeout: a device holding CTS low or sitting on XOFF would hang the
        // caller (the UI thread, for Disconnect and tab close). Close on a thread of its own,
        // registered first so an immediate Reconnect waits for this handle to finish closing.
        var closing = portName is null ? null : _closeTracker.BeginClose(portName);
        new Thread(() =>
        {
            using (closing)
                Close(port);
        })
        { IsBackground = true, Name = "serial-close" }.Start();
    }

    // Purge first so neither a blocked write on the sender thread nor the flush inside
    // Dispose waits on the device. Serialized under _closePortLock so a second thread
    // entering Fail() never raises Closed before the first has finished releasing the handle.
    private void Close(ISerialPortAdapter? port)
    {
        if (port is null)
            return;
        Interlocked.Exchange(ref _closingPort, 1);
        _stopSignal.Set();
        lock (_closePortLock)
        {
            if (_portClosed)
                return;
            _portClosed = true;
            TryDiscardOutBuffer(port);
            // If SendLoop entered Write just before _closingPort became visible, give it a
            // moment to hit the driver and purge again so Dispose does not hang flushing it.
            if (Volatile.Read(ref _writesInFlight) > 0)
            {
                Thread.Sleep(5);
                TryDiscardOutBuffer(port);
            }
            try { port.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
            finally { Volatile.Write(ref _closeFinished, 1); }
        }
    }

    private static void TryDiscardOutBuffer(ISerialPortAdapter port)
    {
        try { if (port.IsOpen) port.DiscardOutBuffer(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>The one close path for a failure seen by either thread: stops the sender,
    /// closes the port (which ends the other thread's blocked read or write) and raises
    /// <see cref="Closed"/> once. Raises nothing once a local stop has begun.</summary>
    private void Fail(Exception? failure)
    {
        ISerialPortAdapter? port;
        lock (_gate)
        {
            _outgoing.CompleteAdding(); // releases the sender thread
            port = _port;
        }
        if (!_lifecycle.IsOpen)
            return; // stopped (Dispose closes the port) or already reported
        // Closed before Closed is raised: a reconnect from its handler reopens this port.
        // Outside the lifecycle lock, so a stop is never held up by the driver.
        Close(port);
        _lifecycle.TryRaiseRemoteClose(() =>
        {
            TraceHook?.Invoke($"serial closed: {failure?.Message}");
            Closed?.Invoke(failure);
        });
    }

    private void ReadLoop(ISerialPortAdapter port)
    {
        var buffer = new byte[16 * 1024];
        Exception? failure = null;
        var zeroReads = 0;
        try
        {
            while (!_lifecycle.IsStopped)
            {
                var read = port.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    if (_lifecycle.IsStopped || !port.IsOpen)
                        break;
                    if (++zeroReads >= MaxConsecutiveZeroReads)
                    {
                        failure = new IOException("The serial port stopped responding.");
                        break;
                    }
                    if (_stopSignal.Wait(_zeroReadBackoff))
                        break;
                    continue;
                }
                zeroReads = 0;
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

    private void SendLoop(ISerialPortAdapter port)
    {
        try
        {
            foreach (var bytes in _outgoing.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _closingPort) != 0 || _lifecycle.IsStopped)
                    break;
                if (ReferenceEquals(bytes, BreakMarker))
                {
                    TraceHook?.Invoke($"serial break ({Description})");
                    port.BreakState = true;
                    try { _stopSignal.Wait(_breakDuration); }
                    finally
                    {
                        if (Volatile.Read(ref _closingPort) == 0 && !_lifecycle.IsStopped && port.IsOpen)
                            port.BreakState = false;
                    }
                    continue;
                }
                Interlocked.Increment(ref _writesInFlight);
                try
                {
                    if (Volatile.Read(ref _closingPort) != 0 || _lifecycle.IsStopped)
                        break;
                    port.Write(bytes, 0, bytes.Length);
                }
                finally
                {
                    Interlocked.Decrement(ref _writesInFlight);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException
            or InvalidOperationException or TimeoutException)
        {
            // A refused write or break does not always fail the reader too, so close here
            // rather than leave a connected tab whose input goes nowhere.
            if (!_lifecycle.IsStopped)
                TraceHook?.Invoke($"serial send failed: {ex.Message}");
            Fail(ex);
        }
    }
}
