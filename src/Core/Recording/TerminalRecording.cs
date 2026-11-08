using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resesh.Core.Recording;


public sealed record TerminalRecordingEvent(double Time, string Type, string Data)
{
    public bool IsOutput => Type == "o";
    public bool IsResize => Type == "r";
}

public sealed record TerminalKeyframe(double Time, int Columns, int Rows, ReadOnlyMemory<byte> State);

public sealed record TerminalRewindSlice(
    DateTimeOffset StartedAt,
    int InitialColumns,
    int InitialRows,
    double EarliestTime,
    double LatestTime,
    TerminalKeyframe? Keyframe,
    IReadOnlyList<TerminalRecordingEvent> Events);

public sealed record TerminalRecording(
    DateTimeOffset StartedAt,
    int Width,
    int Height,
    string? Title,
    IReadOnlyList<TerminalRecordingEvent> Events)
{
    public double Duration => Events.Count == 0 ? 0 : Events[^1].Time;
}

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

public sealed record TerminalRecordingPaths(string CastPath, string LogPath);

/// <summary>
/// The rewind caps' defaults and ranges. Settings defaults, the Settings page's ranges and
/// the capture all read them from here.
/// </summary>
public static class RewindLimits
{
    public const int DefaultMinutes = 30;
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 24 * 60;
    public const int DefaultMegabytes = 32;
    public const int MinimumMegabytes = 1;
    public const int MaximumMegabytes = 1024;
    public const long BytesPerMegabyte = 1024L * 1024;

    /// <summary>The age cap for a setting, clamped to the range.</summary>
    public static TimeSpan Age(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinimumMinutes, MaximumMinutes));

    /// <summary>The memory cap for a setting, clamped to the range.</summary>
    public static long Bytes(int megabytes) =>
        Math.Clamp(megabytes, MinimumMegabytes, MaximumMegabytes) * BytesPerMegabyte;
}

public static class TerminalRecordingFiles
{
    /// <summary>
    /// The folder a recording-directory setting names: environment variables expanded
    /// ("%USERPROFILE%\casts") and made absolute. The recorder writes there and the
    /// Recordings list and "Open Recordings Location" read there, so both see one folder.
    /// </summary>
    public static string ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A recording directory is required.", nameof(directory));
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));
    }

    public static TerminalRecordingPaths CreatePaths(
        string directory,
        string sessionName,
        DateTimeOffset timestamp)
    {
        directory = ResolveDirectory(directory);
        Directory.CreateDirectory(directory);
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(sessionName.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "session";
        var stem = $"{safeName}-{timestamp:yyyyMMdd-HHmmss}";
        var suffix = 1;
        while (true)
        {
            var numberedStem = suffix == 1 ? stem : $"{stem}-{suffix}";
            var castPath = Path.Combine(directory, numberedStem + ".cast");
            var logPath = Path.Combine(directory, numberedStem + ".log");
            if (!File.Exists(castPath) && !File.Exists(logPath))
                return new TerminalRecordingPaths(castPath, logPath);
            suffix++;
        }
    }
}

