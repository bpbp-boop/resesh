using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Resesh.Terminal.Native;

// Draws the characters in BuiltinGlyphs: boxes as whole-pixel rectangles, and the arcs, diagonals
// and Powerline shapes as paths clipped to their cell.
internal sealed unsafe partial class TerminalRenderer
{
    // Light box-drawing line thickness in pixels, from the font's underline; heavy is twice it.
    private int _boxThickness = 1;
    // Path glyphs in cell coordinates, valid for the current cell size.
    private readonly Dictionary<uint, ID2D1PathGeometry> _glyphGeometries = [];

    /// <summary>Draws a built-in glyph in <paramref name="foreground"/> at <paramref name="alpha"/>
    /// over <paramref name="background"/>. The color is mixed up front and drawn opaque, because
    /// a glyph's boxes overlap where its lines join and would show through each other otherwise.</summary>
    private void DrawBuiltinGlyph(uint codepoint, float left, float top, uint foreground, uint background, float alpha)
    {
        alpha *= BuiltinGlyphs.ShadeOf(codepoint);
        Color4 fg = Rgb(foreground), bg = Rgb(background);
        SetBrush(new Color4(bg.R + (fg.R - bg.R) * alpha, bg.G + (fg.G - bg.G) * alpha, bg.B + (fg.B - bg.B) * alpha, 1));
        if (BuiltinGlyphs.IsVector(codepoint))
        {
            DrawVectorGlyph(codepoint, left, top);
            return;
        }
        Span<BuiltinGlyphs.Box> boxes = stackalloc BuiltinGlyphs.Box[BuiltinGlyphs.MaxBoxes];
        var count = BuiltinGlyphs.Boxes(codepoint, CellWidth, CellHeight, _boxThickness, boxes);
        foreach (var box in boxes[..count])
            _dc.FillRectangle(new Rect(left + box.Left, top + box.Top, box.Right - box.Left, box.Bottom - box.Top), _brush);
    }

    private void DrawVectorGlyph(uint codepoint, float left, float top)
    {
        float width = CellWidth, height = CellHeight;
        _dc.Transform = Matrix3x2.CreateTranslation(left, top);
        // Rows repaint independently, so nothing may spill into a neighboring cell.
        _dc.PushAxisAlignedClip(new Rect(0, 0, width, height), AntialiasMode.Aliased);
        switch (codepoint)
        {
            case 0x2571 or 0xE0BB or 0xE0BD: // ╱
                DrawDiagonal(width, height, rising: true);
                break;
            case 0x2572 or 0xE0B9 or 0xE0BF: // ╲
                DrawDiagonal(width, height, rising: false);
                break;
            case 0x2573: // ╳
                DrawDiagonal(width, height, rising: true);
                DrawDiagonal(width, height, rising: false);
                break;
            case 0xE0B0 or 0xE0B2 or 0xE0B4 or 0xE0B6 or 0xE0B8 or 0xE0BA or 0xE0BC or 0xE0BE:
                _dc.FillGeometry(GlyphGeometry(codepoint), _brush);
                break;
            default: // arcs and the outlined Powerline separators
                _dc.DrawGeometry(GlyphGeometry(codepoint), _brush, _boxThickness);
                break;
        }
        _dc.PopAxisAlignedClip();
        _dc.Transform = Matrix3x2.Identity;
    }

    /// <summary>Corner to corner, run slightly past both so the clipped ends fill the corners and
    /// diagonals in adjacent cells connect.</summary>
    private void DrawDiagonal(float width, float height, bool rising)
    {
        float dx = 0.5f * MathF.Min(1, width / height), dy = 0.5f * MathF.Min(1, height / width);
        var (from, to) = rising
            ? (new Vector2(width + dx, -dy), new Vector2(-dx, height + dy))
            : (new Vector2(-dx, -dy), new Vector2(width + dx, height + dy));
        _dc.DrawLine(from, to, _brush, _boxThickness);
    }

