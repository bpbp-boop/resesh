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

namespace Resesh.Terminal.Native;

/// <summary>
/// Draws a flattened libghostty-vt grid with Direct2D/DirectWrite into a composition swap chain
/// for a SwapChainPanel. Dirty rows are drawn into a retained canvas texture; each present copies
/// the canvas to the back buffer, because flip-model buffers do not keep their contents.
/// UI thread only.
/// </summary>
internal sealed unsafe partial class TerminalRenderer : IDisposable
{
    // One device for every terminal: D2D objects are cheap per surface, devices are not.
    private static ID3D11Device? s_device;
    private static ID3D11DeviceContext? s_context;
    private static ID2D1Factory1? s_d2dFactory;
    private static ID2D1Device? s_d2dDevice;
    private static IDWriteFactory2? s_dwrite;
    private static IDXGIFactory2? s_dxgiFactory;
    private static readonly HashSet<TerminalRenderer> s_renderers = [];
    private static int s_uiThread;
    private static int s_generation;
    private static bool s_recovering;

    // Shipped beside the app for the Nerd Font icons prompts and status lines use. An installed
    // copy of the family wins; either way it sits ahead of Segoe UI Symbol, which has unrelated
    // glyphs on some of the same private-use codepoints.
    private const string SymbolsFamily = "Symbols Nerd Font Mono";
    private static readonly string SymbolsFontPath =
        Path.Combine(AppContext.BaseDirectory, "Fonts", "SymbolsNerdFontMono-Regular.ttf");

    private static readonly string[] FallbackFamilies =
    [
        "Cascadia Mono", "Consolas", SymbolsFamily, "Segoe UI Symbol", "Segoe UI Emoji", "Yu Gothic", "Microsoft YaHei",
        "Malgun Gothic", "Nirmala UI", "Ebrima", "Segoe UI Historic", "Cambria Math", "Segoe UI",
    ];

    private ID2D1DeviceContext _dc = null!;
    private ID2D1SolidColorBrush _brush = null!;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _canvas;
    private ID2D1Bitmap1? _canvasBitmap;

    private static readonly (FontWeight Weight, FontStyle Style)[] Styles =
    [
        (FontWeight.Normal, FontStyle.Normal), (FontWeight.Bold, FontStyle.Normal),
        (FontWeight.Normal, FontStyle.Italic), (FontWeight.Bold, FontStyle.Italic),
    ];

    // Faces per style (0 regular, 1 bold, 2 italic, 3 bold italic), each a fallback chain. The
    // chain starts as the configured families plus FallbackFamilies and grows by whatever the
    // system's font fallback finds for characters none of those cover.
    private readonly List<IDWriteFontFace>[] _faces = new List<IDWriteFontFace>[4];
    private readonly List<string> _families = [];
    private readonly List<bool> _isEmojiFace = [];
    private IDWriteTextFormat? _emojiFormat;
    private IDWriteTextFormat? _clusterFormat;
    private int _emojiFace = -1;
    private int _symbolsFace = -1;
    private IDWriteRenderingParams? _symbolParams;
    private readonly Dictionary<ulong, (int Face, ushort Glyph)> _glyphs = [];
    private ushort[] _runGlyphs = [];
    private float[] _runAdvances = [];
    private float _emSize;
    private float _baseline;
    private string? _fontFamilyList;
    private float _compositionScaleX = 1;
    private float _compositionScaleY = 1;
    private int _generation;
    private bool _disposed;

    /// <summary>Left padding in pixels before column 0 (terminal.html uses 6 CSS px).</summary>
    public int OriginX { get; set; }
    public int CellWidth { get; private set; } = 8;
    public int CellHeight { get; private set; } = 17;
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public IDXGISwapChain1? SwapChain => _swapChain;
    public event Action? SwapChainChanged;

    /// <summary>Text for a cell whose grapheme has more than one codepoint (emoji sequences,
    /// combining marks). Set by the surface, which owns the terminal.</summary>
    public Func<int, int, string?>? GraphemeAt { get; set; }

    public TerminalRenderer()
    {
        EnsureShared();
        CreateDeviceResources();
        s_renderers.Add(this);
    }