internal sealed class TerminalDiskRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Output arrives in many small chunks; a write per chunk cost seconds of the reader thread
    // under a flood. Files are flushed at most every 250 ms, and a timer flushes the tail, so a
    // killed process loses at most that much.
    private const int FlushIntervalMs = 250;

    private static readonly byte[] NewLine = Encoding.UTF8.GetBytes(Environment.NewLine);

    private readonly Stream _cast;
    private readonly ArrayBufferWriter<byte> _castLine = new(4096);
    private readonly Utf8JsonWriter _castJson;
    private readonly StreamWriter _plainWriter;
    private readonly AnsiPlainTextRenderer _plainRenderer = new();
    private readonly DateTimeOffset _startedAt;
    private readonly double _captureOffset;
    private readonly object _gate = new();
    private readonly Timer _flushTimer;
    private long _lastFlushMs;
    private bool _flushScheduled;
    private bool _disposed;
    private Exception? _failure;
    private readonly Action<TerminalDiskRecorder, Exception>? _failed;

    /// <param name="failed">Called, outside the recorder's lock, when a background flush
    /// fails, since no caller is there to catch it.</param>
    /// <param name="open">Opens each file; tests substitute streams that fail.</param>
    public TerminalDiskRecorder(
        TerminalRecordingPaths paths,
        string title,
        string? terminalType,
        int width,
        int height,
        DateTimeOffset startedAt,
        double captureOffset,
        Action<TerminalDiskRecorder, Exception>? failed = null,
        Func<string, Stream>? open = null)
    {
        Path = paths.CastPath;
        PlainPath = paths.LogPath;
        _startedAt = startedAt;
        _captureOffset = captureOffset;
        _failed = failed;

        Stream? cast = null;
        Stream? plain = null;
        try
        {
            cast = (open ?? OpenFile)(paths.CastPath);
            plain = (open ?? OpenFile)(paths.LogPath);
            _plainWriter = new StreamWriter(plain, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024);
            _cast = cast;

            var header = new JsonObject
            {
                ["version"] = 2,
                ["width"] = width,
                ["height"] = height,
                ["timestamp"] = startedAt.ToUnixTimeSeconds(),
                ["title"] = title,
                ["env"] = new JsonObject
                {
                    ["TERM"] = string.IsNullOrWhiteSpace(terminalType) ? "xterm-256color" : terminalType,
                    ["SHELL"] = "resesh",
                },
            };
            _cast.Write(Encoding.UTF8.GetBytes(header.ToJsonString(JsonOptions)));
            _cast.Write(NewLine);
            _cast.Flush();
            _castJson = new Utf8JsonWriter(_castLine, new JsonWriterOptions { Encoder = JsonOptions.Encoder });
            _lastFlushMs = Environment.TickCount64;
            _flushTimer = new Timer(_ => FlushScheduled());
        }
        catch
        {
            _castJson?.Dispose();
            try
            {
                cast?.Dispose();
            }
            finally
            {
                plain?.Dispose();
                if (cast is not null)
                    TryDelete(paths.CastPath);
                if (plain is not null)
                    TryDelete(paths.LogPath);
            }
            throw;
        }
    }

    public string Path { get; }
    public string PlainPath { get; }

    /// <summary>Writes one event. Throws <see cref="IOException"/> when the disk refuses it, and
    /// again on every later call: a half-written line must not be followed by more.</summary>
    public void Write(TerminalRecordingEvent item)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_failure is not null)
                throw new IOException(_failure.Message, _failure);
            try
            {
                WriteLocked(item);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failure = exception;
                throw;
            }
        }
    }

    private void WriteLocked(TerminalRecordingEvent item)
    {
        var relativeTime = Math.Max(0, item.Time - _captureOffset);
        // [time, type, data], as JsonSerializer writes it, without the intermediate string
        _castLine.ResetWrittenCount();
        _castJson.Reset(_castLine);
        _castJson.WriteStartArray();
        _castJson.WriteNumberValue(relativeTime);
        _castJson.WriteStringValue(item.Type);
        _castJson.WriteStringValue(item.Data);
        _castJson.WriteEndArray();
        _castJson.Flush();
        _cast.Write(_castLine.WrittenSpan);
        _cast.Write(NewLine);
        if (item.IsOutput)
        {
            var eventTime = _startedAt.AddSeconds(relativeTime);
            foreach (var line in _plainRenderer.Feed(item.Data, eventTime))
                WritePlainLine(line);
        }

        var sinceFlush = Environment.TickCount64 - _lastFlushMs;
        if (sinceFlush >= FlushIntervalMs)
        {
            FlushLocked();
        }
        else if (!_flushScheduled)
        {
            _flushScheduled = true;
            _flushTimer.Change(FlushIntervalMs - sinceFlush, Timeout.Infinite);
        }
    }

    private void FlushScheduled()
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_disposed || !_flushScheduled || _failure is not null)
                return;
            try
            {
                FlushLocked();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Output may have stopped, so no later write would notice: report it now.
                _failure = failure = exception;
            }
        }
        if (failure is not null)
            _failed?.Invoke(this, failure);
    }

    private void FlushLocked()
    {
        _flushScheduled = false;
        _lastFlushMs = Environment.TickCount64;
        _cast.Flush();
        _plainWriter.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _flushTimer.Dispose();
            try
            {
                foreach (var line in _plainRenderer.Flush())
                    WritePlainLine(line);
            }
            finally
            {
                try
                {
                    _castJson.Dispose();
                    _cast.Dispose();
                }
                finally
                {
                    _plainWriter.Dispose();
                }
            }
        }
    }

    private void WritePlainLine(TimestampedPlainLine line)
    {
        if (line.Text.Length == 0)
        {
            _plainWriter.WriteLine();
            return;
        }
        _plainWriter.Write('[');
        _plainWriter.Write(line.Timestamp.ToString(
            "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        _plainWriter.Write("] ");
        _plainWriter.WriteLine(line.Text);
    }

    private static Stream OpenFile(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the original open failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original open failure.
        }
    }
}

