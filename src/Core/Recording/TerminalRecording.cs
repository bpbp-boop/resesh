using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Resesh.Core.Recording;


public sealed record TerminalRecordingEvent(double Time, string Type, string Data)
{
    public bool IsOutput => Type == "o";
    public bool IsResize => Type == "r";
}

public sealed record TerminalKeyframe(double Time, int Columns, int Rows, string State);

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
/// </summary>
public sealed class TerminalCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly LinkedList<TerminalRecordingEvent> _events = [];
    private readonly List<TerminalKeyframe> _keyframes = [];
    private readonly TimeSpan _maximumAge;
    private readonly long _maximumBytes;
    private long _eventBytes;
    private long _keyframeBytes;
    private double _latestTime;
    private int _currentColumns;
    private int _currentRows;
    private TerminalKeyframe? _anchor;
    private TerminalDiskRecorder? _recorder;
    private bool _disposed;

    public TerminalCapture(
        int initialColumns,
        int initialRows,
        DateTimeOffset? startedAt = null,
        TimeSpan? maximumAge = null,
        long maximumBytes = 32L * 1024 * 1024)
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
        _maximumAge = maximumAge ?? TimeSpan.FromMinutes(30);
        _maximumBytes = maximumBytes;
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
                var count = _decoder.GetChars(data, rented.AsSpan(), flush: false);
                if (count > 0)
                {
                    var text = new string(rented, 0, count);
                    AppendLocked(new TerminalRecordingEvent(ToElapsedLocked(unixTimeMilliseconds), "o", text));
                    appended = true;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
        if (appended)
            Changed?.Invoke();
    }

    public void CaptureResize(int columns, int rows, long unixTimeMilliseconds)
    {
        if (columns <= 0 || rows <= 0)
            return;

        lock (_gate)
        {
            ThrowIfDisposed();
            _currentColumns = columns;
            _currentRows = rows;
            AppendLocked(new TerminalRecordingEvent(
                ToElapsedLocked(unixTimeMilliseconds), "r",
                string.Create(CultureInfo.InvariantCulture, $"{columns}x{rows}")));
        }
        Changed?.Invoke();
    }

    public void CaptureKeyframe(string state, int columns, int rows, long unixTimeMilliseconds)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (columns <= 0 || rows <= 0)
            return;

        lock (_gate)
        {
            ThrowIfDisposed();
            _currentColumns = columns;
            _currentRows = rows;
            var frame = new TerminalKeyframe(ToElapsedLocked(unixTimeMilliseconds), columns, rows, state);
            _keyframes.Add(frame);
            _keyframeBytes += FrameBytes(frame);
            TrimLocked();
        }
        Changed?.Invoke();
    }

    public TerminalRewindSlice Snapshot(double? atTime = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var target = Math.Clamp(atTime ?? _latestTime, EarliestTimeLocked(), _latestTime);
            var frame = _anchor;
            for (var index = _keyframes.Count - 1; index >= 0; index--)
            {
                if (_keyframes[index].Time <= target)
                {
                    frame = _keyframes[index];
                    break;
                }
            }

            var after = frame?.Time ?? double.NegativeInfinity;
            var events = _events
                .Where(item => item.Time > after && item.Time <= target)
                .ToArray();
            return new TerminalRewindSlice(
                StartedAt, InitialColumns, InitialRows, EarliestTimeLocked(), _latestTime, frame, events);
        }
    }

    public string StartRecording(string directory, string sessionName, string? terminalType = null)
    {
        string path;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_recorder is not null)
                return _recorder.Path;

            var recordingStartedAt = DateTimeOffset.Now;
            var paths = TerminalRecordingFiles.CreatePaths(directory, sessionName, recordingStartedAt);
            _recorder = new TerminalDiskRecorder(
                paths, sessionName, terminalType,
                _currentColumns, _currentRows, recordingStartedAt, _latestTime);
            path = paths.CastPath;
        }
        RecordingChanged?.Invoke(true, path);
        return path;
    }

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
        recorder.Dispose();
        RecordingChanged?.Invoke(false, path);
        return path;
    }

    private void AppendLocked(TerminalRecordingEvent item)
    {
        _events.AddLast(item);
        _eventBytes += EventBytes(item);
        _latestTime = Math.Max(_latestTime, item.Time);
        _recorder?.Write(item);
        TrimLocked();
    }

    private double ToElapsedLocked(long unixTimeMilliseconds)
    {
        var elapsed = (unixTimeMilliseconds - StartedAt.ToUnixTimeMilliseconds()) / 1000d;
        _latestTime = Math.Max(_latestTime, Math.Max(0, elapsed));
        return _latestTime;
    }

    private void TrimLocked()
    {
        if (_events.Count == 0)
            return;

        var cutoff = _latestTime - _maximumAge.TotalSeconds;
        TerminalKeyframe? ageFrame = null;
        foreach (var frame in _keyframes)
        {
            if (frame.Time > cutoff)
                break;
            ageFrame = frame;
        }
        if (ageFrame is not null)
            PromoteAnchorLocked(ageFrame);

        while (_eventBytes + _keyframeBytes + (_anchor is null ? 0 : FrameBytes(_anchor)) > _maximumBytes)
        {
            if (_keyframes.Count == 0)
                break;
            PromoteAnchorLocked(_keyframes[0]);
        }
    }

    private void PromoteAnchorLocked(TerminalKeyframe frame)
    {
        _anchor = frame;
        while (_events.First is { } first && first.Value.Time <= frame.Time)
        {
            _eventBytes -= EventBytes(first.Value);
            _events.RemoveFirst();
        }
        while (_keyframes.Count > 0 && _keyframes[0].Time <= frame.Time)
        {
            _keyframeBytes -= FrameBytes(_keyframes[0]);
            _keyframes.RemoveAt(0);
        }
    }

    private double EarliestTimeLocked() =>
        _anchor?.Time ?? _events.First?.Value.Time ?? 0;

    private static long EventBytes(TerminalRecordingEvent item) =>
        32L + Encoding.UTF8.GetByteCount(item.Data);

    private static long FrameBytes(TerminalKeyframe frame) =>
        48L + Encoding.UTF8.GetByteCount(frame.State);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        TerminalDiskRecorder? recorder;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            recorder = _recorder;
            _recorder = null;
        }
        recorder?.Dispose();
    }
}

