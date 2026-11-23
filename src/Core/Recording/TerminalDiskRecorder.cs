using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resesh.Core.Recording;

internal sealed record TimestampedPlainLine(DateTimeOffset Timestamp, string Text);

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
