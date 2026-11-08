namespace Resesh.Terminal.Native;

/// <summary>
/// Which horizontal bands of the canvas each present must copy into the back buffer. A flip
/// chain hands back the buffer it presented <see cref="BufferCount"/> frames ago, so a band has
/// to be copied in every frame until each buffer has it: the bands drawn this frame plus those
/// drawn in the frames since that buffer was last presented. Anything that changes the whole
/// canvas, or makes buffers whose contents are unknown, copies everything until all buffers
/// have caught up.
/// </summary>
internal sealed class PresentRegions(int bufferCount)
{
    private readonly List<(int Top, int Bottom)> _current = [];
    private readonly Queue<List<(int Top, int Bottom)>> _history = new();
    private readonly List<(int Top, int Bottom)> _merged = [];
    private int _fullFrames = bufferCount;

    public int BufferCount { get; } = bufferCount;

    /// <summary>The next <see cref="BufferCount"/> presents copy the whole canvas.</summary>
    public void InvalidateAll() => _fullFrames = BufferCount;

    /// <summary>Records pixel rows [<paramref name="top"/>, <paramref name="bottom"/>) as
    /// drawn in the frame being built.</summary>
    public void Add(int top, int bottom)
    {
        if (bottom > top)
            _current.Add((top, bottom));
    }

    /// <summary>Ends the frame: the bands to copy, clipped to <paramref name="height"/>,
    /// sorted and merged; null to copy everything.</summary>
    public IReadOnlyList<(int Top, int Bottom)>? Take(int height)
    {
        var full = _fullFrames > 0;
        if (full)
            _fullFrames--;
        else
        {
            _merged.Clear();
            _merged.AddRange(_current);
            foreach (var frame in _history)
                _merged.AddRange(frame);
            _merged.Sort(static (a, b) => a.Top.CompareTo(b.Top));
            var count = 0;
            for (var i = 0; i < _merged.Count; i++)
            {
                var (top, bottom) = _merged[i];
                var t = Math.Max(0, top);
                var b = Math.Min(height, bottom);
                if (b <= t)
                    continue;
                if (count > 0 && t <= _merged[count - 1].Bottom)
                    _merged[count - 1] = (_merged[count - 1].Top, Math.Max(_merged[count - 1].Bottom, b));
                else
                    _merged[count++] = (t, b);
            }
            _merged.RemoveRange(count, _merged.Count - count);
        }
        // Keep this frame's bands for the buffers presented after it.
        var kept = _history.Count >= BufferCount - 1 && _history.Count > 0 ? _history.Dequeue() : new List<(int, int)>();
        kept.Clear();
        kept.AddRange(_current);
        if (BufferCount > 1)
            _history.Enqueue(kept);
        _current.Clear();
        return full ? null : _merged;
    }
}
