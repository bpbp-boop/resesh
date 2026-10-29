namespace Resesh.Terminal.Native;

/// <summary>
/// The ruler's highlight lane (terminal.html's Phase 9.3 content index): which lines of the
/// whole scrollback match an overview rule. Scrollback lines never change, so each is scanned
/// once, in slices; the live screen area is rescanned every pass. Lines are keyed by a
/// "virtual" number anchored to a marker, so trimming the top of the scrollback does not
/// invalidate the index: virtual = absolute line + (anchor's virtual - anchor's line). Losing
/// the anchor, a reflow, or a rule change rebuilds it.
/// </summary>
internal sealed class OverviewIndex(ICommandBuffer buffer, Highlighter highlighter, TimeSpan? sliceBudget = null)
{
    private const int SliceLines = 2000;
    // Lines read per native call; the time budget is checked between chunks.
    private const int ChunkLines = 64;
    // Each line read takes the parser's lock, so during a flood a line can cost 100+ us and a
    // 2000-line slice took 50-500 ms. A pass stops at whichever comes first, and runs on the
    // annotation worker, not the UI thread.
    private readonly long _sliceBudgetTicks =
        (long)((sliceBudget ?? TimeSpan.FromMilliseconds(4)).TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
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
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var line = (int)Math.Max(0, _nextVirtual - offset); line < liveStart && scanned < SliceLines; line++, scanned++)
        {
            if (scanned % ChunkLines == 0)
            {
                if (scanned > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - start > _sliceBudgetTicks)
                    break;
                // One line more: Mask looks at the next line to see whether it continues this one.
                buffer.Prefetch(line, Math.Min(ChunkLines, liveStart - line) + 1);
            }
            var mask = Mask(line);
            if (mask != 0)
                _scrollback[line + offset] = mask;
            _nextVirtual = line + offset + 1;
        }
        buffer.Prefetch(liveStart, buffer.Length - liveStart);
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
        if (_anchor is null)
            yield break;
        if (_anchor.Line < 0)
        {
            Reset();
            yield break;
        }
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
