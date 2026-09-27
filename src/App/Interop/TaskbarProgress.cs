using System.Runtime.InteropServices;
using Resesh.Core.Models;

namespace Resesh.App.Interop;

/// <summary>
/// Shows a window's combined tab progress on its taskbar button (ITaskbarList3), as
/// Windows Terminal does. The shell draws the state colours: green, red for error,
/// yellow for paused, and a marquee for indeterminate.
/// </summary>
internal static class TaskbarProgress
{
    private const int NoProgress = 0;
    private const int IndeterminateFlag = 1;
    private const int NormalFlag = 2;
    private const int ErrorFlag = 4;
    private const int PausedFlag = 8;

    private static ITaskbarList3? _taskbar;
    private static bool _unavailable;

    public static void Apply(IntPtr hwnd, TerminalProgress progress)
    {
        if (hwnd == IntPtr.Zero || Taskbar() is not { } taskbar)
            return;
        var flag = progress.State switch
        {
            TerminalProgressState.Normal => NormalFlag,
            TerminalProgressState.Error => ErrorFlag,
            TerminalProgressState.Paused => PausedFlag,
            TerminalProgressState.Indeterminate => IndeterminateFlag,
            _ => NoProgress,
        };
        // A value switches an indeterminate button back to normal, so it is set only for
        // the determinate states, and before the state so error and paused keep it.
        if (flag is NormalFlag or ErrorFlag or PausedFlag)
            taskbar.SetProgressValue(hwnd, (ulong)progress.Value, 100);
        taskbar.SetProgressState(hwnd, flag);
    }

    private static ITaskbarList3? Taskbar()
    {
        if (_taskbar is not null || _unavailable)
            return _taskbar;
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarListRcw();
            taskbar.HrInit();
            _taskbar = taskbar;
        }
        catch (COMException)
        {
            _unavailable = true; // no Explorer shell, e.g. Server Core
        }
        return _taskbar;
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListRcw { }

    // Only the vtable prefix up to SetProgressState is declared.
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }
}
