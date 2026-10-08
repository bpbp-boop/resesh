using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Input;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// Clickable links, as terminal.html's link handler and WebLinksAddon: OSC 8 hyperlinks and
/// plain http(s) URLs (soft-wrapped lines joined) underline under the pointer and open in the
/// default browser on a plain click. Programs that track the mouse keep the pointer.
/// </summary>
public sealed unsafe partial class GhosttyTerminalSurface
{
    // WebLinksAddon's URL pattern.
    private static readonly Regex UrlPattern = new(
        @"(https?|HTTPS?)://[^\s""'!*(){}|\\^<>`]*[^\s""':,.!?{}|\\^~\[\]`()<>]",
        RegexOptions.CultureInvariant);

    private string? _hoverUri;
    private (int Row, int Start, int End)[] _hoverSpans = [];
    private (int X, int Y) _hoverCell = (-1, -1);

    private readonly record struct LinkHit(string Uri, (int Row, int Start, int End)[] Spans);

    /// <summary>Re-resolves the link under the pointer (null: the pointer left or is busy).</summary>
    private void UpdateHoverLink((int X, int Y)? cell, bool force = false)
    {
        var target = cell ?? (-1, -1);
        if (!force && target == _hoverCell)
            return;
        _hoverCell = target;
        var hit = cell is { } c ? LinkAt(c.X, c.Y) : null;
        if (hit?.Uri == _hoverUri && (hit?.Spans ?? []).AsSpan().SequenceEqual(_hoverSpans))
            return;
        _hoverUri = hit?.Uri;
        _hoverSpans = hit?.Spans ?? [];
        ProtectedCursor = InputSystemCursor.Create(_hoverUri is null ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Hand);
        _forceFull = true; // re-read rows so the old underline goes and the new one shows
        RequestFrame();
    }

    /// <summary>Underlines the hovered link on rows just read.</summary>
    private void ApplyHoverLink()
    {
        foreach (var (row, start, end) in _hoverSpans)
        {
            if (row < 0 || row >= Rows || _dirty[row] == 0)
                continue;
            for (var x = Math.Max(0, start); x < end && x < Columns; x++)
                _cells[row * Columns + x].Flags |= GhosttyCell.Underline;
        }
    }

    private LinkHit? LinkAt(int x, int y)
    {
        if (_term == IntPtr.Zero || _cells == null || x < 0 || y < 0 || x >= Columns || y >= Rows)
            return null;
        return HyperlinkAt(x, y) ?? UrlAt(x, y);
    }

    private string? HyperlinkUri(int x, int y)
    {
        var buffer = stackalloc byte[2048];
        var n = GhosttyNative.rvt_cell_hyperlink(_term, (ushort)x, (ushort)y, buffer, 2048);
        return n > 0 ? Encoding.UTF8.GetString(buffer, n) : null;
    }

    /// <summary>An OSC 8 hyperlink: the run of cells on this row that share its URI.</summary>
    private LinkHit? HyperlinkAt(int x, int y)
    {
        if (HyperlinkUri(x, y) is not { } uri)
            return null;
        var start = x;
        while (start > 0 && HyperlinkUri(start - 1, y) == uri)
            start--;
        var end = x + 1;
        while (end < Columns && HyperlinkUri(end, y) == uri)
            end++;
        return new LinkHit(uri, [(y, start, end)]);
    }

    /// <summary>A plain URL in the logical line under the pointer: soft-wrapped rows on
    /// screen are joined, as WebLinksAddon joins wrapped buffer lines.</summary>
    private LinkHit? UrlAt(int x, int y)
    {
        if (RefreshCommands() is null || _commandBuffer is not { } buffer)
            return null;
        var top = y;
        while (top > 0 && buffer.IsWrapped(buffer.ViewportTop + top))
            top--;
        var bottom = y;
        while (bottom < Rows - 1 && buffer.IsWrapped(buffer.ViewportTop + bottom + 1))
            bottom++;

        var text = new StringBuilder();
        var map = new List<(int Row, int Start, int End)>();
        for (var row = top; row <= bottom; row++)
        {
            var r = row;
            var rowText = _highlighter.RowText(new ReadOnlySpan<GhosttyCell>(_cells + row * Columns, Columns), c => GraphemeAt(c, r));
            text.Append(rowText);
            for (var i = 0; i < rowText.Length; i++)
                map.Add((row, _highlighter.ColumnStarts[i], _highlighter.ColumnEnds[i]));
        }

        for (var match = UrlPattern.Match(text.ToString()); match.Success; match = match.NextMatch())
        {
            var first = match.Index;
            var last = match.Index + match.Length - 1;
            var covers = false;
            for (var i = first; i <= last && !covers; i++)
                covers = map[i].Row == y && map[i].Start <= x && x < map[i].End;
            if (!covers)
                continue;
            var spans = new List<(int Row, int Start, int End)>();
            for (var i = first; i <= last; i++)
            {
                var (row, start, end) = map[i];
                if (spans.Count > 0 && spans[^1].Row == row)
                    spans[^1] = (row, Math.Min(spans[^1].Start, start), Math.Max(spans[^1].End, end));
                else
                    spans.Add((row, start, end));
            }
            return new LinkHit(match.Value, [.. spans]);
        }
        return null;
    }
}
