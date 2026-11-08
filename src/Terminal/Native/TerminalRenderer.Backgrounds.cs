using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Resesh.Terminal.Native;

/// <summary>
/// Cell backgrounds as one bitmap with a pixel per cell, stretched onto the grid with
/// nearest-neighbor sampling (Wintty and Ghostty draw them the same way, from a per-cell color
/// buffer). A row's background is then one DrawBitmap, where it was a full-width fill plus a
/// rectangle per run of color: dozens per row in a colorful full-screen app. Each cell scales
/// to a whole number of pixels on a whole-pixel origin, so the copy is exact.
/// </summary>
internal sealed unsafe partial class TerminalRenderer
{
    private ID2D1Bitmap1? _backgrounds;
    private int _backgroundColumns;
    private int _backgroundRows;
    private uint[] _backgroundPixels = [];

    /// <summary>D2D1_RECT_U.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RectU
    {
        public uint Left, Top, Right, Bottom;
    }

    /// <summary>Writes the backgrounds of the rows this frame draws into the bitmap. Runs
    /// before anything is drawn, so no draw recorded this frame reads a row being replaced.
    /// False when the bitmap could not be made; rows then fall back to filled rectangles.</summary>
    private bool UpdateBackgrounds(TerminalCell* cells, int cols, int rows, byte* dirty, bool full,
        uint defaultBackground, uint selectionColor)
    {
        if (cols <= 0 || rows <= 0)
            return false;
        var all = full;
        if (_backgrounds is null || _backgroundColumns != cols || _backgroundRows != rows)
        {
            _backgrounds?.Dispose();
            _backgrounds = null;
            try
            {
                _backgrounds = _dc.CreateBitmap(new SizeI(cols, rows), IntPtr.Zero, 0, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                return false; // larger than the device's bitmap limit: an absurdly wide grid
            }
            _backgroundColumns = cols;
            _backgroundRows = rows;
            if (_backgroundPixels.Length < cols * rows)
                _backgroundPixels = new uint[cols * rows];
            all = true;
        }

        fixed (uint* pixels = _backgroundPixels)
        {
            for (var y = 0; y < rows;)
            {
                if (!all && dirty[y] == 0) { y++; continue; }
                var first = y;
                for (; y < rows && (all || dirty[y] != 0); y++)
                {
                    var row = cells + y * cols;
                    var line = pixels + y * cols;
                    for (var x = 0; x < cols; x++)
                        line[x] = 0xFF000000 | (BackgroundOf(row[x], selectionColor) ?? defaultBackground);
                }
                var band = new RectU { Left = 0, Top = (uint)first, Right = (uint)cols, Bottom = (uint)y };
                if (!CopyFromMemory(_backgrounds, &band, pixels + first * cols, (uint)(cols * sizeof(uint))))
                {
                    // Called inside the frame's draw batch: fall back to rectangles rather than
                    // throw past EndDraw. The next frame makes the bitmap again, every row.
                    DisposeBackgrounds();
                    return false;
                }
            }
        }
        return true;
    }

    // ID2D1Bitmap's slot for CopyFromMemory: IUnknown (3), ID2D1Resource::GetFactory (1), then
    // six bitmap methods before it (d2d1.h). Vortice's public overloads take managed arrays.
    private const int CopyFromMemorySlot = 10;

    private static bool CopyFromMemory(ID2D1Bitmap1 bitmap, RectU* destination, void* source, uint pitch)
    {
        var self = bitmap.NativePointer;
        var copy = (delegate* unmanaged[Stdcall]<IntPtr, RectU*, void*, uint, int>)(*(void***)self)[CopyFromMemorySlot];
        return copy(self, destination, source, pitch) >= 0;
    }

    /// <summary>Row <paramref name="y"/>'s cell backgrounds, from the bitmap.</summary>
    private void DrawRowBackground(int cols, int y)
    {
        float top = y * CellHeight;
        _dc.DrawBitmap(_backgrounds!, new RawRectF(OriginX, top, OriginX + cols * CellWidth, top + CellHeight), 1f,
            InterpolationMode.NearestNeighbor, new RawRectF(0, y, cols, y + 1), null);
    }

    private void DisposeBackgrounds()
    {
        _backgrounds?.Dispose();
        _backgrounds = null;
        _backgroundColumns = 0;
        _backgroundRows = 0;
    }
}
