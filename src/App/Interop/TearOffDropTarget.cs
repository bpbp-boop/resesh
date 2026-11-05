using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics;

namespace Resesh.App.Interop;

/// <summary>
/// An invisible, topmost window over every part of the desktop no resesh window shows, up for
/// the length of a tab drag. It accepts the tab, so outside the windows the pointer shows the
/// move cursor instead of "no drop", and it reports where the tab was dropped. Parts of a
/// resesh window that are on screen are cut out of it, so the drag reaches their own targets.
/// </summary>
internal sealed unsafe partial class TearOffDropTarget : IDisposable
{
    private const string ClassName = "ReseshTearOffDropTarget";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint GW_HWNDNEXT = 2;
    private const int RGN_OR = 2;
    private const int RGN_DIFF = 4;
    private const int BLACK_BRUSH = 4;
    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint DWMWA_CLOAKED = 14;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const uint DROPEFFECT_MOVE = 2;

    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static bool _classRegistered;
    private static bool _oleInitialized;

    private IntPtr _window;
    private bool _registered;

    private TearOffDropTarget(IntPtr window) => _window = window;

    /// <summary>Covers the desktop around <paramref name="appWindows"/>. <paramref name="dropped"/>
    /// gets the drop point in physical screen pixels, from inside the system's drag loop.</summary>
    public static TearOffDropTarget Show(IReadOnlyCollection<IntPtr> appWindows, Action<PointInt32> dropped)
    {
        EnsureInitialized();
        var x = GetSystemMetrics(76);
        var y = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        var window = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, null, WS_POPUP, x, y, width, height, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (window == IntPtr.Zero)
            throw new Win32Exception();
        var target = new TearOffDropTarget(window);
        try
        {
            // One step above clear: hit-tested, so the drag finds it, but never seen.
            if (!SetLayeredWindowAttributes(window, 0, 1, LWA_ALPHA))
                throw new Win32Exception();
            var region = UncoveredRegion(appWindows, x, y, width, height);
            if (SetWindowRgn(window, region, false) == 0)
            {
                DeleteObject(region);
                throw new Win32Exception();
            }
            target.Register(new DropHandler(dropped));
            ShowWindow(window, SW_SHOWNOACTIVATE);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_window == IntPtr.Zero)
            return;
        if (_registered)
            RevokeDragDrop(_window);
        DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private void Register(DropHandler handler)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, typeof(IDropTarget).GUID, out var dropTarget));
            try
            {
                Marshal.ThrowExceptionForHR(RegisterDragDrop(_window, dropTarget));
                _registered = true;
            }
            finally
            {
                Marshal.Release(dropTarget);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static void EnsureInitialized()
    {
        // RegisterDragDrop needs OLE on this thread; it is never torn down, like the thread.
        if (!_oleInitialized)
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
            _oleInitialized = true;
        }
        if (_classRegistered)
            return;
        fixed (char* name = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = GetModuleHandleW(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = name,
            };
            if (RegisterClassExW(&windowClass) == 0)
                throw new Win32Exception();
        }
        _classRegistered = true;
    }

    /// <summary>The virtual screen less what is on screen of <paramref name="appWindows"/>, in
    /// window coordinates: windows are walked top to bottom, each app window cutting out only
    /// the part no window above it covers.</summary>
    private static IntPtr UncoveredRegion(IReadOnlyCollection<IntPtr> appWindows, int x, int y, int width, int height)
    {
        var region = CreateRectRgn(0, 0, width, height);
        var covered = CreateRectRgn(0, 0, 0, 0);
        var visible = CreateRectRgn(0, 0, 0, 0);
        try
        {
            for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, GW_HWNDNEXT))
            {
                if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window)
                    || (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0
                    || !TryGetBounds(window, out var bounds))
                    continue;
                var area = CreateRectRgn(bounds.Left - x, bounds.Top - y, bounds.Right - x, bounds.Bottom - y);
                if (appWindows.Contains(window))
                {
                    CombineRgn(visible, area, covered, RGN_DIFF);
                    CombineRgn(region, region, visible, RGN_DIFF);
                }
                CombineRgn(covered, covered, area, RGN_OR);
                DeleteObject(area);
            }
            return region;
        }
        finally
        {
            DeleteObject(covered);
            DeleteObject(visible);
        }
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The window as drawn, without the invisible resize borders around it.</summary>
    private static bool TryGetBounds(IntPtr window, out RECT bounds) =>
        DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, sizeof(RECT)) == 0
        || GetWindowRect(window, out bounds);

    /// <summary>The system's drag loop holds the mouse while a drag runs, so a click or move
    /// reaching this window means the drag ended without the app hearing of it. Hiding then
    /// keeps a lost drag from leaving an invisible window over the desktop.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
                ShowWindow(window, SW_HIDE);
                break;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    [GeneratedComClass]
    private sealed partial class DropHandler(Action<PointInt32> dropped) : IDropTarget
    {
        public int DragEnter(IntPtr dataObject, uint keyState, POINTL point, ref uint effect)
        {
            effect &= DROPEFFECT_MOVE;
            return 0;
        }

        public int DragOver(uint keyState, POINTL point, ref uint effect)
        {
            effect &= DROPEFFECT_MOVE;
            return 0;
        }

        public int DragLeave() => 0;

        public int Drop(IntPtr dataObject, uint keyState, POINTL point, ref uint effect)
        {
            effect &= DROPEFFECT_MOVE;
            if (effect != 0)
                dropped(new PointInt32(point.X, point.Y));
            return 0;
        }
    }

    [GeneratedComInterface, Guid("00000122-0000-0000-C000-000000000046")]
    internal partial interface IDropTarget
    {
        [PreserveSig] int DragEnter(IntPtr dataObject, uint keyState, POINTL point, ref uint effect);
        [PreserveSig] int DragOver(uint keyState, POINTL point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IntPtr dataObject, uint keyState, POINTL point, ref uint effect);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(IntPtr reserved);

    [LibraryImport("ole32.dll")]
    private static partial int RegisterDragDrop(IntPtr window, IntPtr dropTarget);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetTopWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr window, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out RECT rect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr window, uint attribute, out RECT value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? name);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);
}
