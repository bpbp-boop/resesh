using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Resesh.App.Interop;

/// <summary>Where a window's content sits on screen, which AppWindow does not report.</summary>
internal static partial class WindowFrame
{
    /// <summary>The screen position, in physical pixels, of the window's client area, where
    /// its XAML content starts.</summary>
    public static PointInt32? ClientOrigin(IntPtr hwnd)
    {
        var point = new POINT();
        return ClientToScreen(hwnd, ref point) ? new PointInt32(point.X, point.Y) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hwnd, ref POINT point);
}
