namespace Resesh.Terminal.Native;

/// <summary>
/// The ruler's highlight lane (terminal.html's Phase 9.3 content index): which lines of the
/// whole scrollback match an overview rule. Scrollback lines never change, so each is scanned
/// once, in slices; the live screen area is rescanned every pass. Lines are keyed by a
/// "virtual" number anchored to a marker, so trimming the top of the scrollback does not
/// invalidate the index: virtual = absolute line + (anchor's virtual - anchor's line). Losing
/// the anchor, a reflow, or a rule change rebuilds it.
/// </summary>
internal sealed class OverviewIndex(ICommandBuffer buffer, Highlighter highlighter)
{
    private const int SliceLines = 2000;
    private const int ReanchorGap = 4096;

    private readonly Dictionary<long, uint> _scrollback = [];
    private readonly Dictionary<int, uint> _live = []; // absolute line -> mask, rebuilt each pass
    private ICommandMarker? _anchor;
    private long _anchorVirtual;
    private long _nextVirtual = -1; // next scrollback line to scan, as a virtual number

    /// <summary>Drops everything (rules changed, reflow): the next pass starts over.</summary>
    public void Reset()
    {
        _anchor?.Dispose();
        _anchor = null;
        _scrollback.Clear();
        _live.Clear();
        _nextVirtual = -1;
    }

    /// <summary>Scans up to one slice of new scrollback plus the live screen area. Returns true
    /// when scrollback remains unscanned (the caller schedules another pass).</summary>
    public bool Advance()
    {
        _live.Clear();
        if (!highlighter.HasOverviewRules || buffer.IsAlternate || buffer.Length == 0)
            return false;
        if (_anchor is null || _anchor.Line < 0)
        {
            Reset();
            _anchor = buffer.CreateMarker(0);
            if (_anchor is null)
                return false;
            _anchorVirtual = 0;
            _nextVirtual = 0;
        }
        var offset = _anchorVirtual - _anchor.Line;

        // Scrollback ends where the live screen area begins.
        var liveStart = Math.Max(0, buffer.Length - buffer.Rows);
        var scanned = 0;
        for (var line = (int)Math.Max(0, _nextVirtual - offset); line < liveStart && scanned < SliceLines; line++, scanned++)
        {
            var mask = Mask(line);
            if (mask != 0)
                _scrollback[line + offset] = mask;
            _nextVirtual = line + offset + 1;
        }
        for (var line = liveStart; line < buffer.Length; line++)
        {
            var mask = Mask(line);
            if (mask != 0)
                _live[line] = mask;
        }

        // Keep the anchor near new output so trimming does not take it soon.
        if (buffer.CursorLine - _anchor.Line > ReanchorGap && buffer.CreateMarker(liveStart) is { } fresh)
        {
            _anchorVirtual = liveStart + offset;
            _anchor.Dispose();
            _anchor = fresh;
        }
        // Forget entries trimmed off the top.
        if (_scrollback.Count > 0 && _scrollback.Keys.Min() - offset < 0)
        {
            foreach (var key in _scrollback.Keys.Where(k => k - offset < 0).ToList())
                _scrollback.Remove(key);
        }
        return _nextVirtual - offset < liveStart;
    }

    private uint Mask(int line)
    {
        if (buffer.IsWrapped(line))
            return 0; // a soft wrap belongs to the line it continues; scanned there
        var text = buffer.LineText(line);
        if (string.IsNullOrEmpty(text))
            return 0;
        for (var next = line + 1; buffer.IsWrapped(next) && buffer.LineText(next) is { } more && text.Length < 4096; next++)
            text += more;
        return highlighter.OverviewMask(text);
    }

    /// <summary>Absolute line -> rule mask for every indexed line still in the buffer.</summary>
    public IEnumerable<(int Line, uint Mask)> Lines()
    {
        if (_anchor is null || _anchor.Line < 0)
            yield break;
        var offset = _anchorVirtual - _anchor.Line;
        foreach (var (key, mask) in _scrollback)
        {
            var line = key - offset;
            if (line >= 0 && line < int.MaxValue)
                yield return ((int)line, mask);
        }
        foreach (var (line, mask) in _live)
            yield return (line, mask);
    }
}