public static class AsciicastReader
{
    public static TerminalRecording Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("The cast file is empty.");
        using var header = ParseJson(headerLine, "header");
        var root = header.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.GetInt32() != 2)
        {
            throw new InvalidDataException("Only asciicast v2 files are supported.");
        }

        var width = PositiveInt(root, "width", 80);
        var height = PositiveInt(root, "height", 24);
        var timestamp = root.TryGetProperty("timestamp", out var timestampElement) && timestampElement.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : File.GetCreationTimeUtc(path);
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var events = new List<TerminalRecordingEvent>();
        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = ParseJson(line, $"event at line {lineNumber}");
            var item = document.RootElement;
            var type = item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 3
                ? item[1].GetString()
                : null;
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3 ||
                !item[0].TryGetDouble(out var time) || !double.IsFinite(time) || type is not ("o" or "r"))
            {
                throw new InvalidDataException($"Invalid asciicast event at line {lineNumber}.");
            }
            var data = item[2].GetString() ?? throw new InvalidDataException($"Invalid event data at line {lineNumber}.");
            if (time < 0 || (events.Count > 0 && time < events[^1].Time))
                throw new InvalidDataException($"Event times must be ordered at line {lineNumber}.");
            if (type == "r" && !TryParseSize(data, out _, out _))
                throw new InvalidDataException($"Invalid resize event at line {lineNumber}.");
            events.Add(new TerminalRecordingEvent(time, type, data));
        }
        return new TerminalRecording(timestamp, width, height, title, events);
    }

    public static bool TryParseSize(string value, out int columns, out int rows)
    {
        columns = 0;
        rows = 0;
        var separator = value.IndexOf('x');
        return separator > 0 && separator < value.Length - 1 &&
            int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out columns) &&
            int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out rows) &&
            columns > 0 && rows > 0;
    }

    private static JsonDocument ParseJson(string value, string description)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid asciicast {description}.", exception);
        }
    }

    private static int PositiveInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var element) && element.TryGetInt32(out var value) && value > 0
            ? value
            : fallback;
}

internal sealed record TimestampedPlainLine(DateTimeOffset Timestamp, string Text);

/// <summary>
/// Reduces a VT output stream to committed text lines. Unlike escape-code stripping, this
/// applies cursor movement and overwrites, so shells such as PSReadLine do not append every
/// intermediate command-line redraw to the log.
/// </summary>
internal sealed class AnsiPlainTextRenderer
{
    // Cursor controls are untrusted output, not requests to allocate multi-gigabyte rows.
    private const int MaximumCursorPosition = ushort.MaxValue;

    // Rows waiting for a newline are held in memory. Output that scatters text over thousands
    // of rows (cursor addressing up to row 65535) or pads rows out to column 65535 could hold
    // gigabytes; these caps keep the worst case near 16 MB. Real screens stay well inside them.
    private const int MaximumPendingRows = 1024;
    private const int MaximumRowLength = 8192;

    private readonly Dictionary<int, PlainRow> _rows = [];
    private readonly StringBuilder _csi = new();
    private AnsiState _state;
    private AnsiState _stringState;
    private int _row;
    private int _column;
    private int _savedRow;
    private int _savedColumn;
    private bool _sawAbsoluteCursor;
    private DateTimeOffset _lastTimestamp;
    private List<TimestampedPlainLine> _completed = [];

    public IReadOnlyList<TimestampedPlainLine> Feed(string value, DateTimeOffset timestamp)
    {
        _lastTimestamp = timestamp;
        var completed = _completed = new List<TimestampedPlainLine>();
        foreach (var character in value)
        {
            switch (_state)
            {
                case AnsiState.Text:
                    ReadText(character, timestamp, completed);
                    break;
                case AnsiState.Escape:
                    ReadEscape(character, timestamp, completed);
                    break;
                case AnsiState.Csi:
                    if (character is >= '@' and <= '~')
                    {
                        ApplyCsi(character, timestamp, completed);
                        _csi.Clear();
                        _state = AnsiState.Text;
                    }
                    else if (_csi.Length < 1024)
                    {
                        _csi.Append(character);
                    }
                    else
                    {
                        _csi.Clear();
                        _state = AnsiState.IgnoreCsi;
                    }
                    break;
                case AnsiState.IgnoreCsi:
                    if (character is >= '@' and <= '~')
                        _state = AnsiState.Text;
                    break;
                case AnsiState.Osc:
                case AnsiState.ControlString:
                    if (character == '\a' && _state == AnsiState.Osc)
                        _state = AnsiState.Text;
                    else if (character == '\x1b')
                    {
                        _stringState = _state;
                        _state = AnsiState.StringEscape;
                    }
                    break;
                case AnsiState.StringEscape:
                    _state = character == '\\' ? AnsiState.Text : _stringState;
                    break;
            }
        }
        return completed;
    }