    private ID2D1PathGeometry GlyphGeometry(uint codepoint)
    {
        if (_glyphGeometries.TryGetValue(codepoint, out var hit))
            return hit;
        float w = CellWidth, h = CellHeight;
        var geometry = s_d2dFactory!.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            switch (codepoint)
            {
                case 0x256D: AddArc(sink, 1, 1); break;   // ╭
                case 0x256E: AddArc(sink, -1, 1); break;  // ╮
                case 0x256F: AddArc(sink, -1, -1); break; // ╯
                case 0x2570: AddArc(sink, 1, -1); break;  // ╰
                case 0xE0B0: AddPolyline(sink, true, new(0, 0), new(w, h / 2), new(0, h)); break;
                case 0xE0B1: AddPolyline(sink, false, new(0, 0), new(w, h / 2), new(0, h)); break;
                case 0xE0B2: AddPolyline(sink, true, new(w, 0), new(0, h / 2), new(w, h)); break;
                case 0xE0B3: AddPolyline(sink, false, new(w, 0), new(0, h / 2), new(w, h)); break;
                case 0xE0B4: AddHalfRound(sink, filled: true, mirrored: false); break;
                case 0xE0B5: AddHalfRound(sink, filled: false, mirrored: false); break;
                case 0xE0B6: AddHalfRound(sink, filled: true, mirrored: true); break;
                case 0xE0B7: AddHalfRound(sink, filled: false, mirrored: true); break;
                case 0xE0B8: AddPolyline(sink, true, new(0, 0), new(w, h), new(0, h)); break;
                case 0xE0BA: AddPolyline(sink, true, new(w, 0), new(w, h), new(0, h)); break;
                case 0xE0BC: AddPolyline(sink, true, new(0, 0), new(w, 0), new(0, h)); break;
                case 0xE0BE: AddPolyline(sink, true, new(0, 0), new(w, 0), new(w, h)); break;
            }
            sink.Close();
        }
        return _glyphGeometries[codepoint] = geometry;
    }

    private static void AddPolyline(ID2D1GeometrySink sink, bool filled, params ReadOnlySpan<Vector2> points)
    {
        sink.BeginFigure(points[0], filled ? FigureBegin.Filled : FigureBegin.Hollow);
        foreach (var point in points[1..])
            sink.AddLine(point);
        sink.EndFigure(filled ? FigureEnd.Closed : FigureEnd.Open);
    }

    /// <summary>A rounded corner: straight in from the top or bottom edge, a curve through the
    /// center, straight out to the left or right edge, lined up with the light box lines.</summary>
    private void AddArc(ID2D1GeometrySink sink, int dx, int dy)
    {
        float w = CellWidth, h = CellHeight;
        var centerX = Math.Max(0, CellWidth - _boxThickness) / 2 + _boxThickness / 2f;
        var centerY = Math.Max(0, CellHeight - _boxThickness) / 2 + _boxThickness / 2f;
        var radius = MathF.Min(w, h) / 2;
        const float control = 0.25f; // how far from the center the curve's control points sit
        sink.BeginFigure(new Vector2(centerX, dy > 0 ? h : 0), FigureBegin.Hollow);
        sink.AddLine(new Vector2(centerX, centerY + dy * radius));
        sink.AddBezier(new BezierSegment
        {
            Point1 = new Vector2(centerX, centerY + dy * control * radius),
            Point2 = new Vector2(centerX + dx * control * radius, centerY),
            Point3 = new Vector2(centerX + dx * radius, centerY),
        });
        sink.AddLine(new Vector2(dx > 0 ? w : 0, centerY));
        sink.EndFigure(FigureEnd.Open);
    }

    /// <summary>The Powerline half circle against the left edge (the right when mirrored), with
    /// straight sides when the cell is taller than two radii. The outline is inset by half the
    /// line so the stroke stays inside the filled shape's edge.</summary>
    private void AddHalfRound(ID2D1GeometrySink sink, bool filled, bool mirrored)
    {
        float w = CellWidth, h = CellHeight;
        var inset = filled ? 0 : _boxThickness / 2f;
        var corner = MathF.Min(w, h / 2);             // where the curves meet the straight side
        var radius = corner - inset;
        var k = radius * (MathF.Sqrt(2) - 1) * 4 / 3; // control distance for a quarter circle
        Vector2 P(float x, float y) => new(mirrored ? w - x : x, y);
        sink.BeginFigure(P(0, inset), filled ? FigureBegin.Filled : FigureBegin.Hollow);
        sink.AddBezier(new BezierSegment { Point1 = P(k, inset), Point2 = P(radius, corner - k), Point3 = P(radius, corner) });
        sink.AddLine(P(radius, h - corner));
        sink.AddBezier(new BezierSegment { Point1 = P(radius, h - corner + k), Point2 = P(k, h - inset), Point3 = P(0, h - inset) });
        sink.EndFigure(filled ? FigureEnd.Closed : FigureEnd.Open);
    }

    private void DisposeGlyphGeometries()
    {
        foreach (var geometry in _glyphGeometries.Values)
            geometry.Dispose();
        _glyphGeometries.Clear();
    }
}
