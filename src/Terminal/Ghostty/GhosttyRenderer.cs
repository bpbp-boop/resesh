using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWFactoryType = Vortice.DirectWrite.FactoryType;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using MeasuringMode = Vortice.DCommon.MeasuringMode;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// Draws a flattened libghostty-vt grid with Direct2D/DirectWrite into a composition swap chain
/// for a SwapChainPanel. Dirty rows are drawn into a retained canvas texture; each present copies
/// the canvas to the back buffer, because flip-model buffers do not keep their contents.
/// UI thread only.
/// </summary>
internal sealed unsafe class GhosttyRenderer : IDisposable
{
    // One device for every terminal: D2D objects are cheap per surface, devices are not.
    private static ID3D11Device? s_device;
    private static ID3D11DeviceContext? s_context;
    private static ID2D1Factory1? s_d2dFactory;
    private static ID2D1Device? s_d2dDevice;
    private static IDWriteFactory2? s_dwrite;
    private static IDXGIFactory2? s_dxgiFactory;

    private static readonly string[] FallbackFamilies =
    [
        "Cascadia Mono", "Consolas", "Segoe UI Symbol", "Segoe UI Emoji", "Yu Gothic", "Microsoft YaHei",
        "Malgun Gothic", "Nirmala UI", "Ebrima", "Segoe UI Historic", "Cambria Math", "Segoe UI",
    ];

    private readonly ID2D1DeviceContext _dc;
    private readonly ID2D1SolidColorBrush _brush;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _canvas;
    private ID2D1Bitmap1? _canvasBitmap;

    // Faces per style (0 regular, 1 bold, 2 italic, 3 bold italic), each a fallback chain.
    private readonly IDWriteFontFace[][] _faces = new IDWriteFontFace[4][];
    private readonly bool[] _isEmojiFace = new bool[FallbackFamilies.Length + 1];
    private IDWriteTextFormat? _emojiFormat;
    private IDWriteTextFormat? _clusterFormat;
    private int _emojiFace = -1;
    private readonly Dictionary<ulong, (int Face, ushort Glyph)> _glyphs = [];
    private ushort[] _runGlyphs = [];
    private float[] _runAdvances = [];
    private float _emSize;
    private float _baseline;

    /// <summary>Left padding in pixels before column 0 (terminal.html uses 6 CSS px).</summary>
    public int OriginX { get; set; }
    public int CellWidth { get; private set; } = 8;
    public int CellHeight { get; private set; } = 17;
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public IDXGISwapChain1? SwapChain => _swapChain;

    /// <summary>Text for a cell whose grapheme has more than one codepoint (emoji sequences,
    /// combining marks). Set by the surface, which owns the terminal.</summary>
    public Func<int, int, string?>? GraphemeAt { get; set; }

    public GhosttyRenderer()
    {
        EnsureShared();
        _dc = s_d2dDevice!.CreateDeviceContext(DeviceContextOptions.None);
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;
        _brush = _dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
    }

    private static void EnsureShared()
    {
        if (s_device is not null)
            return;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        s_device = device;
        s_context = context;
        s_d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        s_d2dDevice = s_d2dFactory.CreateDevice(dxgiDevice);
        using var adapter = dxgiDevice.GetAdapter();
        s_dxgiFactory = adapter.GetParent<IDXGIFactory2>();
        s_dwrite = DWrite.DWriteCreateFactory<IDWriteFactory2>(DWFactoryType.Shared);
    }

