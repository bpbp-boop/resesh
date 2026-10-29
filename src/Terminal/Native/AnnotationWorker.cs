namespace Resesh.Terminal.Native;

/// <summary>
/// The annotation work that reads the terminal line by line: stamping output times
/// (<see cref="LineTimes"/>) and indexing the ruler's highlight lane (<see cref="OverviewIndex"/>).
/// It runs on the thread pool, one pass at a time over its own buffer, because each terminal read
/// can wait for the parser: on the UI thread a pass held input for up to ~20 ms in a flood. The UI
/// asks for passes with <see cref="Request"/> and reads <see cref="Ticks"/>; <c>published</c> is
/// called on the worker when the ticks change.
/// </summary>
internal sealed class AnnotationWorker
{
    // Before more scrollback is indexed. Every pass competes with the parser for the terminal
    // lock, so passes keep the pace the UI thread kept when it ran them.
    private const int ContinueMilliseconds = 100;

    private readonly object _gate = new(); // held for a whole pass
    private readonly ICommandBuffer _buffer;
    private readonly Action? _refresh;
    private readonly Action? _detach;
    private readonly Highlighter _highlighter;
    private readonly Action _published;
    private readonly Action<string>? _trace;
    private readonly OverviewIndex _overview;
    private readonly LineTimes _lineTimes;
    private readonly Timer _continue;

    private int _state; // 0 idle, 1 a pass queued or running, 2 another pass asked for meanwhile
    private volatile bool _disposed;
    private bool _cleanedUp;
    private volatile bool _resetOverview;
    private long _observedMs; // 0: no line times this pass
    private IReadOnlyList<(int Line, uint Color)> _ticks = [];

    /// <param name="refresh">Clears the buffer's per-pass snapshot before each pass.</param>
    /// <param name="detach">Releases the buffer's markers once the worker is done with it.</param>
    public AnnotationWorker(ICommandBuffer buffer, Highlighter highlighter, Action published,
        Action? refresh = null, Action? detach = null, TimeSpan? sliceBudget = null, Action<string>? trace = null)
    {
        _trace = trace;
        _buffer = buffer;
        _refresh = refresh;
        _detach = detach;
        _highlighter = highlighter;
        _published = published;
        _overview = new OverviewIndex(buffer, highlighter, sliceBudget);
        _lineTimes = new LineTimes(buffer);
        _continue = new Timer(_ => Schedule());
    }

    /// <summary>Highlight-lane ticks from the last pass: each indexed line in the color of its
    /// last matching overview rule.</summary>
    public IReadOnlyList<(int Line, uint Color)> Ticks => Volatile.Read(ref _ticks);

    /// <summary>Asks for a pass soon. <paramref name="observedMs"/> stamps the lines output reached
    /// with that time; 0 leaves line times alone (a replay is writing the terminal).</summary>
    public void Request(long observedMs)
    {
        Interlocked.Exchange(ref _observedMs, observedMs);
        Schedule();
    }

    /// <summary>The highlight lane starts over (rules changed, or a reflow moved every line).</summary>
    public void ResetOverview()
    {
        _resetOverview = true;
        Schedule();
    }

    /// <summary>When output reached <paramref name="line"/>, for the ruler's tooltips. Gives up
    /// rather than hold the UI thread behind a long pass.</summary>
    public long? TimeOf(int line)
    {
        if (!Monitor.TryEnter(_gate, 5))
            return null;
        try
        {
            if (_disposed)
                return null;
            _refresh?.Invoke();
            return _lineTimes.TimeOf(line);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Stops passes and releases markers. With <paramref name="wait"/>, returns only once
    /// no pass is reading the terminal (it is about to be freed); otherwise a running pass cleans
    /// up when it ends.</summary>
    public void Dispose(bool wait)
    {
        _disposed = true;
        _continue.Dispose();
        if (wait)
            Monitor.Enter(_gate);
        else if (!Monitor.TryEnter(_gate))
            return;
        try
        {
            CleanUpLocked();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void Schedule()
    {
        if (_disposed)
            return;
        // Idle: start a pass. Busy: make sure the running one goes round again.
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(_ => Run(), null);
        else
            Interlocked.Exchange(ref _state, 2);
    }

    private void Run()
    {
        while (true)
        {
            bool more;
            lock (_gate)
            {
                if (_disposed)
                {
                    CleanUpLocked();
                    return;
                }
                try
                {
                    more = PassLocked();
                }
                catch (Exception exception)
                {
                    _trace?.Invoke($"annotation pass failed: {exception.Message}");
                    more = false;
                }
            }
            if (more && !_disposed)
                _continue.Change(ContinueMilliseconds, Timeout.Infinite);
            // Another request arrived during the pass: go again. Otherwise go idle, unless one
            // arrives between the check and the exchange.
            if (Interlocked.CompareExchange(ref _state, 1, 2) == 2)
                continue;
            if (Interlocked.CompareExchange(ref _state, 0, 1) == 1)
                return;
        }
    }

    private bool PassLocked()
    {
        _refresh?.Invoke();
        if (_resetOverview)
        {
            _resetOverview = false;
            _overview.Reset();
        }
        var observedMs = Interlocked.Exchange(ref _observedMs, 0);
        if (observedMs > 0)
            _lineTimes.Note(observedMs);
        var more = _overview.Advance();
        var ticks = new List<(int Line, uint Color)>();
        var rules = _highlighter.Rules;
        foreach (var (line, mask) in _overview.Lines())
        {
            var index = 31 - System.Numerics.BitOperations.LeadingZeroCount(mask); // the last rule wins, as in terminal.html
            if (index < rules.Count)
                ticks.Add((line, rules[index].Color));
        }
        if (!ticks.SequenceEqual(_ticks))
        {
            Volatile.Write(ref _ticks, ticks);
            _published();
        }
        return more;
    }

    private void CleanUpLocked()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        _lineTimes.Reset();
        _overview.Reset();
        _detach?.Invoke();
    }
}