    public IReadOnlyList<TimestampedPlainLine> Flush()
    {
        var completed = new List<TimestampedPlainLine>();
        foreach (var row in _rows.Keys.Order().ToArray())
            CommitRow(row, _lastTimestamp, completed);
        return completed;
    }

    private void ReadText(
        char character,
        DateTimeOffset timestamp,
        List<TimestampedPlainLine> completed)
    {
        switch (character)
        {
            case '\x1b':
                _state = AnsiState.Escape;
                break;
            case '\u009b':
                _csi.Clear();
                _state = AnsiState.Csi;
                break;
            case '\u009d':
                _state = AnsiState.Osc;
                break;
            case '\u0090':
                _state = AnsiState.ControlString;
                break;
            case '\b':
                _column = Math.Max(0, _column - 1);
                break;
            case '\r':
                _column = 0;
                break;
            case '\n':
                CommitRow(_row, timestamp, completed);
                NextRow();
                break;
            case '\t':
                do
                {
                    WriteCharacter(' ', timestamp);
                }
                while (_column % 8 != 0);
                break;
            default:
                if (character >= ' ')
                    WriteCharacter(character, timestamp);
                break;
        }
    }

    private void ReadEscape(
        char character,
        DateTimeOffset timestamp,
        List<TimestampedPlainLine> completed)
    {
        switch (character)
        {
            case '[':
                _csi.Clear();
                _state = AnsiState.Csi;
                return;
            case ']':
                _state = AnsiState.Osc;
                return;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = AnsiState.ControlString;
                return;
            case '7':
                _savedRow = _row;
                _savedColumn = _column;
                break;
            case '8':
                _row = _savedRow;
                _column = _savedColumn;
                break;
            case 'D':
                CommitRow(_row, timestamp, completed);
                NextRow();
                break;
            case 'E':
                CommitRow(_row, timestamp, completed);
                NextRow();
                _column = 0;
                break;
            case 'M':
                _row = Math.Max(0, _row - 1);
                break;
            case 'c':
                _rows.Clear();
                _row = 0;
                _column = 0;
                break;
        }
        _state = AnsiState.Text;
    }

    private void ApplyCsi(
        char command,
        DateTimeOffset timestamp,
        List<TimestampedPlainLine> completed)
    {
        switch (command)
        {
            case 'H':
            case 'f':
            {
                var row = Parameter(0, 1) - 1;
                var column = Parameter(1, 1) - 1;
                if (!_sawAbsoluteCursor)
                {
                    _rows.Remove(Math.Max(0, row));
                    _sawAbsoluteCursor = true;
                }
                _row = Math.Max(0, row);
                _column = Math.Max(0, column);
                break;
            }
            case 'G':
            case '`':
                _column = Math.Max(0, Parameter(0, 1) - 1);
                break;
            case 'A':
                _row = Math.Max(0, _row - Parameter(0, 1));
                break;
            case 'B':
            case 'e':
                _row = (int)Math.Min(MaximumCursorPosition, (long)_row + Parameter(0, 1));
                break;
            case 'C':
            case 'a':
                _column = (int)Math.Min(MaximumCursorPosition, (long)_column + Parameter(0, 1));
                break;
            case 'D':
                _column = Math.Max(0, _column - Parameter(0, 1));
                break;
            case 'E':
                _row = (int)Math.Min(MaximumCursorPosition, (long)_row + Parameter(0, 1));
                _column = 0;
                break;
            case 'F':
                _row = Math.Max(0, _row - Parameter(0, 1));
                _column = 0;
                break;
            case 'J':
                if (Parameter(0, 0) is 2 or 3)
                    _rows.Clear();
                break;
            case 'K':
                EraseLine(Parameter(0, 0));
                break;
            case 'P':
                DeleteCharacters(Parameter(0, 1));
                break;
            case '@':
                InsertSpaces(Parameter(0, 1));
                break;
            case 'X':
                EraseCharacters(Parameter(0, 1));
                break;
            case 's':
                _savedRow = _row;
                _savedColumn = _column;
                break;
            case 'u':
                _row = _savedRow;
                _column = _savedColumn;
                break;
            case 'd':
                _row = Math.Max(0, Parameter(0, 1) - 1);
                break;
            case 'n':
            case 'm':
            case 'h':
            case 'l':
                break;
        }
    }

