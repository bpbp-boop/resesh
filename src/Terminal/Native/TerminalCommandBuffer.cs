using System.Runtime.InteropServices;
using System.Text;

namespace Resesh.Terminal.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyBufferInfo
{
    public int CursorLine;
    public int CursorX;
    public int ViewportTop;
    public int TotalLines;
    public int Rows;
    public int Cols;
    public int Alternate;
}

/// <summary>Shell integration step as reseshvt.c captures it (RvtSemanticEvent); the
/// command line's UTF-8 bytes follow the struct.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySemanticEvent
{
    public int Kind;
    public int PromptKind;
    public int HasExit;
    public int ExitCode;
    public int CursorLine;
    public int CursorX;
    public uint Marker;
    public uint CommandLength;
}

/// <summary>
/// <see cref="ICommandBuffer"/> over a libghostty-vt terminal, used by one thread at a time: the
/// surface's on the UI thread, the annotation worker's on the pool. Buffer info and
/// line reads are cached until <see cref="Refresh"/>, which the surface calls before each
/// tracker entry point, so one pass sees one consistent snapshot of positions.
/// </summary>
internal sealed unsafe partial class TerminalCommandBuffer(Func<IntPtr> terminal) : ICommandBuffer
{
    private GhosttyBufferInfo _info;
    private IntPtr Terminal => terminal();
    private readonly Dictionary<int, (string? Text, bool Wrapped)> _lines = [];
    private readonly HashSet<NativeMarker> _live = [];

    internal void Refresh()
    {
        _lines.Clear();
        var term = terminal();
        if (term == IntPtr.Zero)
        {
            _info = default;
            return;
        }
        GhosttyBufferInfo info;
        GhosttyNative.rvt_buffer_info(term, &info);
        _info = info;
    }

    public bool IsAlternate => _info.Alternate != 0;
    public int CursorLine => _info.CursorLine;
    public int CursorX => _info.CursorX;
    public int ViewportTop => _info.ViewportTop;
    public int Rows => _info.Rows;
    public int Length => _info.TotalLines;

    [System.Runtime.CompilerServices.SkipLocalsInit] // the shim fills the buffer before it is read
    private (string? Text, bool Wrapped) Line(int line)
    {
        if (_lines.TryGetValue(line, out var cached))
            return cached;
        var term = terminal();
        (string? Text, bool Wrapped) result = (null, false);
        if (term != IntPtr.Zero && line >= 0 && line < _info.TotalLines)
        {
            var buffer = stackalloc byte[16384];
            int wrapped;
            var n = GhosttyNative.rvt_line_text(term, line, buffer, 16384, &wrapped);
            if (n >= 0)
                result = (Encoding.UTF8.GetString(buffer, n), wrapped != 0);
        }
        _lines[line] = result;
        return result;
    }

    public string? LineText(int line) => Line(line).Text;

    private byte[]? _prefetchText;
    private int[] _prefetchLengths = [];
    private byte[] _prefetchWrapped = [];

    /// <summary>Fills the line cache for a run of lines with one native call, which takes the
    /// terminal lock once: line by line, each read waits behind the parser during a flood.</summary>
    public void Prefetch(int first, int count)
    {
        var term = terminal();
        first = Math.Max(0, first);
        count = Math.Min(count, _info.TotalLines - first);
        if (term == IntPtr.Zero || count <= 0)
            return;
        _prefetchText ??= new byte[256 * 1024];
        if (_prefetchLengths.Length < count)
        {
            _prefetchLengths = new int[count];
            _prefetchWrapped = new byte[count];
        }
        while (count > 0)
        {
            int done;
            fixed (byte* text = _prefetchText)
            fixed (int* lengths = _prefetchLengths)
            fixed (byte* wrapped = _prefetchWrapped)
                done = GhosttyNative.rvt_lines_text(term, first, count, text, (nuint)_prefetchText.Length, lengths, wrapped);
            if (done <= 0)
                return;
            var offset = 0;
            for (var i = 0; i < done; i++)
            {
                var length = _prefetchLengths[i];
                if (length < 0)
                    continue; // missing or did not fit: Line reads it on its own
                _lines[first + i] = (Encoding.UTF8.GetString(_prefetchText, offset, length), _prefetchWrapped[i] != 0);
                offset += length;
            }
            first += done;
            count -= done;
        }
    }

    public bool IsWrapped(int line) => Line(line).Wrapped;

    /// <summary>Finds the visible OSC 8 run, crossing soft wraps but not hard line breaks.</summary>
    internal (string Uri, (int Row, int Start, int End)[] Spans)? HyperlinkAt(int x, int y)
    {
        var term = Terminal;
        if (term == IntPtr.Zero || x < 0 || x >= _info.Cols || y < 0 || y >= Rows)
            return null;
        string? UriAt(int column, int row)
        {
            var bytes = stackalloc byte[2048];
            var length = GhosttyNative.rvt_cell_hyperlink(term, (ushort)column, (ushort)row, bytes, 2048);
            return length > 0 ? Encoding.UTF8.GetString(bytes, length) : null;
        }
        if (UriAt(x, y) is not { } uri)
            return null;
        var top = y;
        var start = x;
        while (true)
        {
            if (start > 0)
            {
                if (UriAt(start - 1, top) != uri)
                    break;
                start--;
            }
            else
            {
                if (top == 0 || !IsWrapped(ViewportTop + top) || UriAt(_info.Cols - 1, top - 1) != uri)
                    break;
                top--;
                start = _info.Cols - 1;
            }
        }
        var bottom = y;
        var end = x + 1;
        while (true)
        {
            if (end < _info.Cols)
            {
                if (UriAt(end, bottom) != uri)
                    break;
                end++;
            }
            else
            {
                if (bottom == Rows - 1 || !IsWrapped(ViewportTop + bottom + 1) || UriAt(0, bottom + 1) != uri)
                    break;
                bottom++;
                end = 1;
            }
        }
        var spans = new (int Row, int Start, int End)[bottom - top + 1];
        for (var row = top; row <= bottom; row++)
            spans[row - top] = (row, row == top ? start : 0, row == bottom ? end : _info.Cols);
        return (uri, spans);
    }

    public ICommandMarker? CreateMarker(int line)
    {
        var term = terminal();
        if (term == IntPtr.Zero)
            return null;
        var id = GhosttyNative.rvt_marker_new(term, line);
        return id == 0 ? null : Adopt(id);
    }

    /// <summary>Takes ownership of a marker the shim created (shell integration events).</summary>
    internal ICommandMarker? Adopt(uint id)
    {
        if (id == 0)
            return null;
        var marker = new NativeMarker(this, id);
        _live.Add(marker);
        return marker;
    }

    /// <summary>Releases this buffer's markers before the terminal is freed or reused.</summary>
    internal void Detach()
    {
        var term = Terminal;
        foreach (var marker in _live)
            marker.Release(term);
        _live.Clear();
    }

    private sealed partial class NativeMarker(TerminalCommandBuffer owner, uint id) : ICommandMarker
    {
        private bool _disposed;

        public bool IsDisposed => _disposed || Line < 0;

        public int Line
        {
            get
            {
                if (_disposed)
                    return -1;
                var term = owner.Terminal;
                var line = term == IntPtr.Zero ? -1 : GhosttyNative.rvt_marker_line(term, id);
                if (line < 0)
                {
                    owner._live.Remove(this);
                    Release(term);
                }
                return line;
            }
        }

        internal void Release(IntPtr term)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (term != IntPtr.Zero)
                GhosttyNative.rvt_marker_free(term, id);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            owner._live.Remove(this);
            Release(owner.Terminal);
        }
    }
}
