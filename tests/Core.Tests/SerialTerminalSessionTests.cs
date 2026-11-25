using System.Collections.Concurrent;
using System.Text;
using Resesh.Core.Models;
using Resesh.Core.Serial;

namespace Resesh.Core.Tests;

public sealed class SerialTerminalSessionTests
{
    private sealed class FakeSerialPort : ISerialPortAdapter
    {
        private readonly BlockingCollection<Func<byte[], int, int, int>> _reads = new();
        private readonly List<string> _events = [];
        private readonly object _eventsGate = new();
        private readonly ManualResetEventSlim _disposedEvent = new(false);
        private bool _open;
        private bool _breakState;
        private int _disposeCount;

        public Action? OnOpen { get; init; }
        public Action<byte[]>? OnWrite { get; init; }
        public Action? OnDiscardOutBuffer { get; init; }
        public Action? OnDispose { get; init; }

        public bool IsOpen => _open;
        public bool IsDisposed => _disposedEvent.IsSet;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public ManualResetEventSlim DisposedSignal => _disposedEvent;

        public bool BreakState
        {
            get => _breakState;
            set
            {
                _breakState = value;
                Record(value ? "break:on" : "break:off");
            }
        }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_eventsGate)
                    return [.. _events];
            }
        }

        public void Open()
        {
            OnOpen?.Invoke();
            _open = true;
        }

        public void EnqueueRead(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _reads.Add((buffer, offset, count) =>
            {
                var n = Math.Min(count, bytes.Length);
                Buffer.BlockCopy(bytes, 0, buffer, offset, n);
                return n;
            });
        }

        public void EnqueueZeroRead(bool closePort = false) =>
            _reads.Add((_, _, _) =>
            {
                if (closePort)
                    _open = false;
                return 0;
            });

        public void EnqueueReadException(Exception ex) =>
            _reads.Add((_, _, _) => throw ex);

        public int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                var step = _reads.Take();
                return step(buffer, offset, count);
            }
            catch (InvalidOperationException)
            {
                throw new ObjectDisposedException(nameof(FakeSerialPort));
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var copy = buffer.AsSpan(offset, count).ToArray();
            Record("write:" + Encoding.UTF8.GetString(copy));
            OnWrite?.Invoke(copy);
        }

        public void DiscardOutBuffer()
        {
            Record("discard");
            OnDiscardOutBuffer?.Invoke();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _open = false;
            OnDispose?.Invoke();
            _reads.CompleteAdding();
            _disposedEvent.Set();
        }

        private void Record(string entry)
        {
            lock (_eventsGate)
                _events.Add(entry);
        }
    }

    private static SerialTarget Target(string port = "COM5") => new() { PortName = port };

    [Fact]
    public async Task ReadWriteAndBreak_PreserveOrder()
    {
        var writeAfterBreak = new ManualResetEventSlim(false);
        var wrapped = new FakeSerialPort
        {
            OnWrite = bytes =>
            {
                if (Encoding.UTF8.GetString(bytes) == "after")
                    writeAfterBreak.Set();
            },
        };

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SerialTerminalSession((_, _) => wrapped, _ => true, breakDuration: TimeSpan.FromMilliseconds(25));
        session.OutputReceived += data => received.TrySetResult(Encoding.UTF8.GetString(data));

        session.Open(Target("com5"));
        Assert.True(session.IsConnected);
        Assert.Equal("COM5 9600 8N1", session.Description);
        Assert.True(session.Resize(120, 40));

        wrapped.EnqueueRead("router# ");
        Assert.Equal("router# ", await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        session.Write([]); // ignored
        session.Write(Encoding.UTF8.GetBytes("before"));
        session.SendBreak();
        session.Write(Encoding.UTF8.GetBytes("after"));

        Assert.True(writeAfterBreak.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(["write:before", "break:on", "break:off", "write:after"], wrapped.Events);
    }

    [Fact]
    public void SimultaneousReadAndWriteFailure_ClosesPortBeforeRaisingClosedOnce()
    {
        var disposeStarted = new ManualResetEventSlim(false);
        var allowDisposeFinish = new ManualResetEventSlim(false);
        var disposeCompleted = false;
        var writeEntered = new ManualResetEventSlim(false);
        var writeGate = new ManualResetEventSlim(false);

        var port = new FakeSerialPort
        {
            OnWrite = _ =>
            {
                writeEntered.Set();
                writeGate.Wait(TimeSpan.FromSeconds(2));
                throw new IOException("write failed on unplug");
            },
            OnDiscardOutBuffer = () => writeGate.Set(),
            OnDispose = () =>
            {
                disposeStarted.Set();
                allowDisposeFinish.Wait(TimeSpan.FromSeconds(2));
                Volatile.Write(ref disposeCompleted, true);
            },
        };

        var closedCount = 0;
        var disposedWhenClosedFired = false;
        var closedSignal = new ManualResetEventSlim(false);

        using var session = new SerialTerminalSession((_, _) => port, _ => true);
        session.Closed += _ =>
        {
            disposedWhenClosedFired = Volatile.Read(ref disposeCompleted);
            Interlocked.Increment(ref closedCount);
            closedSignal.Set();
        };

        session.Open(Target());
        // Wait until SendLoop is actively inside Write before failing ReadLoop, so both
        // threads always fail concurrently while disposal is gated.
        session.Write(Encoding.UTF8.GetBytes("x"));
        Assert.True(writeEntered.Wait(TimeSpan.FromSeconds(2)));
        port.EnqueueReadException(new IOException("read failed on unplug"));

        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(50);
        Assert.False(closedSignal.IsSet);

        allowDisposeFinish.Set();
        Assert.True(closedSignal.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(disposedWhenClosedFired);
        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ZeroByteRead_WhenDriverKeepsPortOpen_FailsInsteadOfSpinning()
    {
        var port = new FakeSerialPort();
        for (var i = 0; i < 10; i++)
            port.EnqueueZeroRead(closePort: false);

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SerialTerminalSession(
            (_, _) => port,
            _ => true,
            zeroReadBackoff: TimeSpan.FromMilliseconds(5));
        session.Closed += ex => closed.TrySetResult(ex);

        session.Open(Target());
        var error = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<IOException>(error);
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public async Task ZeroByteRead_WhenPortClosed_EndsImmediately()
    {
        var port = new FakeSerialPort();
        port.EnqueueZeroRead(closePort: true);

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SerialTerminalSession((_, _) => port, _ => true);
        session.Closed += ex => closed.TrySetResult(ex);

        session.Open(Target());
        var error = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(error);
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public async Task DisconnectThenImmediateReconnect_WaitsForBackgroundClose()
    {
        var tracker = new SerialPortCloseTracker();
        var handleHeld = false;
        var allowFirstDispose = new ManualResetEventSlim(false);
        var firstDisposeStarted = new ManualResetEventSlim(false);
        var firstClosedRaised = false;

        var firstPort = new FakeSerialPort
        {
            OnOpen = () => Volatile.Write(ref handleHeld, true),
            OnDispose = () =>
            {
                firstDisposeStarted.Set();
                allowFirstDispose.Wait(TimeSpan.FromSeconds(2));
                Volatile.Write(ref handleHeld, false);
            },
        };
        var secondPort = new FakeSerialPort
        {
            OnOpen = () =>
            {
                if (Volatile.Read(ref handleHeld))
                    throw new UnauthorizedAccessException("Access to the port 'COM7' is denied.");
                Volatile.Write(ref handleHeld, true);
            },
            OnDispose = () => Volatile.Write(ref handleHeld, false),
        };

        var first = new SerialTerminalSession((_, _) => firstPort, _ => true, closeTracker: tracker);
        first.Closed += _ => firstClosedRaised = true;
        first.Open(Target("COM7"));

        // Dispose must return without waiting for the slow close thread.
        first.Dispose();
        Assert.True(firstDisposeStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(firstClosedRaised);

        using var second = new SerialTerminalSession((_, _) => secondPort, _ => true, closeTracker: tracker);
        var reopenTask = Task.Run(() => second.Open(Target("COM7")));

        // Release the first port's close shortly after reopen starts; reopen should wait and succeed.
        await Task.Delay(50);
        Assert.False(reopenTask.IsCompleted);
        allowFirstDispose.Set();

        await reopenTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(second.IsConnected);
        Assert.False(firstClosedRaised);
    }

    [Fact]
    public void Dispose_UnblocksStalledWriteViaDiscardOutBufferAndSuppressesClosed()
    {
        var writeEntered = new ManualResetEventSlim(false);
        var writeUnblocked = new ManualResetEventSlim(false);
        var closedRaised = false;

        var port = new FakeSerialPort
        {
            OnWrite = _ =>
            {
                writeEntered.Set();
                writeUnblocked.Wait(TimeSpan.FromSeconds(2));
                throw new IOException("aborted by purge");
            },
            OnDiscardOutBuffer = () => writeUnblocked.Set(),
        };

        var session = new SerialTerminalSession((_, _) => port, _ => true);
        session.Closed += _ => closedRaised = true;
        session.Open(Target());
        session.Write(Encoding.UTF8.GetBytes("stalled"));
        Assert.True(writeEntered.Wait(TimeSpan.FromSeconds(2)));

        session.Dispose();
        Assert.True(port.DisposedSignal.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(closedRaised);
    }

    [Fact]
    public void Dispose_DuringBreak_WakesSenderImmediately()
    {
        var port = new FakeSerialPort();

        using var session = new SerialTerminalSession(
            (_, _) => port,
            _ => true,
            breakDuration: TimeSpan.FromSeconds(10));
        session.Open(Target());
        session.SendBreak();

        // Wait until BreakState = true has been applied.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!port.BreakState && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.True(port.BreakState);

        var started = DateTime.UtcNow;
        session.Dispose();
        Assert.True(session.WaitForSenderExit(TimeSpan.FromSeconds(2)));
        Assert.True(port.DisposedSignal.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Close_DrainsDelayedWriteAndPurgesBeforeDisposingPort()
    {
        var writeEntered = new ManualResetEventSlim(false);
        var discardCalls = 0;
        var writeCompleted = false;
        var writeFinishedBeforeDispose = false;
        var discardedAfterWrite = false;

        var port = new FakeSerialPort
        {
            OnWrite = _ =>
            {
                writeEntered.Set();
                // Simulate a write that takes longer than a brief sleep before unwinding.
                Thread.Sleep(60);
                Volatile.Write(ref writeCompleted, true);
            },
            OnDiscardOutBuffer = () =>
            {
                Interlocked.Increment(ref discardCalls);
                if (Volatile.Read(ref writeCompleted))
                    Volatile.Write(ref discardedAfterWrite, true);
            },
            OnDispose = () =>
            {
                writeFinishedBeforeDispose = Volatile.Read(ref writeCompleted);
            },
        };

        var session = new SerialTerminalSession((_, _) => port, _ => true);
        session.Open(Target());
        session.Write(Encoding.UTF8.GetBytes("delayed"));
        Assert.True(writeEntered.Wait(TimeSpan.FromSeconds(2)));

        session.Dispose();
        Assert.True(port.DisposedSignal.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(writeFinishedBeforeDispose);
        Assert.True(discardedAfterWrite);
        Assert.True(Volatile.Read(ref discardCalls) >= 2);
    }

    [Fact]
    public void Open_ClassifiesBusyMissingAndUnsupportedFramingErrors()
    {
        using (var busy = new SerialTerminalSession(
            (_, _) => new FakeSerialPort { OnOpen = () => throw new UnauthorizedAccessException("denied") },
            _ => true))
        {
            var ex = Assert.Throws<SerialSessionException>(() => busy.Open(Target("COM4")));
            Assert.False(ex.PortMissing);
            Assert.Contains("in use", ex.Message);
        }

        using (var missing = new SerialTerminalSession(
            (_, _) => new FakeSerialPort { OnOpen = () => throw new IOException("not found") },
            _ => false))
        {
            var ex = Assert.Throws<SerialSessionException>(() => missing.Open(Target("COM4")));
            Assert.True(ex.PortMissing);
            Assert.Contains("not connected", ex.Message);
        }

        using (var badFraming = new SerialTerminalSession(
            (_, _) => new FakeSerialPort { OnOpen = () => throw new ArgumentException("unsupported") },
            _ => true))
        {
            var ex = Assert.Throws<SerialSessionException>(() => badFraming.Open(Target("COM4")));
            Assert.False(ex.PortMissing);
            Assert.Contains("does not accept", ex.Message);
        }
    }
}