    private int Parameter(int index, int defaultValue)
    {
        var text = _csi.ToString().TrimStart('?', '>', '!');
        var values = text.Split(';');
        if (index >= values.Length ||
            !int.TryParse(values[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value == 0)
        {
            return defaultValue;
        }
        return Math.Min(value, MaximumCursorPosition);
    }

    /// <summary>Moves down a row. Line feeds have no bottom margin here, so clamp rather than
    /// count towards overflow over a very long recording.</summary>
    private void NextRow() => _row = Math.Min(MaximumCursorPosition, _row + 1);

    private void WriteCharacter(char character, DateTimeOffset timestamp)
    {
        if (_column >= MaximumRowLength)
        {
            // Past the end of what is kept, the text is dropped. The cursor still moves, up to a
            // multiple of 8 so a tab's loop to the next stop always ends.
            _column = Math.Min(_column + 1, MaximumCursorPosition + 1);
            return;
        }
        var row = GetRow();
        while (row.Text.Length < _column)
            row.Text.Append(' ');
        if (_column < row.Text.Length)
            row.Text[_column] = character;
        else
            row.Text.Append(character);
        if (!char.IsWhiteSpace(character) && row.Timestamp is null)
            row.Timestamp = timestamp;
        _column++;
    }

    private PlainRow GetRow()
    {
        if (!_rows.TryGetValue(_row, out var row))
        {
            // Too many rows waiting: the topmost is written out as it stands.
            if (_rows.Count >= MaximumPendingRows)
                CommitRow(_rows.Keys.Min(), _lastTimestamp, _completed);
            row = new PlainRow();
            _rows.Add(_row, row);
        }
        return row;
    }

    private void CommitRow(
        int rowNumber,
        DateTimeOffset fallbackTimestamp,
        List<TimestampedPlainLine> completed)
    {
        if (!_rows.Remove(rowNumber, out var row))
        {
            completed.Add(new TimestampedPlainLine(fallbackTimestamp, ""));
            return;
        }
        completed.Add(new TimestampedPlainLine(
            row.Timestamp ?? fallbackTimestamp,
            row.Text.ToString().Trim()));
    }

    private void EraseLine(int mode)
    {
        if (!_rows.TryGetValue(_row, out var row))
            return;
        switch (mode)
        {
            case 0:
                if (_column < row.Text.Length)
                    row.Text.Length = _column;
                break;
            case 1:
                for (var index = 0; index <= _column && index < row.Text.Length; index++)
                    row.Text[index] = ' ';
                break;
            case 2:
                row.Text.Clear();
                row.Timestamp = null;
                break;
        }
    }

    private void DeleteCharacters(int count)
    {
        if (_rows.TryGetValue(_row, out var row) && _column < row.Text.Length)
            row.Text.Remove(_column, Math.Min(count, row.Text.Length - _column));
    }

    private void InsertSpaces(int count)
    {
        var row = GetRow();
        count = Math.Min(count, Math.Max(0, MaximumRowLength - Math.Max(_column, row.Text.Length)));
        if (count == 0)
            return;
        while (row.Text.Length < _column)
            row.Text.Append(' ');
        row.Text.Insert(_column, new string(' ', count));
    }

    private void EraseCharacters(int count)
    {
        count = Math.Min(count, Math.Max(0, MaximumRowLength - _column));
        if (count == 0)
            return;
        var row = GetRow();
        while (row.Text.Length < _column + count)
            row.Text.Append(' ');
        for (var index = _column; index < _column + count; index++)
            row.Text[index] = ' ';
    }

    private sealed class PlainRow
    {
        public StringBuilder Text { get; } = new();
        public DateTimeOffset? Timestamp { get; set; }
    }

    private enum AnsiState
    {
        Text,
        Escape,
        Csi,
        IgnoreCsi,
        Osc,
        ControlString,
        StringEscape,
    }
}