    /// <summary>Loads the font chain and measures the cell. <paramref name="emPixels"/> is the
    /// font size in physical pixels (CSS px times the rasterization scale).</summary>
    public void SetFont(string fontFamilyList, float emPixels)
    {
        DisposeFaces();
        _emSize = emPixels;
        _glyphs.Clear();
        using var collection = s_dwrite!.GetSystemFontCollection(false);
        var requested = fontFamilyList.Split(',')
            .Select(f => f.Trim().Trim('"', '\''))
            .Where(f => f.Length > 0 && !f.Equals("monospace", StringComparison.OrdinalIgnoreCase));
        var families = requested.Concat(FallbackFamilies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => collection.FindFamilyName(f, out _))
            .ToArray();
        if (families.Length == 0)
            throw new InvalidOperationException("No usable terminal font installed.");

        (FontWeight, FontStyle)[] styles =
            [(FontWeight.Normal, FontStyle.Normal), (FontWeight.Bold, FontStyle.Normal),
             (FontWeight.Normal, FontStyle.Italic), (FontWeight.Bold, FontStyle.Italic)];
        for (var s = 0; s < 4; s++)
        {
            _faces[s] = [.. families.Select(f =>
            {
                collection.FindFamilyName(f, out var index);
                using var family = collection.GetFontFamily(index);
                using var font = family.GetFirstMatchingFont(styles[s].Item1, FontStretch.Normal, styles[s].Item2);
                return font.CreateFontFace();
            })];
        }
        Array.Clear(_isEmojiFace);
        _emojiFace = -1;
        for (var i = 0; i < families.Length && i < _isEmojiFace.Length; i++)
        {
            _isEmojiFace[i] = families[i].Equals("Segoe UI Emoji", StringComparison.OrdinalIgnoreCase);
            if (_isEmojiFace[i]) _emojiFace = i;
        }

        var primary = _faces[0][0];
        var metrics = primary.Metrics;
        var scale = emPixels / metrics.DesignUnitsPerEm;
        var zero = primary.GetGlyphIndices([(uint)'0']);
        var advance = primary.GetDesignGlyphMetrics(zero, false)[0].AdvanceWidth * scale;
        CellWidth = Math.Max(1, (int)MathF.Round(advance));
        CellHeight = Math.Max(1, (int)MathF.Ceiling((metrics.Ascent + metrics.Descent + metrics.LineGap) * scale));
        _baseline = MathF.Round(metrics.Ascent * scale + metrics.LineGap * scale / 2);

        _emojiFormat?.Dispose();
        _emojiFormat = s_dwrite.CreateTextFormat("Segoe UI Emoji", FontWeight.Normal, FontStyle.Normal, emPixels * 0.9f);
        _emojiFormat.TextAlignment = TextAlignment.Center;
        _emojiFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _emojiFormat.WordWrapping = WordWrapping.NoWrap;
        _clusterFormat?.Dispose();
        _clusterFormat = s_dwrite.CreateTextFormat(families[0], FontWeight.Normal, FontStyle.Normal, emPixels);
        _clusterFormat.TextAlignment = TextAlignment.Leading;
        _clusterFormat.ParagraphAlignment = ParagraphAlignment.Near;
        _clusterFormat.WordWrapping = WordWrapping.NoWrap;
    }

    /// <summary>Sizes the swap chain and canvas in physical pixels. Returns true when the
    /// swap chain was created (the caller must attach it to the panel).</summary>
    public bool Resize(int pixelWidth, int pixelHeight)
    {
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        if (pixelWidth == PixelWidth && pixelHeight == PixelHeight && _swapChain is not null)
            return false;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;

        _dc.Target = null;
        _canvasBitmap?.Dispose();
        _canvas?.Dispose();
        _canvas = s_device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)pixelWidth, (uint)pixelHeight, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using (var surface = _canvas.QueryInterface<IDXGISurface>())
            _canvasBitmap = _dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
        _dc.Target = _canvasBitmap;

