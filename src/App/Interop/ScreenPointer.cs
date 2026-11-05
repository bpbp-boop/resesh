using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Resesh.App.Interop;

/// <summary>
/// Where the mouse is on the desktop, in physical pixels, for a tab dragged out of every
/// window: the system drag loop has ended by then, so no XAML element reports a position.
/// </summary>
internal static class ScreenPointer
{
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_ESCAPE = 0x1B;
    private const uint GA_ROOT = 2;

    public static PointInt32? Position() =>
        GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : null;

    /// <summary>A drag cancelled with Escape ends like a drop nowhere, but the button that
    /// carried it is still held. Physical buttons are checked, so both, for swapped buttons.</summary>
    public static bool IsDragCancelled() =>
        IsDown(VK_ESCAPE) || IsDown(VK_LBUTTON) || IsDown(VK_RBUTTON);

    /// <summary>The top-level window under <paramref name="point"/>, or zero.</summary>
    public static IntPtr TopLevelWindowAt(PointInt32 point)
    {
        var window = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, GA_ROOT);
    }

    /// <summary>The screen position of the window's client area, where its XAML content starts.</summary>
    public static PointInt32? ClientOrigin(IntPtr hwnd)
    {
        var point = new POINT();
        return ClientToScreen(hwnd, ref point) ? new PointInt32(point.X, point.Y) : null;
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
}
