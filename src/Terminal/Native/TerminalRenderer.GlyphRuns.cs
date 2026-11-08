using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DirectWrite;

namespace Resesh.Terminal.Native;

/// <summary>
/// Glyph runs drawn straight through ID2D1RenderTarget::DrawGlyphRun. Vortice's GlyphRun
/// takes managed arrays sized to the run, so every run of every frame allocated two of them;
/// the native call reads the renderer's own reused buffers instead.
/// </summary>
internal sealed unsafe partial class TerminalRenderer
{
    // ID2D1RenderTarget's slot for DrawGlyphRun: IUnknown (3), ID2D1Resource::GetFactory (1),
    // then 25 render-target methods before it (d2d1.h).
    private const int DrawGlyphRunSlot = 29;

    /// <summary>DWRITE_GLYPH_RUN.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGlyphRun
    {
        public IntPtr FontFace;
        public float FontEmSize;
        public uint GlyphCount;
        public ushort* GlyphIndices;
        public float* GlyphAdvances;
        public GlyphOffset* GlyphOffsets;
        public int IsSideways;
        public uint BidiLevel;
    }

    /// <summary>D2D1_POINT_2F, passed by value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Point2F
    {
        public float X, Y;
    }

    /// <summary>Draws <paramref name="count"/> glyphs of <paramref name="face"/> in the current
    /// brush. <paramref name="offsets"/> may be null.</summary>
    private void DrawGlyphs(Vector2 origin, IDWriteFontFace face, ushort* glyphs, float* advances, GlyphOffset* offsets, int count)
    {
        var run = new NativeGlyphRun
        {
            FontFace = face.NativePointer,
            FontEmSize = _emSize,
            GlyphCount = (uint)count,
            GlyphIndices = glyphs,
            GlyphAdvances = advances,
            GlyphOffsets = offsets,
        };
        var dc = _dc.NativePointer;
        var drawGlyphRun = (delegate* unmanaged[Stdcall]<IntPtr, Point2F, NativeGlyphRun*, IntPtr, int, void>)(*(void***)dc)[DrawGlyphRunSlot];
        drawGlyphRun(dc, new Point2F { X = origin.X, Y = origin.Y }, &run, _brush.NativePointer, (int)Vortice.DCommon.MeasuringMode.Natural);
    }

    /// <summary>A run in <paramref name="face"/> of the given style. The icon font draws
    /// smoothed both ways and in grayscale (see <see cref="_symbolParams"/>).</summary>
    private void DrawRun(Vector2 origin, int style, int face, ushort* glyphs, float* advances, GlyphOffset* offsets, int count)
    {
        var fontFace = _faces[style][face];
        if (face != _symbolsFace)
        {
            DrawGlyphs(origin, fontFace, glyphs, advances, offsets, count);
            return;
        }
        _dc.TextRenderingParams = _symbolParams;
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        DrawGlyphs(origin, fontFace, glyphs, advances, offsets, count);
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;
        _dc.TextRenderingParams = null;
    }
}