public sealed record TerminalRecordingPaths(string CastPath, string LogPath);

public static class TerminalRecordingFiles
{
    public static TerminalRecordingPaths CreatePaths(
        string directory,
        string sessionName,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A recording directory is required.", nameof(directory));

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

    private readonly StreamWriter _castWriter;
    private readonly StreamWriter _plainWriter;
    private readonly AnsiPlainTextRenderer _plainRenderer = new();
    private readonly DateTimeOffset _startedAt;
    private readonly double _captureOffset;
    private bool _disposed;

    public TerminalDiskRecorder(
        TerminalRecordingPaths paths,
        string title,
        string? terminalType,
        int width,
        int height,
        DateTimeOffset startedAt,
        double captureOffset)
    {
        Path = paths.CastPath;
        PlainPath = paths.LogPath;
        _startedAt = startedAt;
        _captureOffset = captureOffset;

        StreamWriter? castWriter = null;
        try
        {
            castWriter = OpenWriter(paths.CastPath);
            _plainWriter = OpenWriter(paths.LogPath);
            _castWriter = castWriter;
        }
        catch
        {
            castWriter?.Dispose();
            TryDelete(paths.CastPath);
            TryDelete(paths.LogPath);
            throw;
        }

        var header = new
        {
            version = 2,
            width,
            height,
            timestamp = startedAt.ToUnixTimeSeconds(),
            title,
            env = new Dictionary<string, string>
            {
                ["TERM"] = string.IsNullOrWhiteSpace(terminalType) ? "xterm-256color" : terminalType,
                ["SHELL"] = "Resesh",
            },
        };
        _castWriter.WriteLine(JsonSerializer.Serialize(header, JsonOptions));
    }

    public string Path { get; }
    public string PlainPath { get; }

    public void Write(TerminalRecordingEvent item)
    {
        if (_disposed)
            return;

        var relativeTime = Math.Max(0, item.Time - _captureOffset);
        _castWriter.WriteLine(JsonSerializer.Serialize(new object[] { relativeTime, item.Type, item.Data }, JsonOptions));
        if (!item.IsOutput)
            return;

        var eventTime = _startedAt.AddSeconds(relativeTime);
        foreach (var line in _plainRenderer.Feed(item.Data, eventTime))
            WritePlainLine(line);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var line in _plainRenderer.Flush())
            WritePlainLine(line);
        try
        {
            _castWriter.Dispose();
        }
        finally
        {
            _plainWriter.Dispose();
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

    private static StreamWriter OpenWriter(string path) => new(
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        AutoFlush = true,
    };

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
                !item[0].TryGetDouble(out var time) || type is not ("o" or "r"))
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

    public IReadOnlyList<TimestampedPlainLine> Feed(string value, DateTimeOffset timestamp)
    {
        _lastTimestamp = timestamp;
        var completed = new List<TimestampedPlainLine>();
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
                    else
                    {
                        _csi.Append(character);
                    }
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
                _row++;
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
                _row++;
                break;
            case 'E':
                CommitRow(_row, timestamp, completed);
                _row++;
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
                _row += Parameter(0, 1);
                break;
            case 'C':
            case 'a':
                _column += Parameter(0, 1);
                break;
            case 'D':
                _column = Math.Max(0, _column - Parameter(0, 1));
                break;
            case 'E':
                _row += Parameter(0, 1);
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
        return value;
    }

    private void WriteCharacter(char character, DateTimeOffset timestamp)
    {
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
        while (row.Text.Length < _column)
            row.Text.Append(' ');
        row.Text.Insert(_column, new string(' ', count));
    }

    private void EraseCharacters(int count)
    {
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
        Osc,
        ControlString,
        StringEscape,
    }
}
