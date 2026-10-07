using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWFactoryType = Vortice.DirectWrite.FactoryType;
using MapFlags = Vortice.Direct3D11.MapFlags;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using MeasuringMode = Vortice.DCommon.MeasuringMode;

namespace GhosttyVtBench;

/// <summary>
/// Spike renderer: Direct2D on a D3D11 texture, DirectWrite glyph runs placed on the cell
/// grid. Only dirty rows are redrawn; the texture keeps the previous frame. This stands in for
/// a SwapChainPanel-backed surface (present cost excluded; GPU completion is awaited).
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _texture;
    private readonly ID2D1DeviceContext _dc;
    private readonly ID2D1Bitmap1 _target;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly IDWriteFactory2 _dw;
    private readonly IDWriteFontFace[] _regular;   // primary + fallback chain
    private readonly IDWriteFontFace[] _bold;
    private readonly float _fontSize;
    private readonly float _baseline;
    private readonly ID3D11Query[] _frameQueries = new ID3D11Query[2];
    private int _frameIndex;
    private readonly Dictionary<uint, (int Face, ushort Glyph)> _glyphCache = [];
    private readonly Dictionary<uint, (int Face, ushort Glyph)> _boldGlyphCache = [];

    private readonly ushort[] _runGlyphs;
    private readonly float[] _runAdvances;

    public int Cols { get; }
    public int Rows { get; }
    public float CellWidth { get; }
    public float CellHeight { get; }
    public int Width { get; }
    public int Height { get; }
    public string AdapterName { get; }

    public Renderer(int cols, int rows, string fontFamily, float fontSize)
    {
        Cols = cols;
        Rows = rows;
        _fontSize = fontSize;
        _runGlyphs = new ushort[cols];
        _runAdvances = new float[cols];

        _dw = DWrite.DWriteCreateFactory<IDWriteFactory2>(DWFactoryType.Shared);
        using var collection = _dw.GetSystemFontCollection(false);
        IDWriteFontFace Face(string family, FontWeight weight)
        {
            if (!collection.FindFamilyName(family, out var index))
                throw new InvalidOperationException("font not found: " + family);
            using var fam = collection.GetFontFamily(index);
            using var font = fam.GetFirstMatchingFont(weight, FontStretch.Normal, FontStyle.Normal);
            return font.CreateFontFace();
        }
        string[] chain = [fontFamily, "Segoe UI Symbol", "Yu Gothic", "Segoe UI Emoji", "Segoe UI"];
        _regular = [.. chain.Select(f => Face(f, FontWeight.Normal))];
        _bold = [.. chain.Select(f => Face(f, FontWeight.Bold))];

        var m = _regular[0].Metrics;
        float scale = fontSize / m.DesignUnitsPerEm;
        var zero = _regular[0].GetGlyphIndices([(uint)'0']);
        var gm = _regular[0].GetDesignGlyphMetrics(zero, false);
        CellWidth = MathF.Round(gm[0].AdvanceWidth * scale);
        CellHeight = MathF.Ceiling((m.Ascent + m.Descent + m.LineGap) * scale);
        _baseline = MathF.Round(m.Ascent * scale + (m.LineGap * scale) / 2);
        Width = (int)(cols * CellWidth);
        Height = (int)(rows * CellHeight);

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device!;
        _context = context!;
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
            AdapterName = adapter.Description.Description;

        _texture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));

        using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var d2dDevice = d2dFactory.CreateDevice(dxgiDevice))
            _dc = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        using (var surface = _texture.QueryInterface<IDXGISurface>())
            _target = _dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
        _dc.Target = _target;
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _brush = _dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        for (int i = 0; i < _frameQueries.Length; i++)
            _frameQueries[i] = _device.CreateQuery(new QueryDescription(QueryType.Event));
    }

    private (int Face, ushort Glyph) Lookup(uint cp, bool bold)
    {
        var cache = bold ? _boldGlyphCache : _glyphCache;
        if (cache.TryGetValue(cp, out var hit))
            return hit;
        var faces = bold ? _bold : _regular;
        (int, ushort) result = (0, 0);
        for (int i = 0; i < faces.Length; i++)
        {
            var g = faces[i].GetGlyphIndices([cp])[0];
            if (g != 0) { result = (i, g); break; }
        }
        cache[cp] = result;
        return result;
    }

    private static Color4 Rgb(uint c) => new(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);

    /// <summary>Draws every row flagged in <paramref name="dirty"/> and clears the flags.</summary>
    public void DrawFrame(RvtCell* cells, byte* dirty, in RvtFrameInfo info, ref int lastCursorRow)
    {
        // Wait for the frame before last so at most one frame is queued on the GPU, like a
        // swap chain with a frame-latency of 2.
        var q = _frameQueries[_frameIndex & 1];
        if (_frameIndex >= 2)
            while (_context.GetData(q, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0) Thread.SpinWait(20);

        // The old cursor row must be repainted to erase the cursor.
        if (lastCursorRow >= 0 && lastCursorRow < Rows) dirty[lastCursorRow] = 1;
        if (info.CursorVisible != 0 && info.CursorY < Rows) dirty[info.CursorY] = 1;

        _dc.BeginDraw();
        for (int y = 0; y < Rows; y++)
        {
            if (dirty[y] == 0) continue;
            dirty[y] = 0;
            DrawRow(cells + y * Cols, y, info.DefaultBg);
        }
        if (info.CursorVisible != 0 && info.CursorY < Rows)
        {
            _brush.Color = new Color4(0.85f, 0.85f, 0.85f, 0.6f);
            float x = info.CursorX * CellWidth, top = info.CursorY * CellHeight;
            _dc.FillRectangle(new Rect(x, top, CellWidth, CellHeight), _brush);
            lastCursorRow = info.CursorY;
        }
        _dc.EndDraw();
        _context.End(q);
        _context.Flush();
        _frameIndex++;
    }

    private void DrawRow(RvtCell* row, int y, uint defaultBg)
    {
        float top = y * CellHeight;
        _brush.Color = Rgb(defaultBg);
        _dc.FillRectangle(new Rect(0, top, Width, CellHeight), _brush);

        // Background runs.
        for (int x = 0; x < Cols;)
        {
            if ((row[x].Flags & RvtCell.DefaultBg) != 0) { x++; continue; }
            uint bg = row[x].Bg; int start = x;
            while (x < Cols && (row[x].Flags & RvtCell.DefaultBg) == 0 && row[x].Bg == bg) x++;
            _brush.Color = Rgb(bg);
            _dc.FillRectangle(new Rect(start * CellWidth, top, (x - start) * CellWidth, CellHeight), _brush);
        }

        // Foreground glyph runs: same face, weight and color; every glyph advances whole cells.
        float baseline = top + _baseline;
        for (int x = 0; x < Cols;)
        {
            ref var c = ref row[x];
            if (c.Cp == 0 || c.Cp == ' ' || c.Wide == RvtCell.WideSpacerTail || (c.Flags & RvtCell.Invisible) != 0) { x++; continue; }
            bool bold = (c.Flags & RvtCell.Bold) != 0;
            var (face, _) = Lookup(c.Cp, bold);
            uint fg = c.Fg; int start = x; int n = 0;
            while (x < Cols)
            {
                ref var d = ref row[x];
                if (d.Wide == RvtCell.WideSpacerTail) { x++; continue; }
                if (d.Cp == 0 || d.Cp == ' ')
                {
                    // A blank cell continues the run as an advance on the previous glyph.
                    if (n > 0) { _runAdvances[n - 1] += CellWidth; x++; continue; }
                    break;
                }
                if (d.Fg != fg || ((d.Flags & RvtCell.Bold) != 0) != bold) break;
                var (f, g) = Lookup(d.Cp, bold);
                if (f != face) break;
                _runGlyphs[n] = g;
                _runAdvances[n] = d.Wide == RvtCell.WideWide ? 2 * CellWidth : CellWidth;
                n++; x++;
            }
            if (n == 0) { x++; continue; }
            _brush.Color = Rgb(fg);
            var faces = bold ? _bold : _regular;
            var run = new GlyphRun
            {
                FontFace = faces[face],
                FontEmSize = _fontSize,
                Indices = _runGlyphs.AsSpan(0, n).ToArray(),
                Advances = _runAdvances.AsSpan(0, n).ToArray(),
            };
            _dc.DrawGlyphRun(new Vector2(start * CellWidth, baseline), run, _brush, MeasuringMode.Natural);

            // Decorations.
            for (int i = start; i < x; i++)
            {
                var flags = row[i].Flags;
                if ((flags & RvtCell.Underline) != 0)
                    _dc.FillRectangle(new Rect(i * CellWidth, baseline + 2, CellWidth, 1), _brush);
                if ((flags & RvtCell.Strike) != 0)
                    _dc.FillRectangle(new Rect(i * CellWidth, top + CellHeight / 2, CellWidth, 1), _brush);
            }
        }
    }

    /// <summary>Blocks until the GPU has finished all submitted drawing.</summary>
    public void WaitForGpu()
    {
        using var q = _device.CreateQuery(new QueryDescription(QueryType.Event));
        _context.End(q);
        _context.Flush();
        while (_context.GetData(q, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0) Thread.SpinWait(20);
    }

    public void SavePng(string path)
    {
        using var staging = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _context.CopyResource(staging, _texture);
        var map = _context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var rgba = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                var src = new ReadOnlySpan<byte>((byte*)map.DataPointer + y * map.RowPitch, Width * 4);
                for (int x = 0; x < Width; x++)
                {
                    rgba[(y * Width + x) * 4 + 0] = src[x * 4 + 2];
                    rgba[(y * Width + x) * 4 + 1] = src[x * 4 + 1];
                    rgba[(y * Width + x) * 4 + 2] = src[x * 4 + 0];
                    rgba[(y * Width + x) * 4 + 3] = 255;
                }
            }
            Png.Write(path, Width, Height, rgba);
        }
        finally { _context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        foreach (var q in _frameQueries) q.Dispose();
        _brush.Dispose(); _target.Dispose(); _dc.Dispose(); _texture.Dispose();
        foreach (var f in _regular) f.Dispose();
        foreach (var f in _bold) f.Dispose();
        _dw.Dispose(); _context.Dispose(); _device.Dispose();
    }
}