    private void CreateDeviceResources()
    {
        _dc = s_d2dDevice!.CreateDeviceContext(DeviceContextOptions.None);
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;
        _brushColor = new Color4(1, 1, 1, 1);
        _brush = _dc.CreateSolidColorBrush(_brushColor);
        _generation = s_generation;
    }

    private static void EnsureShared()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (s_uiThread != 0 && s_uiThread != thread)
            throw new InvalidOperationException("Terminal graphics resources must be used on their UI thread.");
        s_uiThread = thread;
        if (s_device is not null)
            return;
        try
        {
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
            s_generation++;
        }
        catch
        {
            DisposeShared();
            throw;
        }
    }

    private static void DisposeShared()
    {
        s_context?.ClearState();
        s_context?.Flush();
        s_dwrite?.Dispose();
        s_dwrite = null;
        s_d2dDevice?.Dispose();
        s_d2dDevice = null;
        s_d2dFactory?.Dispose();
        s_d2dFactory = null;
        s_dxgiFactory?.Dispose();
        s_dxgiFactory = null;
        s_context?.Dispose();
        s_context = null;
        s_device?.Dispose();
        s_device = null;
    }

    private static void RecoverShared(int observedGeneration)
    {
        if (Environment.CurrentManagedThreadId != s_uiThread)
            throw new InvalidOperationException("Terminal graphics recovery must run on the UI thread.");
        if (s_recovering || observedGeneration != s_generation)
            return;
        s_recovering = true;
        var renderers = s_renderers.ToArray();
        try
        {
            foreach (var renderer in renderers)
            {
                if (renderer._disposed)
                    continue;
                renderer.DisposeDeviceResources();
                renderer.SwapChainChanged?.Invoke();
            }
            DisposeShared();
            EnsureShared();
            foreach (var renderer in renderers)
            {
                if (renderer._disposed)
                    continue;
                renderer.CreateDeviceResources();
                if (renderer._fontFamilyList is { } family)
                    renderer.SetFont(family, renderer._emSize);
                if (renderer.PixelWidth > 0 && renderer.PixelHeight > 0)
                {
                    renderer.Resize(renderer.PixelWidth, renderer.PixelHeight);
                    renderer.SetCompositionScale(renderer._compositionScaleX, renderer._compositionScaleY);
                }
            }
            // Notify only after every renderer is rebuilt; callbacks can schedule another frame.
            foreach (var renderer in renderers)
                if (!renderer._disposed)
                    renderer.SwapChainChanged?.Invoke();
        }
        finally
        {
            s_recovering = false;
        }
    }

    /// <summary>Loads the font chain and measures the cell. <paramref name="emPixels"/> is the
    /// font size in physical pixels (CSS px times the rasterization scale).</summary>
    public void SetFont(string fontFamilyList, float emPixels)
    {
        DisposeFaces();
        _fontFamilyList = fontFamilyList;
        _emSize = emPixels;
        _glyphs.Clear();
        using var collection = s_dwrite!.GetSystemFontCollection(false);
        var requested = fontFamilyList.Split(',')
            .Select(f => f.Trim().Trim('"', '\''))
            .Where(f => f.Length > 0 && !f.Equals("monospace", StringComparison.OrdinalIgnoreCase));
        var families = requested.Concat(FallbackFamilies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => collection.FindFamilyName(f, out _) || (f == SymbolsFamily && File.Exists(SymbolsFontPath)))
            .ToArray();
        if (families.Length == 0)
            throw new InvalidOperationException("No usable terminal font installed.");

        foreach (var name in families)
        {
            if (!collection.FindFamilyName(name, out var index))
            {
                AddSymbolsFont();
                continue;
            }
            using var family = collection.GetFontFamily(index);
            AddFamily(name, family);
        }
        _emojiFace = _isEmojiFace.IndexOf(true);
        _symbolsFace = _families.FindIndex(f => f.Equals(SymbolsFamily, StringComparison.OrdinalIgnoreCase));
        if (_symbolParams is null)
        {
            // The icon font is unhinted, and at text sizes DirectWrite's default mode smooths
            // only horizontally, which leaves icon curves and diagonals stair-stepped. Icons
            // draw smoothed both ways, in grayscale: ClearType fringes their solid shapes.
            using var defaults = s_dwrite.CreateRenderingParams();
            _symbolParams = s_dwrite.CreateCustomRenderingParams(defaults.Gamma, defaults.EnhancedContrast,
                defaults.ClearTypeLevel, defaults.PixelGeometry, RenderingMode.NaturalSymmetric);
        }

        var primary = _faces[0][0];
        var metrics = primary.Metrics;
        var scale = emPixels / metrics.DesignUnitsPerEm;
        var zero = primary.GetGlyphIndices([(uint)'0']);
        var advance = primary.GetDesignGlyphMetrics(zero, false)[0].AdvanceWidth * scale;
        CellWidth = Math.Max(1, (int)MathF.Round(advance));
        CellHeight = Math.Max(1, (int)MathF.Ceiling((metrics.Ascent + metrics.Descent + metrics.LineGap) * scale));
        _baseline = MathF.Round(metrics.Ascent * scale + metrics.LineGap * scale / 2);
        // Rounded up as Ghostty does, which keeps lines from going thin as the font grows. A
        // double line is three of these wide, so keep it to a fifth of the cell.
        _boxThickness = Math.Clamp((int)MathF.Ceiling(metrics.UnderlineThickness * scale), 1, Math.Max(1, CellWidth / 5));
        DisposeGlyphGeometries();

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

    private int AddFamily(string name, IDWriteFontFamily family)
    {
        for (var s = 0; s < Styles.Length; s++)
        {
            using var font = family.GetFirstMatchingFont(Styles[s].Weight, FontStretch.Normal, Styles[s].Style);
            _faces[s].Add(font.CreateFontFace());
        }
        _families.Add(name);
        _isEmojiFace.Add(name.Equals("Segoe UI Emoji", StringComparison.OrdinalIgnoreCase));
        return _families.Count - 1;
    }

    /// <summary>The bundled icon font, loaded straight from its file. Icons have one design, so
    /// every style uses it unchanged.</summary>
    private void AddSymbolsFont()
    {
        using var file = s_dwrite!.CreateFontFileReference(SymbolsFontPath);
        foreach (var chain in _faces)
            chain.Add(s_dwrite.CreateFontFace(FontFaceType.Truetype, [file], 0, FontSimulations.None)!);
        _families.Add(SymbolsFamily);
        _isEmojiFace.Add(false);
    }

    /// <summary>The face for a character nothing in the chain covers: the font Windows itself
    /// would fall back to, added to the chain on first use. -1 when no installed font has it.</summary>
    private int FallbackFace(uint codepoint)
    {
        using var family = SystemFontFallback.Map(s_dwrite!, codepoint, _families[0]);
        if (family is null)
            return -1;
        var name = SystemFontFallback.NameOf(family);
        var index = _families.FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : AddFamily(name, family);
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
        _compositionScaleX = scaleX;
        _compositionScaleY = scaleY;
        if (_swapChain is null)
            return;
        using var chain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        chain2.MatrixTransform = Matrix3x2.CreateScale(1 / scaleX, 1 / scaleY);
    }

    private void ReleaseSwapChain()
    {
        if (_dc is not null)
            _dc.Target = null;
        _canvasBitmap?.Dispose();
        _canvasBitmap = null;
        _canvas?.Dispose();
        _canvas = null;
        _swapChain?.Dispose();
        _swapChain = null;
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
        for (var i = 0; i < faces.Count; i++)
        {
            var glyph = faces[i].GetGlyphIndices([codepoint])[0];
            if (glyph != 0) { result = (i, glyph); break; }
        }
        if (result.Item2 == 0 && FallbackFace(codepoint) is >= 0 and var fallback)
        {
            var glyph = faces[fallback].GetGlyphIndices([codepoint])[0];
            if (glyph != 0) result = (fallback, glyph);
        }
        _glyphs[key] = result;
        return result;
    }

    // The brush's Color setter is a COM call; consecutive runs mostly share a color.
    private Color4 _brushColor = new(1, 1, 1, 1);

    private void SetBrush(Color4 color)
    {
        if (color.Equals(_brushColor))
            return;
        _brushColor = color;
        _brush.Color = color;
    }

    private static Color4 Rgb(uint c, float alpha = 1f) =>
        new(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, alpha);

    /// <summary>Draws the rows flagged in <paramref name="dirty"/> (clearing the flags), then
    /// presents. <paramref name="full"/> also repaints the margin outside the grid.</summary>
    /// <summary>Find highlight colors (terminal.html's search decorations).</summary>
    private const uint MatchColor = 0x515C6A, CurrentMatchColor = 0xF2CC60;

    private bool _extendedMargin;

    /// <summary>The one background the last column carries on every row while the margin is
    /// extended, or null. The surface continues it across the ruler lane and scroll bar,
    /// which sit outside the swap chain.</summary>
    public uint? MarginColor { get; private set; }

    public bool Draw(TerminalCell* cells, int cols, int rows, byte* dirty, in GhosttyFrameInfo info,
        bool cursorShown, bool focused, uint selectionColor, bool full)
    {
        if (_disposed || _swapChain is null || _canvas is null)
            return false;
        var observedGeneration = _generation;
        if (_runGlyphs.Length < cols)
        {
            _runGlyphs = new ushort[cols];
            _runAdvances = new float[cols];
        }

        _dc.BeginDraw();
        if (full)
        {
            SetBrush(Rgb(info.DefaultBackground));
            _dc.FillRectangle(new Rect(0, 0, PixelWidth, PixelHeight), _brush);
        }
        // A full-screen app paints its own background on every cell, which leaves the margin
        // around the grid as a frame in the theme color, so the edge cells extend into it
        // instead. That applies on the alternate screen, and whenever both edge columns are
        // painted on every row: tmux persistence keeps the outer alternate screen off, so a
        // full-screen app inside it is only recognizable by what it paints. A shell keeps the
        // theme color, so a colored prompt segment at column 0 does not bleed outward.
        var extend = info.Alternate != 0;
        uint? rightEdge = null;
        if (rows > 0 && cols > 0)
        {
            var painted = true;
            var uniform = true;
            rightEdge = MarginBackgroundOf(cells[cols - 1]);
            for (var y = 0; y < rows; y++)
            {
                var right = MarginBackgroundOf(cells[y * cols + cols - 1]);
                painted &= right is not null && MarginBackgroundOf(cells[y * cols]) is not null;
                uniform &= right == rightEdge;
            }
            extend |= painted;
            if (!uniform)
                rightEdge = null;
        }
        MarginColor = extend ? rightEdge : null;
        if (extend != _extendedMargin)
        {
            // Starting or stopping the extension changes every row's margin.
            _extendedMargin = extend;
            full = true;
        }
        if (rows > 0 && (full || dirty[rows - 1] != 0))
            DrawBottomMargin(cells + (rows - 1) * cols, cols, rows, info.DefaultBackground, extend);
        for (var y = 0; y < rows; y++)
        {
            if (dirty[y] == 0 && !full)
                continue;
            dirty[y] = 0;
            DrawRow(cells + y * cols, cols, y, info.DefaultBackground, selectionColor, extend);
        }
        if (cursorShown && info.CursorVisible != 0 && info.CursorY < rows && info.CursorX < cols)
            DrawCursor(cells + info.CursorY * cols + info.CursorX, info, focused);
        var endDraw = _dc.EndDraw();
        if (endDraw == Vortice.Direct2D1.ResultCode.RecreateTarget)
        {
            RecoverShared(observedGeneration);
            return false;
        }
        endDraw.CheckError();

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            s_context!.CopyResource(backBuffer, _canvas);
        var present = _swapChain.Present(1, PresentFlags.None);
        if (present == Vortice.DXGI.ResultCode.DeviceRemoved
            || present == Vortice.DXGI.ResultCode.DeviceReset
            || present == Vortice.DXGI.ResultCode.DeviceHung)
        {
            RecoverShared(observedGeneration);
            return false;
        }
        present.CheckError();
        return true;
    }

    /// <summary>The strip below the last row, which no row repaint covers. When extending,
    /// each column continues the last row's background and the corners follow the end cells.</summary>
    private void DrawBottomMargin(TerminalCell* row, int cols, int rows, uint defaultBackground, bool extend)
    {
        float top = rows * CellHeight, height = PixelHeight - top;
        if (height <= 0)
            return;
        SetBrush(Rgb(defaultBackground));
        _dc.FillRectangle(new Rect(0, top, PixelWidth, height), _brush);
        if (!extend)
            return;
        for (var x = 0; x < cols;)
        {
            if (MarginBackgroundOf(row[x]) is not { } bg) { x++; continue; }
            var start = x;
            while (x < cols && MarginBackgroundOf(row[x]) == bg)
                x++;
            float left = start == 0 ? 0 : OriginX + start * CellWidth;
            float right = x == cols ? PixelWidth : OriginX + x * CellWidth;
            SetBrush(Rgb(bg));
            _dc.FillRectangle(new Rect(left, top, right - left, height), _brush);
        }
    }

    private void DrawRow(TerminalCell* row, int cols, int y, uint defaultBackground, uint selectionColor, bool extend)
    {
        float top = y * CellHeight;
        SetBrush(Rgb(defaultBackground));
        _dc.FillRectangle(new Rect(0, top, PixelWidth, CellHeight), _brush);
        if (extend && cols > 0)
        {
            if (MarginBackgroundOf(row[0]) is { } leftBg)
            {
                SetBrush(Rgb(leftBg));
                _dc.FillRectangle(new Rect(0, top, OriginX, CellHeight), _brush);
            }
            float gridRight = OriginX + cols * CellWidth;
            if (gridRight < PixelWidth && MarginBackgroundOf(row[cols - 1]) is { } rightBg)
            {
                SetBrush(Rgb(rightBg));
                _dc.FillRectangle(new Rect(gridRight, top, PixelWidth - gridRight, CellHeight), _brush);
            }
        }

        // Background runs: selection, then find matches, override the cell background.
        for (var x = 0; x < cols;)
        {
            if (BackgroundOf(row[x], selectionColor) is not { } bg) { x++; continue; }
            var start = x;
            while (x < cols && BackgroundOf(row[x], selectionColor) == bg)
                x++;
            SetBrush(Rgb(bg));
            _dc.FillRectangle(new Rect(OriginX + start * CellWidth, top, (x - start) * CellWidth, CellHeight), _brush);
        }

        // Glyph runs: same face, style and color; every glyph advances whole cells.
        var baseline = top + _baseline;
        for (var x = 0; x < cols;)
        {
            ref var c = ref row[x];
            if (c.Codepoint is 0 or ' ' || c.Wide == TerminalCell.SpacerTail || (c.Flags & TerminalCell.Invisible) != 0)
            {
                x++;
                continue;
            }
            if (c.GraphemeLength > 1 || IsEmojiCell(c))
            {
                DrawCluster(x, y, c);
                x += c.Wide == TerminalCell.WideChar ? 2 : 1;
                continue;
            }
            var fg = c.Foreground;
            var faint = (c.Flags & TerminalCell.Faint) != 0;
            if (BuiltinGlyphs.Covers(c.Codepoint))
            {
                DrawBuiltinGlyph(c.Codepoint, OriginX + x * CellWidth, top, fg,
                    BackgroundOf(c, selectionColor) ?? defaultBackground, faint ? 0.55f : 1f);
                x++;
                continue;
            }
            var style = StyleOf(c.Flags);
            var (face, _) = Lookup(c.Codepoint, style);
            var start = x;
            var n = 0;
            while (x < cols)
            {
                ref var d = ref row[x];
                if (d.Wide == TerminalCell.SpacerTail) { x++; continue; }
                if (d.Codepoint is 0 or ' ')
                {
                    if (n > 0) { _runAdvances[n - 1] += CellWidth; x++; continue; }
                    break;
                }
                if (d.Foreground != fg || StyleOf(d.Flags) != style || ((d.Flags & TerminalCell.Faint) != 0) != faint
                    || d.GraphemeLength > 1 || (d.Flags & TerminalCell.Invisible) != 0 || BuiltinGlyphs.Covers(d.Codepoint))
                    break;
                var (f, g) = Lookup(d.Codepoint, style);
                if (f != face || _isEmojiFace[f]) break;
                _runGlyphs[n] = g;
                _runAdvances[n] = d.Wide == TerminalCell.WideChar ? 2 * CellWidth : CellWidth;
                n++;
                x++;
            }
            if (n == 0) { x++; continue; }
            SetBrush(Rgb(fg, faint ? 0.55f : 1f));
            var run = new GlyphRun
            {
                FontFace = _faces[style][face],
                FontEmSize = _emSize,
                Indices = _runGlyphs.AsSpan(0, n).ToArray(),
                Advances = _runAdvances.AsSpan(0, n).ToArray(),
            };
            DrawRun(new Vector2(OriginX + start * CellWidth, baseline), run, face);
        }
        DrawDecorations(row, cols, top, baseline);
    }

    private void DrawRun(Vector2 origin, GlyphRun run, int face)
    {
        if (face != _symbolsFace)
        {
            _dc.DrawGlyphRun(origin, run, _brush, MeasuringMode.Natural);
            return;
        }
        _dc.TextRenderingParams = _symbolParams;
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _dc.DrawGlyphRun(origin, run, _brush, MeasuringMode.Natural);
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;
        _dc.TextRenderingParams = null;
    }

    private const ushort DecorationFlags = TerminalCell.Underline | TerminalCell.Strike | TerminalCell.Overline;

    /// <summary>Underlines (single, double, curly, dotted, dashed; SGR 58 colors), overlines and
    /// strikethrough for every cell, blanks included (links and highlights underline spaces too).</summary>
    private void DrawDecorations(TerminalCell* row, int cols, float top, float baseline)
    {
        float thickness = Math.Max(1, (int)Math.Round(CellHeight / 16f));
        var underlineY = Math.Min(baseline + 2, top + CellHeight - 2 * thickness);
        for (var x = 0; x < cols; x++)
        {
            ref var c = ref row[x];
            if ((c.Flags & DecorationFlags) == 0 || c.Wide == TerminalCell.SpacerTail || (c.Flags & TerminalCell.Invisible) != 0)
                continue;
            float left = OriginX + x * CellWidth;
            float width = c.Wide == TerminalCell.WideChar ? 2 * CellWidth : CellWidth;
            var alpha = (c.Flags & TerminalCell.Faint) != 0 ? 0.55f : 1f;
            if ((c.Flags & TerminalCell.Underline) != 0)
            {
                SetBrush(Rgb(c.UnderlineColor == TerminalCell.DefaultUnderlineColor ? c.Foreground : c.UnderlineColor, alpha));
                switch (c.UnderlineStyle)
                {
                    case 2: // double
                        _dc.FillRectangle(new Rect(left, underlineY - thickness, width, thickness), _brush);
                        _dc.FillRectangle(new Rect(left, underlineY + thickness, width, thickness), _brush);
                        break;
                    case 3: // curly: one wave per cell, continuous across cells
                    {
                        var amplitude = Math.Max(1f, thickness);
                        var steps = Math.Max(4, (int)width / 2);
                        var previous = new Vector2(left, underlineY);
                        for (var i = 1; i <= steps; i++)
                        {
                            var px = left + width * i / steps;
                            var phase = (px - OriginX) / CellWidth * 2 * MathF.PI;
                            var next = new Vector2(px, underlineY + amplitude * MathF.Sin(phase));
                            _dc.DrawLine(previous, next, _brush, thickness);
                            previous = next;
                        }
                        break;
                    }
                    case 4: // dotted
                        for (var dx = 0f; dx < width; dx += 2 * thickness)
                            _dc.FillRectangle(new Rect(left + dx, underlineY, thickness, thickness), _brush);
                        break;
                    case 5: // dashed
                        var dash = Math.Max(2f, CellWidth / 2f);
                        for (var dx = 0f; dx < width; dx += CellWidth)
                            _dc.FillRectangle(new Rect(left + dx, underlineY, Math.Min(dash, width - dx), thickness), _brush);
                        break;
                    default: // single
                        _dc.FillRectangle(new Rect(left, underlineY, width, thickness), _brush);
                        break;
                }
            }
            if ((c.Flags & (TerminalCell.Strike | TerminalCell.Overline)) != 0)
            {
                SetBrush(Rgb(c.Foreground, alpha));
                if ((c.Flags & TerminalCell.Strike) != 0)
                    _dc.FillRectangle(new Rect(left, top + CellHeight / 2, width, thickness), _brush);
                if ((c.Flags & TerminalCell.Overline) != 0)
                    _dc.FillRectangle(new Rect(left, top, width, thickness), _brush);
            }
        }
    }

    private static bool IsEmojiPresentation(uint codepoint) => codepoint is >= 0x1F000 and <= 0x1FAFF;

    private static uint? BackgroundOf(in TerminalCell c, uint selectionColor) =>
        (c.Flags & TerminalCell.Selected) != 0 ? selectionColor
        : (c.Flags & TerminalCell.MatchCurrent) != 0 ? CurrentMatchColor
        : (c.Flags & TerminalCell.Match) != 0 ? MatchColor
        : (c.Flags & TerminalCell.DefaultBackground) != 0 ? null
        : c.Background;

    /// <summary>The background a cell lends to the margin beside it: the app's own color only,
    /// so a selection or find match at the edge does not spill out of the grid.</summary>
    private static uint? MarginBackgroundOf(in TerminalCell c) =>
        (c.Flags & TerminalCell.DefaultBackground) != 0 ? null : c.Background;

    private bool IsEmojiCell(in TerminalCell c) =>
        IsEmojiPresentation(c.Codepoint) && _isEmojiFace[Lookup(c.Codepoint, 0).Face];

    private static int StyleOf(ushort flags) =>
        ((flags & TerminalCell.Bold) != 0 ? 1 : 0) | ((flags & TerminalCell.Italic) != 0 ? 2 : 0);

    /// <summary>Grapheme clusters and color emoji go through DirectWrite layout, centered on
    /// their cells, so shaping and color fonts work without a glyph-run path for them.</summary>
    private void DrawCluster(int x, int y, in TerminalCell c)
    {
        var text = c.GraphemeLength > 1 ? GraphemeAt?.Invoke(x, y) : null;
        text ??= char.ConvertFromUtf32((int)c.Codepoint);
        var width = (c.Wide == TerminalCell.WideChar ? 2 : 1) * CellWidth;
        SetBrush(Rgb(c.Foreground));
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

    private void DrawCursor(TerminalCell* cell, in GhosttyFrameInfo info, bool focused)
    {
        float left = OriginX + info.CursorX * CellWidth, top = info.CursorY * CellHeight;
        var width = cell->Wide == TerminalCell.WideChar ? 2 * CellWidth : CellWidth;
        SetBrush(Rgb(info.CursorColor));
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
        if (BuiltinGlyphs.Covers(cell->Codepoint))
        {
            DrawBuiltinGlyph(cell->Codepoint, left, top, info.DefaultBackground, info.CursorColor, 1f);
            return;
        }
        var style = StyleOf(cell->Flags);
        var (face, glyph) = Lookup(cell->Codepoint, style);
        if (_isEmojiFace[face] || cell->GraphemeLength > 1)
            return;
        SetBrush(Rgb(info.DefaultBackground));
        var run = new GlyphRun
        {
            FontFace = _faces[style][face],
            FontEmSize = _emSize,
            Indices = [glyph],
            Advances = [width],
        };
        DrawRun(new Vector2(left, top + _baseline), run, face);
    }

    private void DisposeFaces()
    {
        for (var i = 0; i < _faces.Length; i++)
        {
            if (_faces[i] is { } chain)
                foreach (var face in chain)
                    face.Dispose();
            _faces[i] = [];
        }
        _families.Clear();
        _isEmojiFace.Clear();
    }

    private void DisposeDeviceResources()
    {
        ReleaseSwapChain();
        DisposeFaces();
        DisposeGlyphGeometries();
        _emojiFormat?.Dispose();
        _emojiFormat = null;
        _clusterFormat?.Dispose();
        _clusterFormat = null;
        _symbolParams?.Dispose();
        _symbolParams = null;
        _glyphs.Clear();
        _emojiFace = -1;
        _symbolsFace = -1;
        _brush?.Dispose();
        _brush = null!;
        _dc?.Dispose();
        _dc = null!;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        s_renderers.Remove(this);
        SwapChainChanged = null;
        DisposeDeviceResources();
        if (s_renderers.Count == 0)
            DisposeShared();
    }
}