        var created = false;
        if (_swapChain is null)
        {
            _swapChain = s_dxgiFactory!.CreateSwapChainForComposition(s_device, new SwapChainDescription1
            {
                Width = (uint)pixelWidth,
                Height = (uint)pixelHeight,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = DxgiAlphaMode.Ignore,
            });
            created = true;
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)pixelWidth, (uint)pixelHeight, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }
        return created;
    }

    /// <summary>Maps the physical-pixel swap chain onto the panel's DIP layout.</summary>
    public void SetCompositionScale(float scaleX, float scaleY)
    {
        if (_swapChain is null)
            return;
        using var chain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        chain2.MatrixTransform = Matrix3x2.CreateScale(1 / scaleX, 1 / scaleY);
    }

    public void ReleaseSwapChain()
    {
        _dc.Target = null;
        _canvasBitmap?.Dispose();
        _canvasBitmap = null;
        _canvas?.Dispose();
        _canvas = null;
        _swapChain?.Dispose();
        _swapChain = null;
        PixelWidth = PixelHeight = 0;
    }

    private (int Face, ushort Glyph) Lookup(uint codepoint, int style)
    {
        var key = ((ulong)(uint)style << 32) | codepoint;
        if (_glyphs.TryGetValue(key, out var hit))
            return hit;
        var faces = _faces[style];
        (int, ushort) result = (0, 0);
        // Pictographs default to emoji presentation: take the color font before symbol fonts
        // that carry monochrome versions of the same codepoints.
        if (IsEmojiPresentation(codepoint) && _emojiFace >= 0)
        {
            var glyph = faces[_emojiFace].GetGlyphIndices([codepoint])[0];
            if (glyph != 0)
            {
                _glyphs[key] = (_emojiFace, glyph);
                return (_emojiFace, glyph);
            }
        }
        for (var i = 0; i < faces.Length; i++)
        {
            var glyph = faces[i].GetGlyphIndices([codepoint])[0];
            if (glyph != 0) { result = (i, glyph); break; }
        }
        _glyphs[key] = result;
        return result;
    }

    private static Color4 Rgb(uint c, float alpha = 1f) =>
        new(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, alpha);

    /// <summary>Draws the rows flagged in <paramref name="dirty"/> (clearing the flags), then
    /// presents. <paramref name="full"/> also repaints the margin outside the grid.</summary>
    public void Draw(GhosttyCell* cells, int cols, int rows, byte* dirty, in GhosttyFrameInfo info,
        bool cursorShown, bool focused, uint selectionColor, bool full)
    {
        if (_swapChain is null || _canvas is null)
            return;
        if (_runGlyphs.Length < cols)
        {
            _runGlyphs = new ushort[cols];
            _runAdvances = new float[cols];
        }

        _dc.BeginDraw();
        if (full)
        {
            _brush.Color = Rgb(info.DefaultBackground);
            _dc.FillRectangle(new Rect(0, 0, PixelWidth, PixelHeight), _brush);
        }
        for (var y = 0; y < rows; y++)
        {
            if (dirty[y] == 0 && !full)
                continue;
            dirty[y] = 0;
            DrawRow(cells + y * cols, cols, y, info.DefaultBackground, selectionColor);
        }
        if (cursorShown && info.CursorVisible != 0 && info.CursorY < rows && info.CursorX < cols)
            DrawCursor(cells + info.CursorY * cols + info.CursorX, info, focused);
        _dc.EndDraw();

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            s_context!.CopyResource(backBuffer, _canvas);
        _swapChain.Present(1, PresentFlags.None);
    }

    private void DrawRow(GhosttyCell* row, int cols, int y, uint defaultBackground, uint selectionColor)
    {
        float top = y * CellHeight;
        _brush.Color = Rgb(defaultBackground);
        _dc.FillRectangle(new Rect(0, top, PixelWidth, CellHeight), _brush);

        // Background runs; selection overrides the cell background.
        for (var x = 0; x < cols;)
        {
            var selected = (row[x].Flags & GhosttyCell.Selected) != 0;
            if (!selected && (row[x].Flags & GhosttyCell.DefaultBackground) != 0) { x++; continue; }
            var bg = selected ? selectionColor : row[x].Background;
            var start = x;
            while (x < cols)
            {
                var s = (row[x].Flags & GhosttyCell.Selected) != 0;
                var b = s ? selectionColor : row[x].Background;
                if ((!s && (row[x].Flags & GhosttyCell.DefaultBackground) != 0) || b != bg) break;
                x++;
            }
            _brush.Color = Rgb(bg);
            _dc.FillRectangle(new Rect(OriginX + start * CellWidth, top, (x - start) * CellWidth, CellHeight), _brush);
        }

        // Glyph runs: same face, style and color; every glyph advances whole cells.
        var baseline = top + _baseline;
        for (var x = 0; x < cols;)
        {
            ref var c = ref row[x];
            if (c.Codepoint is 0 or ' ' || c.Wide == GhosttyCell.SpacerTail || (c.Flags & GhosttyCell.Invisible) != 0)
            {
                x++;
                continue;
            }
            if (c.GraphemeLength > 1 || IsEmojiCell(c))
            {
                DrawCluster(x, y, c);
                x += c.Wide == GhosttyCell.WideChar ? 2 : 1;
                continue;
            }
            var style = StyleOf(c.Flags);
            var (face, _) = Lookup(c.Codepoint, style);
            var fg = c.Foreground;
            var faint = (c.Flags & GhosttyCell.Faint) != 0;
            var start = x;
            var n = 0;
            while (x < cols)
            {
                ref var d = ref row[x];
                if (d.Wide == GhosttyCell.SpacerTail) { x++; continue; }
                if (d.Codepoint is 0 or ' ')
                {
                    if (n > 0) { _runAdvances[n - 1] += CellWidth; x++; continue; }
                    break;
                }
                if (d.Foreground != fg || StyleOf(d.Flags) != style || ((d.Flags & GhosttyCell.Faint) != 0) != faint
                    || d.GraphemeLength > 1 || (d.Flags & GhosttyCell.Invisible) != 0)
                    break;
                var (f, g) = Lookup(d.Codepoint, style);
                if (f != face || _isEmojiFace[f]) break;
                _runGlyphs[n] = g;
                _runAdvances[n] = d.Wide == GhosttyCell.WideChar ? 2 * CellWidth : CellWidth;
                n++;
                x++;
            }
            if (n == 0) { x++; continue; }
            _brush.Color = Rgb(fg, faint ? 0.55f : 1f);
            var run = new GlyphRun
            {
                FontFace = _faces[style][face],
                FontEmSize = _emSize,
                Indices = _runGlyphs.AsSpan(0, n).ToArray(),
                Advances = _runAdvances.AsSpan(0, n).ToArray(),
            };
            _dc.DrawGlyphRun(new Vector2(OriginX + start * CellWidth, baseline), run, _brush, MeasuringMode.Natural);
            for (var i = start; i < x; i++)
            {
                var flags = row[i].Flags;
                if ((flags & GhosttyCell.Underline) != 0)
                    _dc.FillRectangle(new Rect(OriginX + i * CellWidth, baseline + 2, CellWidth, 1), _brush);
                if ((flags & GhosttyCell.Strike) != 0)
                    _dc.FillRectangle(new Rect(OriginX + i * CellWidth, top + CellHeight / 2, CellWidth, 1), _brush);
            }
        }
    }

    private static bool IsEmojiPresentation(uint codepoint) => codepoint is >= 0x1F000 and <= 0x1FAFF;

    private bool IsEmojiCell(in GhosttyCell c) =>
        IsEmojiPresentation(c.Codepoint) && _isEmojiFace[Lookup(c.Codepoint, 0).Face];

    private static int StyleOf(ushort flags) =>
        ((flags & GhosttyCell.Bold) != 0 ? 1 : 0) | ((flags & GhosttyCell.Italic) != 0 ? 2 : 0);

    /// <summary>Grapheme clusters and color emoji go through DirectWrite layout, centered on
    /// their cells, so shaping and color fonts work without a glyph-run path for them.</summary>
    private void DrawCluster(int x, int y, in GhosttyCell c)
    {
        var text = c.GraphemeLength > 1 ? GraphemeAt?.Invoke(x, y) : null;
        text ??= char.ConvertFromUtf32((int)c.Codepoint);
        var width = (c.Wide == GhosttyCell.WideChar ? 2 : 1) * CellWidth;
        _brush.Color = Rgb(c.Foreground);
        var emoji = IsEmojiPresentation(c.Codepoint) || text.Contains('️');
        if (emoji)
        {
            _dc.DrawText(text, _emojiFormat!, new Rect(OriginX + x * CellWidth, y * CellHeight, width, CellHeight), _brush,
                DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
            return;
        }
        // Base character plus combining marks: lay out in the terminal font on the baseline.
        using var layout = s_dwrite!.CreateTextLayout(text, _clusterFormat!, width, CellHeight);
        var lineMetrics = layout.LineMetrics;
        var offset = lineMetrics.Length > 0 ? _baseline - lineMetrics[0].Baseline : 0;
        _dc.DrawTextLayout(new Vector2(OriginX + x * CellWidth, y * CellHeight + offset), layout, _brush,
            DrawTextOptions.Clip);
    }

    private void DrawCursor(GhosttyCell* cell, in GhosttyFrameInfo info, bool focused)
    {
        float left = OriginX + info.CursorX * CellWidth, top = info.CursorY * CellHeight;
        var width = cell->Wide == GhosttyCell.WideChar ? 2 * CellWidth : CellWidth;
        _brush.Color = Rgb(info.CursorColor);
        if (!focused || info.CursorStyle == 3)
        {
            _dc.DrawRectangle(new Rect(left + 0.5f, top + 0.5f, width - 1, CellHeight - 1), _brush, 1);
            return;
        }
        switch (info.CursorStyle)
        {
            case 1:
                _dc.FillRectangle(new Rect(left, top, Math.Max(2, CellWidth / 8), CellHeight), _brush);
                return;
            case 2:
                _dc.FillRectangle(new Rect(left, top + CellHeight - 2, width, 2), _brush);
                return;
        }
        _dc.FillRectangle(new Rect(left, top, width, CellHeight), _brush);
        if (cell->Codepoint is 0 or ' ')
            return;
        // Re-draw the covered glyph in the background color so it stays readable.
        var style = StyleOf(cell->Flags);
        var (face, glyph) = Lookup(cell->Codepoint, style);
        if (_isEmojiFace[face] || cell->GraphemeLength > 1)
            return;
        _brush.Color = Rgb(info.DefaultBackground);
        var run = new GlyphRun
        {
            FontFace = _faces[style][face],
            FontEmSize = _emSize,
            Indices = [glyph],
            Advances = [width],
        };
        _dc.DrawGlyphRun(new Vector2(left, top + _baseline), run, _brush, MeasuringMode.Natural);
    }

    private void DisposeFaces()
    {
        foreach (var chain in _faces)
            if (chain is not null)
                foreach (var face in chain)
                    face.Dispose();
    }

    public void Dispose()
    {
        ReleaseSwapChain();
        DisposeFaces();
        _emojiFormat?.Dispose();
        _clusterFormat?.Dispose();
        _brush.Dispose();
        _dc.Dispose();
    }
}
