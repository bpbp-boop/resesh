namespace Resesh.Terminal.Ghostty;

/// <summary>
/// When each line of output arrived (terminal.html's Phase 9.5 line timestamps), for the
/// ruler card, relative ages and command history times. Lines are stamped in runs: a run
/// starts at a tracked marker and covers every line up to the next run, all with the time
/// output reached them (to the second). Tracked markers follow trimming and reflow, so stamps
/// survive both. The oldest runs are dropped beyond <see cref="MaxRuns"/>; lines before the
/// first run, and the alternate screen, have no time.
/// </summary>
internal sealed class GhosttyLineTimes(ICommandBuffer buffer)
{
    internal const int MaxRuns = 1024;

    private readonly List<(ICommandMarker Marker, long UnixMs)> _runs = [];
    private ICommandMarker? _frontier; // the last line stamped

    public void Reset()
    {
        foreach (var (marker, _) in _runs)
            marker.Dispose();
        _runs.Clear();
        _frontier?.Dispose();
        _frontier = null;
    }

    /// <summary>Stamps the lines output reached since the last call (through the cursor line)
    /// with <paramref name="unixMs"/>. A line keeps the time it was first reached.</summary>
    public void Note(long unixMs)
    {
        if (unixMs <= 0 || buffer.IsAlternate || buffer.Length == 0)
            return;
        Prune();
        var cursor = buffer.CursorLine;
        while (cursor > 0 && buffer.IsWrapped(cursor))
            cursor--; // a soft-wrapped row belongs to the line it continues
        int start;
        if (_frontier is null || _frontier.Line < 0)
            start = _runs.Count == 0 ? cursor : 0;
        else
            start = _frontier.Line + 1;
        if (start > cursor)
            return;
        if (_runs.Count == 0 || _runs[^1].UnixMs / 1000 != unixMs / 1000)
        {
            if (buffer.CreateMarker(start) is not { } marker)
                return;
            _runs.Add((marker, unixMs));
            if (_runs.Count > MaxRuns)
            {
                _runs[0].Marker.Dispose();
                _runs.RemoveAt(0);
            }
        }
        _frontier?.Dispose();
        _frontier = buffer.CreateMarker(cursor);
    }

    /// <summary>When output reached <paramref name="line"/>, or null when unknown.</summary>
    public long? TimeOf(int line)
    {
        Prune();
        if (line < 0 || _runs.Count == 0 || _frontier is not { Line: >= 0 } frontier || line > frontier.Line)
            return null;
        // Runs stay in line order (markers never pass each other): the last that starts at or
        // before the line.
        int lo = 0, hi = _runs.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_runs[mid].Marker.Line <= line) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 ? _runs[found].UnixMs : null;
    }

    /// <summary>Runs whose start left the scrollback: the oldest one that still covers lines
    /// moves to the top; the rest go.</summary>
    private void Prune()
    {
        var gone = 0;
        while (gone < _runs.Count && _runs[gone].Marker.Line < 0)
            gone++;
        if (gone == 0)
            return;
        var keepLast = gone < _runs.Count ? _runs[gone].Marker.Line > 0 : _frontier is { Line: >= 0 };
        var (_, lastMs) = _runs[gone - 1];
        for (var i = 0; i < gone; i++)
            _runs[i].Marker.Dispose();
        _runs.RemoveRange(0, gone);
        if (keepLast && buffer.CreateMarker(0) is { } top)
            _runs.Insert(0, (top, lastMs));
    }
}
