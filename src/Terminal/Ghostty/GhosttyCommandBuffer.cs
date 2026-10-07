using System.Runtime.InteropServices;
using System.Text;

namespace Resesh.Terminal.Ghostty;

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
/// <see cref="ICommandBuffer"/> over a libghostty-vt terminal. UI thread only. Buffer info and
/// line reads are cached until <see cref="Refresh"/>, which the surface calls before each
/// tracker entry point, so one pass sees one consistent snapshot of positions.
/// </summary>
internal sealed unsafe class GhosttyCommandBuffer(Func<IntPtr> terminal) : ICommandBuffer
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

    public bool IsWrapped(int line) => Line(line).Wrapped;

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

    /// <summary>The terminal is about to be freed: markers must not touch it afterwards.</summary>
    internal void Detach()
    {
        foreach (var marker in _live)
            marker.Orphan();
        _live.Clear();
    }

    private sealed class NativeMarker(GhosttyCommandBuffer owner, uint id) : ICommandMarker
    {
        private bool _disposed;
        private bool _orphaned;

        public bool IsDisposed => _disposed || _orphaned || Line < 0;

        public int Line
        {
            get
            {
                if (_disposed || _orphaned)
                    return -1;
                var term = owner.Terminal;
                return term == IntPtr.Zero ? -1 : GhosttyNative.rvt_marker_line(term, id);
            }
        }

        internal void Orphan() => _orphaned = true;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._live.Remove(this);
            if (_orphaned)
                return;
            var term = owner.Terminal;
            if (term != IntPtr.Zero)
                GhosttyNative.rvt_marker_free(term, id);
        }
    }
}
