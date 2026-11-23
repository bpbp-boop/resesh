using System.Buffers;
using System.Globalization;
using System.Text;

namespace Resesh.Core.Recording;

/// <summary>
/// One ordered capture point for terminal output and resize events. The bounded in-memory
/// stream feeds instant rewind; an optional writer consumes the same events for disk recording.
/// Rewind events are grouped into blocks of about 64 KB, and each sealed block and keyframe is
/// Brotli-compressed on the thread pool, so the same cap holds several times more history.
/// The newest block stays raw until it fills or a keyframe arrives.
/// </summary>
public sealed class TerminalCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    // Sealed blocks, oldest first, then the raw events of the block still filling.
    private readonly List<RewindEventBlock> _blocks = [];
    private readonly List<TerminalRecordingEvent> _open = [];
    private long _openBytes;
    private readonly List<RewindKeyframe> _keyframes = [];
    private readonly Queue<RewindChunk> _uncompressed = new();
    private bool _compressing;
    private TimeSpan _maximumAge;
    private long _maximumBytes;
    private readonly bool _retainForRewind;
    private long _eventBytes;
    private long _keyframeBytes;
    private double _latestTime;
    private bool _hasCaptureTimestamp;
    private int _currentColumns;
    private int _currentRows;
    private RewindKeyframe? _anchor;
    private TerminalDiskRecorder? _recorder;
    // A recorder that failed under the lock, waiting to be closed and reported outside it.
    private TerminalDiskRecorder? _failedRecorder;
    private string? _recordingError;
    private bool _disposed;
    private bool _rewindDisabled;

    public TerminalCapture(
        int initialColumns,
        int initialRows,
        DateTimeOffset? startedAt = null,
        TimeSpan? maximumAge = null,
        long maximumBytes = RewindLimits.DefaultMegabytes * RewindLimits.BytesPerMegabyte,
        bool retainForRewind = true)
    {
        if (initialColumns <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialColumns));
        if (initialRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialRows));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        InitialColumns = initialColumns;
        InitialRows = initialRows;
        _currentColumns = initialColumns;
        _currentRows = initialRows;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
        _maximumAge = maximumAge ?? TimeSpan.FromMinutes(RewindLimits.DefaultMinutes);
        _maximumBytes = maximumBytes;
        _retainForRewind = retainForRewind;
    }

    /// <summary>Changes the rewind caps of a live capture, as the settings change. Lower caps
    /// drop the oldest rewind data at once; higher ones keep what is already retained and
    /// let it grow.</summary>
    public void SetLimits(TimeSpan maximumAge, long maximumBytes)
    {
        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        bool changed;
        lock (_gate)
        {
            if (_disposed || (_maximumAge == maximumAge && _maximumBytes == maximumBytes))
                return;
            var wasAvailable = HasRewindDataLocked;
            _maximumAge = maximumAge;
            _maximumBytes = maximumBytes;
            TrimLocked();
            changed = wasAvailable != HasRewindDataLocked;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The caps in effect: (age, bytes).</summary>
    public (TimeSpan MaximumAge, long MaximumBytes) Limits
    {
        get
        {
            lock (_gate)
                return (_maximumAge, _maximumBytes);
        }
    }

    public DateTimeOffset StartedAt { get; }
    public int InitialColumns { get; }
    public int InitialRows { get; }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
                return _recorder is not null;
        }
    }

    public string? RecordingPath
    {
        get
        {
            lock (_gate)
                return _recorder?.Path;
        }
    }

    public event Action? Changed;
    public event Action<bool, string?>? RecordingChanged;

    /// <summary>The disk recording stopped because writing it failed (disk full, file gone).
    /// Arguments are the recording's path and the reason. Raised after
    /// <see cref="RecordingChanged"/> reports the recording stopped, on whichever thread was
    /// writing: the terminal's output thread or a flush timer.</summary>
    public event Action<string, string>? RecordingFailed;

    /// <summary>Why the last recording stopped on its own, or null. Cleared when a new one starts.</summary>
    public string? RecordingError
    {
        get
        {
            lock (_gate)
                return _recordingError;
        }
    }

    /// <summary>Checks availability without building a replay slice on every output chunk.</summary>
    public bool HasRewindData
    {
        get
        {
            lock (_gate)
                return HasRewindDataLocked;
        }
    }

    private bool HasRewindDataLocked =>
        _anchor is not null || _keyframes.Count > 0 || _blocks.Count > 0 || _open.Count > 0;

    public void CaptureOutput(ReadOnlySpan<byte> data, long unixTimeMilliseconds)
    {
        if (data.IsEmpty)
            return;

        var maximumCharacters = Encoding.UTF8.GetMaxCharCount(data.Length);
        var rented = ArrayPool<char>.Shared.Rent(maximumCharacters);
        var appended = false;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_retainForRewind && _recorder is null)
                    return;

                var count = _decoder.GetChars(data, rented.AsSpan(), flush: false);
                if (count > 0)
                {
                    var text = new string(rented, 0, count);
                    appended = AppendLocked(new TerminalRecordingEvent(
                        ToElapsedLocked(unixTimeMilliseconds), "o", text));
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
        ReportRecordingFailure();
        StartCompression();
        if (appended)
            Changed?.Invoke();
    }

    public void CaptureResize(int columns, int rows, long unixTimeMilliseconds)
    {
        if (columns <= 0 || rows <= 0)
            return;

        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            _currentColumns = columns;
            _currentRows = rows;
            changed = AppendLocked(new TerminalRecordingEvent(
                ToElapsedLocked(unixTimeMilliseconds), "r",
                string.Create(CultureInfo.InvariantCulture, $"{columns}x{rows}")));
        }
        ReportRecordingFailure();
        StartCompression();
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Keeps a copy of <paramref name="state"/>: the caller's memory may be freed as soon
    /// as this returns (the terminal surface passes the shim's own buffer).</summary>
    public void CaptureKeyframe(ReadOnlyMemory<byte> state, int columns, int rows, long unixTimeMilliseconds)
    {
        if (state.IsEmpty)
            throw new ArgumentException("A terminal keyframe cannot be empty.", nameof(state));
        if (columns <= 0 || rows <= 0)
            return;

        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            _currentColumns = columns;
            _currentRows = rows;
            if (_retainForRewind && !_rewindDisabled)
            {
                // Seal first: no block may hold events from both sides of a keyframe.
                SealOpenBlockLocked();
                var frame = new RewindKeyframe(
                    ToElapsedLocked(unixTimeMilliseconds), columns, rows, state.ToArray());
                _keyframes.Add(frame);
                _keyframeBytes += frame.Bytes;
                _uncompressed.Enqueue(frame);
                changed = true;
                TrimLocked();
            }
        }
        StartCompression();
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The keyframe at or before <paramref name="atTime"/> and the events after it.
    /// Picks the pieces under the lock and decompresses them outside it, so scrubbing never
    /// holds up terminal output.</summary>
    public TerminalRewindSlice Snapshot(double? atTime = null)
    {
        RewindKeyframe? frame;
        RewindEventBlock[] blocks;
        TerminalRecordingEvent[] open;
        double target, after, earliest, latest;
        lock (_gate)
        {
            ThrowIfDisposed();
            earliest = EarliestTimeLocked();
            latest = _latestTime;
            target = Math.Clamp(atTime ?? _latestTime, earliest, _latestTime);
            frame = _anchor;
            for (var index = _keyframes.Count - 1; index >= 0; index--)
            {
                if (_keyframes[index].Time <= target)
                {
                    frame = _keyframes[index];
                    break;
                }
            }

            after = frame?.Time ?? double.NegativeInfinity;
            var from = after;
            var until = target;
            blocks = _blocks
                .Where(block => block.LastTime > from && block.FirstTime <= until)
                .ToArray();
            open = _open
                .Where(item => item.Time > from && item.Time <= until)
                .ToArray();
        }

        var events = new List<TerminalRecordingEvent>();
        foreach (var block in blocks)
        {
            foreach (var item in block.Events())
            {
                if (item.Time > after && item.Time <= target)
                    events.Add(item);
            }
        }
        events.AddRange(open);
        return new TerminalRewindSlice(
            StartedAt, InitialColumns, InitialRows, earliest, latest, frame?.ToKeyframe(), events);
    }

    /// <summary>Runs compression on the calling thread instead of the thread pool. Tests set it.</summary>
    internal bool CompressInline { get; set; }

    /// <summary>Never starts the compressor, as if the thread pool were starved. Tests set it.</summary>
    internal bool SuspendCompression { get; set; }

    /// <summary>Runs after a chunk is compressed, before the result is applied. Tests set it.</summary>
    internal Action? ChunkCompressed { get; set; }

    /// <summary>Chunks waiting for the compressor.</summary>
    internal int PendingCompressionCount
    {
        get
        {
            lock (_gate)
                return _uncompressed.Count;
        }
    }

    /// <summary>Bytes counted against the cap: raw, or compressed once the compressor is done.</summary>
    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
                return RetainedBytesLocked();
        }
    }

    /// <summary>Starts the compressor if chunks are waiting and it is not already running.
    /// Called outside the lock.</summary>
    private void StartCompression()
    {
        lock (_gate)
        {
            if (_compressing || _disposed || SuspendCompression || _uncompressed.Count == 0)
                return;
            _compressing = true;
        }
        if (CompressInline)
            CompressPending();
        else
            ThreadPool.UnsafeQueueUserWorkItem(static capture => capture.CompressPending(), this, preferLocal: false);
    }

    private void CompressPending()
    {
        var restart = false;
        try
        {
            while (true)
            {
                RewindChunk? chunk;
                lock (_gate)
                {
                    if (_disposed || !_uncompressed.TryDequeue(out chunk))
                    {
                        _compressing = false;
                        return;
                    }
                    if (chunk.Dropped)
                        continue;
                }

                if (chunk.Compress() is not { } compressed)
                    continue;
                ChunkCompressed?.Invoke();

                lock (_gate)
                {
                    if (chunk.Dropped || _disposed)
                        continue;
                    var change = chunk.UseCompressed(compressed);
                    if (chunk is RewindEventBlock)
                        _eventBytes += change;
                    else if (chunk != _anchor)
                        _keyframeBytes += change;
                }
            }
        }
        catch (Exception) when (!CompressInline)
        {
            // Background compression is best-effort: keep the uncompressed chunk intact rather
            // than terminating the process from an unhandled thread-pool exception.
        }
        finally
        {
            lock (_gate)
            {
                if (_compressing)
                {
                    _compressing = false;
                    restart = !_disposed && !SuspendCompression && _uncompressed.Count > 0;
                }
            }
        }
        if (restart && !CompressInline)
            StartCompression();
    }

    /// <summary>Opens the recording files; null opens them on disk. Tests substitute streams that fail.</summary>
    internal Func<string, Stream>? OpenRecordingFile { get; set; }

    public string StartRecording(string directory, string sessionName, string? terminalType = null)
    {
        string path;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_recorder is not null)
                return _recorder.Path;

            var recordingStartedAt = DateTimeOffset.Now;
            var captureOffset = Math.Max(_latestTime,
                (recordingStartedAt.ToUnixTimeMilliseconds() - StartedAt.ToUnixTimeMilliseconds()) / 1000d);
            var paths = TerminalRecordingFiles.CreatePaths(directory, sessionName, recordingStartedAt);
            _recorder = new TerminalDiskRecorder(
                paths, sessionName, terminalType,
                _currentColumns, _currentRows, recordingStartedAt, captureOffset, OnRecorderFailed, OpenRecordingFile);
            _recordingError = null;
            path = paths.CastPath;
        }
        RecordingChanged?.Invoke(true, path);
        return path;
    }

    /// <summary>Stops and closes the recording. Throws when the final flush fails; the
    /// recording has stopped either way.</summary>
    public string? StopRecording()
    {
        TerminalDiskRecorder? recorder;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder is null)
            return null;
        var path = recorder.Path;
        try
        {
            recorder.Dispose();
        }
        finally
        {
            RecordingChanged?.Invoke(false, path);
        }
        return path;
    }

    private bool AppendLocked(TerminalRecordingEvent item)
    {
        _latestTime = Math.Max(_latestTime, item.Time);
        if (_recorder is { } recorder)
        {
            try
            {
                recorder.Write(item);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Recording is a side channel: it must not take the terminal's output path (and
                // rewind) down with it, nor fail again on every later chunk.
                FailRecordingLocked(recorder, exception);
            }
        }
        if (!_retainForRewind || _rewindDisabled)
            return false;

        var bytes = EventBytes(item);
        _open.Add(item);
        _openBytes += bytes;
        _eventBytes += bytes;
        if (_openBytes >= BlockBytes)
            SealOpenBlockLocked();
        TrimLocked();
        return true;
    }

    /// <summary>Raw event bytes per block. Larger blocks compress a little better but leave
    /// more raw output in the open block and cost more to decompress on every seek.</summary>
    private const long BlockBytes = 64 * 1024;

    private void SealOpenBlockLocked()
    {
        if (_open.Count == 0)
            return;
        var block = new RewindEventBlock([.. _open], _openBytes);
        _blocks.Add(block);
        _uncompressed.Enqueue(block);
        _open.Clear();
        _openBytes = 0;
    }

    private void FailRecordingLocked(TerminalDiskRecorder recorder, Exception exception)
    {
        if (_recorder != recorder)
            return;
        _recorder = null;
        _failedRecorder = recorder;
        _recordingError = $"Recording stopped: {exception.Message}";
    }

    /// <summary>A background flush failed while no output was arriving.</summary>
    private void OnRecorderFailed(TerminalDiskRecorder recorder, Exception exception)
    {
        lock (_gate)
            FailRecordingLocked(recorder, exception);
        ReportRecordingFailure();
    }

    /// <summary>Closes a recorder that failed under the lock and tells listeners, outside it.</summary>
    private void ReportRecordingFailure()
    {
        TerminalDiskRecorder? recorder;
        string? message;
        lock (_gate)
        {
            recorder = _failedRecorder;
            _failedRecorder = null;
            message = _recordingError;
        }
        if (recorder is null)
            return;
        CloseQuietly(recorder);
        RecordingChanged?.Invoke(false, recorder.Path);
        RecordingFailed?.Invoke(recorder.Path, message ?? "Recording stopped.");
    }

    /// <summary>Closing flushes what is buffered, which fails again on a full disk; the file
    /// handles are released regardless, and the failure has already been reported.</summary>
    private static void CloseQuietly(TerminalDiskRecorder recorder)
    {
        try
        {
            recorder.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private double ToElapsedLocked(long unixTimeMilliseconds)
    {
        var elapsed = Math.Max(
            0,
            (unixTimeMilliseconds - StartedAt.ToUnixTimeMilliseconds()) / 1000d);
        if (_hasCaptureTimestamp && elapsed <= _latestTime)
            elapsed = Math.BitIncrement(_latestTime);
        _latestTime = elapsed;
        _hasCaptureTimestamp = true;
        return _latestTime;
    }

    private void TrimLocked()
    {
        var cutoff = _latestTime - _maximumAge.TotalSeconds;
        RewindKeyframe? ageFrame = null;
        foreach (var frame in _keyframes)
        {
            if (frame.Time > cutoff)
                break;
            ageFrame = frame;
        }
        if (ageFrame is not null)
            PromoteAnchorLocked(ageFrame);

        while (RetainedBytesLocked() > _maximumBytes)
        {
            if (_keyframes.Count > 0)
            {
                PromoteAnchorLocked(_keyframes[0]);
                continue;
            }

            DisableRewindLocked();
            break;
        }

        // Trimmed chunks no longer count against the cap, so they must not wait in the queue
        // holding raw memory either, however far behind the compressor is. Chunks are queued
        // oldest first and trimmed oldest first, so the trimmed ones are at the front.
        while (_uncompressed.TryPeek(out var pending) && pending.Dropped)
            _uncompressed.Dequeue();
    }

    private long RetainedBytesLocked() =>
        _eventBytes + _keyframeBytes + (_anchor?.Bytes ?? 0);

    private void DisableRewindLocked()
    {
        // Every retained chunk, not only the queued ones: the compressor may be working on one
        // and must not add its size back to counters reset below.
        foreach (var block in _blocks)
            block.Dropped = true;
        foreach (var frame in _keyframes)
            frame.Dropped = true;
        _anchor?.Dropped = true;
        _uncompressed.Clear();
        _blocks.Clear();
        _open.Clear();
        _openBytes = 0;
        _keyframes.Clear();
        _eventBytes = 0;
        _keyframeBytes = 0;
        _anchor = null;
        _rewindDisabled = true;
    }

    private void PromoteAnchorLocked(RewindKeyframe frame)
    {
        if (_anchor is { } previous && previous != frame)
            previous.Dropped = true;
        _anchor = frame;

        var blocks = 0;
        while (blocks < _blocks.Count && _blocks[blocks].LastTime <= frame.Time)
        {
            _eventBytes -= _blocks[blocks].Bytes;
            _blocks[blocks].Dropped = true;
            blocks++;
        }
        _blocks.RemoveRange(0, blocks);

        // Each keyframe seals the open block, so its events all follow the newest keyframe.
        var events = 0;
        while (events < _open.Count && _open[events].Time <= frame.Time)
        {
            var bytes = EventBytes(_open[events]);
            _openBytes -= bytes;
            _eventBytes -= bytes;
            events++;
        }
        _open.RemoveRange(0, events);

        while (_keyframes.Count > 0 && _keyframes[0].Time <= frame.Time)
        {
            _keyframeBytes -= _keyframes[0].Bytes;
            if (_keyframes[0] != frame)
                _keyframes[0].Dropped = true;
            _keyframes.RemoveAt(0);
        }
    }

    private double EarliestTimeLocked() =>
        _anchor?.Time
        ?? (_blocks.Count > 0 ? _blocks[0].FirstTime : _open.Count > 0 ? _open[0].Time : 0);

    private static long EventBytes(TerminalRecordingEvent item) =>
        32L + Encoding.UTF8.GetByteCount(item.Data);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        TerminalDiskRecorder? recorder;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            recorder = _recorder ?? _failedRecorder;
            _recorder = null;
            _failedRecorder = null;
        }
        // Dispose runs as the tab closes; a final flush that fails must not take it down.
        if (recorder is not null)
            CloseQuietly(recorder);
    }
}
