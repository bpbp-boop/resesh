using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Input;

namespace Resesh.Terminal.Native;

/// <summary>
/// Clickable links, as terminal.html's link handler and WebLinksAddon: OSC 8 hyperlinks and
/// plain http(s) URLs (soft-wrapped lines joined) underline under the pointer and open in the
/// default browser on a plain click. Programs that track the mouse keep the pointer.
/// </summary>
public sealed unsafe partial class NativeTerminalSurface
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
        UpdatePointerCursor();
        _forceFull = true; // re-read rows so the old underline goes and the new one shows
        RequestFrame();
    }

    /// <summary>A hovered link shows the hand; otherwise the pointer is the shape the program
    /// asked for through OSC 22 (the I-beam until it asks).</summary>
    private void UpdatePointerCursor()
    {
        var shape = _hoverUri is not null ? InputSystemCursorShape.Hand : CursorShapeFor(_lastInfo.MouseShape);
        if (shape == _pointerShape)
            return;
        _pointerShape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private InputSystemCursorShape _pointerShape = InputSystemCursorShape.IBeam;

    /// <summary>GhosttyMouseShape (the CSS cursor names OSC 22 carries) to the nearest system
    /// cursor. Windows has no drag-and-drop or zoom cursors outside OLE, so those stay arrows.</summary>
    private static InputSystemCursorShape CursorShapeFor(byte shape) => shape switch
    {
        2 => InputSystemCursorShape.Help,                                 // help
        3 or 15 => InputSystemCursorShape.Hand,                           // pointer, grab
        4 => InputSystemCursorShape.AppStarting,                          // progress
        5 => InputSystemCursorShape.Wait,                                 // wait
        6 or 7 => InputSystemCursorShape.Cross,                           // cell, crosshair
        8 or 9 => InputSystemCursorShape.IBeam,                           // text, vertical-text
        12 or 16 or 17 => InputSystemCursorShape.SizeAll,                 // move, grabbing, all-scroll
        13 or 14 => InputSystemCursorShape.UniversalNo,                   // no-drop, not-allowed
        18 or 21 or 23 or 28 => InputSystemCursorShape.SizeWestEast,      // col-, e-, w-, ew-resize
        19 or 20 or 22 or 29 => InputSystemCursorShape.SizeNorthSouth,    // row-, n-, s-, ns-resize
        24 or 27 or 30 => InputSystemCursorShape.SizeNortheastSouthwest,  // ne-, sw-, nesw-resize
        25 or 26 or 31 => InputSystemCursorShape.SizeNorthwestSoutheast,  // nw-, se-, nwse-resize
        _ => InputSystemCursorShape.Arrow, // default, context-menu, alias, copy, zoom-in, zoom-out
    };

    /// <summary>Underlines the hovered link on rows just read.</summary>
    private void ApplyHoverLink()
    {
        foreach (var (row, start, end) in _hoverSpans)
        {
            if (row < 0 || row >= Rows || _dirty[row] == 0)
                continue;
            for (var x = Math.Max(0, start); x < end && x < Columns; x++)
                _cells[row * Columns + x].Flags |= TerminalCell.Underline;
        }
    }

    private LinkHit? LinkAt(int x, int y)
    {
        if (_term == IntPtr.Zero || _cells == null || x < 0 || y < 0 || x >= Columns || y >= Rows)
            return null;
        return HyperlinkAt(x, y) ?? UrlAt(x, y);
    }

    private LinkHit? HyperlinkAt(int x, int y)
    {
        if (RefreshCommands() is null || _commandBuffer is not { } buffer)
            return null;
        return buffer.HyperlinkAt(x, y) is { } hit ? new LinkHit(hit.Uri, hit.Spans) : null;
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
            var rowText = _highlighter.RowText(new ReadOnlySpan<TerminalCell>(_cells + row * Columns, Columns), c => GraphemeAt(c, r));
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
