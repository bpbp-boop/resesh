using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Resesh.Terminal.Native;

/// <summary>
/// Caches for cells drawn through DirectWrite layout rather than glyph runs. Color emoji are
/// rasterized once into one shared atlas on the Direct2D device and copied from it: drawing
/// one with DrawText costs a layout and a color-glyph rasterization, ~0.5 ms each, on every
/// frame it is on screen (see tools/terminal-bench/RESULTS.md). Combining-mark clusters keep
/// their per-frame draw (so they stay ClearType on the opaque canvas) but reuse their text layout.
/// </summary>
internal sealed unsafe partial class TerminalRenderer
{
    private const int EmojiAtlasSize = 2048;
    private const int MaxClusterLayouts = 512;

    // Shared across all renderers on s_d2dDevice (guarded by DeviceGate): one 16 MB texture
    // for the process instead of one per tab, shelf-packed by each surface's cell pixel size.
    private static ID2D1Bitmap1? s_emojiAtlas;
    private static readonly Dictionary<(string Text, int CellWidth, int CellHeight, int Cells, uint Color, float EmSize), RawRectF> s_emojiSlots = [];
    private static int s_emojiX;
    private static int s_emojiY;
    private static int s_emojiRowHeight;

    private readonly Dictionary<(string Text, int Width), (IDWriteTextLayout Layout, float Offset)> _clusterLayouts = [];

    /// <summary>Draws an emoji cluster into the cell rectangle from the shared atlas, rasterizing
    /// it on first use. The brush must already carry the cell's foreground, which color fonts use
    /// for their monochrome layers.</summary>
    private void DrawEmoji(string text, int cells, uint color, RawRectF cell)
    {
        var key = (text, CellWidth, CellHeight, cells, color, _emSize);
        if (!s_emojiSlots.TryGetValue(key, out var source))
        {
            if (!TryRasterizeEmoji(text, cells, out source))
            {
                _dc.DrawText(text, _emojiFormat!, ToRect(cell), _brush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
                return;
            }
            s_emojiSlots[key] = source;
        }
        // Whole-pixel rectangles of equal size, so nearest-neighbor is an exact copy.
        _dc.DrawBitmap(s_emojiAtlas!, cell, 1f, InterpolationMode.NearestNeighbor, source, null);
    }

    private bool TryRasterizeEmoji(string text, int cells, out RawRectF slot)
    {
        slot = default;
        int slotWidth = cells * CellWidth, slotHeight = CellHeight;
        if (slotWidth <= 0 || slotHeight <= 0 || slotWidth > EmojiAtlasSize || slotHeight > EmojiAtlasSize || _canvasBitmap is null)
            return false;
        if (s_emojiAtlas is null)
        {
            s_emojiAtlas = _dc.CreateBitmap(new SizeI(EmojiAtlasSize, EmojiAtlasSize), IntPtr.Zero, 0, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                BitmapOptions.Target));
        }
        if (s_emojiX + slotWidth > EmojiAtlasSize)
        {
            s_emojiX = 0;
            s_emojiY += s_emojiRowHeight;
            s_emojiRowHeight = 0;
        }
        if (s_emojiY + slotHeight > EmojiAtlasSize)
        {
            // Full: start over. Commands on one device context run in order, so draws already
            // recorded this frame still read the old contents.
            s_emojiSlots.Clear();
            s_emojiX = 0;
            s_emojiY = 0;
            s_emojiRowHeight = 0;
        }
        float left = s_emojiX, top = s_emojiY;
        s_emojiX += slotWidth;
        s_emojiRowHeight = Math.Max(s_emojiRowHeight, slotHeight);
        slot = new RawRectF(left, top, left + slotWidth, top + slotHeight);

        // Switching targets mid-draw is allowed; the atlas is transparent, so text is
        // grayscale antialiased there (color glyphs are anyway).
        _dc.Target = s_emojiAtlas;
        _dc.PushAxisAlignedClip(slot, AntialiasMode.Aliased);
        _dc.Clear(new Color4(0, 0, 0, 0));
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _dc.DrawText(text, _emojiFormat!, ToRect(slot), _brush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;
        _dc.PopAxisAlignedClip();
        _dc.Target = _canvasBitmap;
        return true;
    }

    /// <summary>Base character plus combining marks, laid out in the terminal font and placed
    /// on the row's baseline.</summary>
    private void DrawCombiningCluster(string text, int width, Vector2 cellOrigin)
    {
        var key = (text, width);
        if (!_clusterLayouts.TryGetValue(key, out var entry))
        {
            if (_clusterLayouts.Count >= MaxClusterLayouts)
                ClearClusterLayouts();
            var layout = s_dwrite!.CreateTextLayout(text, _clusterFormat!, width, CellHeight);
            var lineMetrics = layout.LineMetrics;
            entry = (layout, lineMetrics.Length > 0 ? _baseline - lineMetrics[0].Baseline : 0);
            _clusterLayouts[key] = entry;
        }
        _dc.DrawTextLayout(cellOrigin + new Vector2(0, entry.Offset), entry.Layout, _brush, DrawTextOptions.Clip);
    }

    private static Rect ToRect(RawRectF r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private void ClearClusterLayouts()
    {
        foreach (var (layout, _) in _clusterLayouts.Values)
            layout.Dispose();
        _clusterLayouts.Clear();
    }

    /// <summary>Drops this renderer's layout cache when the font or cell size changed, or the
    /// surface is trimmed or disposed.</summary>
    private void ResetClusterCaches()
    {
        ClearClusterLayouts();
    }

    private static void DisposeSharedEmojiAtlas()
    {
        s_emojiAtlas?.Dispose();
        s_emojiAtlas = null;
        s_emojiSlots.Clear();
        s_emojiX = 0;
        s_emojiY = 0;
        s_emojiRowHeight = 0;
    }
}
