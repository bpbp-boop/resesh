using System.Runtime.InteropServices;

namespace Resesh.App.Interop;

/// <summary>
/// Background attention signals for agent alerts (Phase 6.2): flash the taskbar button,
/// and optionally play the system notification sound. Deliberately content-free — an
/// agent's label never leaves the app, so nothing an agent says can end up on a lock
/// screen or in a notification history.
/// </summary>
internal static class WindowAlerts
{
    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;
    private const uint MB_ICONASTERISK = 0x00000040;

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>Flashes the taskbar button until the window comes to the foreground.</summary>
    public static void Flash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    public static void Beep() => MessageBeep(MB_ICONASTERISK);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
}
