using System.Globalization;
using System.Text;

namespace Resesh.Core.Recording;

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
