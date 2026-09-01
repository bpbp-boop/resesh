using System.Runtime.InteropServices;

namespace Resesh.Terminal;

/// <summary>
/// Narrow adapter for the unsupported HwndTerminal C ABI exported by
/// Microsoft.Terminal.Control.dll. Keep all Microsoft Terminal interop in this file.
/// </summary>
internal sealed class NativeTerminalApi
{
    internal const int AdapterAbiVersion = 1;
    internal const string DllEnvironmentVariable = "RESESH_NATIVE_TERMINAL_DLL";

    internal static NativeTerminalApi Instance => Shared.Value;
    private static readonly Lazy<NativeTerminalApi> Shared = new(Create);

    private readonly IntPtr _module;

    private NativeTerminalApi(IntPtr module)
    {
        _module = module;
        CreateTerminal = Load<CreateTerminalDelegate>("CreateTerminal");
        DestroyTerminal = Load<DestroyTerminalDelegate>("DestroyTerminal");
        SendOutput = Load<SendOutputDelegate>("TerminalSendOutput");
        TriggerResize = Load<TriggerResizeDelegate>("TerminalTriggerResize");
        DpiChanged = Load<DpiChangedDelegate>("TerminalDpiChanged");
        RegisterWriteCallback = Load<RegisterWriteCallbackDelegate>("TerminalRegisterWriteCallback");
        SendKeyEvent = Load<SendKeyEventDelegate>("TerminalSendKeyEvent");
        SendCharEvent = Load<SendCharEventDelegate>("TerminalSendCharEvent");
        if (TryLoad<SetFocusedDelegate>("TerminalSetFocused") is { } setFocused)
        {
            SetFocused = setFocused.Invoke;
        }
        else
        {
            var setFocus = Load<FocusDelegate>("TerminalSetFocus");
            var killFocus = Load<FocusDelegate>("TerminalKillFocus");
            SetFocused = (terminal, focused) =>
            {
                if (focused)
                    setFocus(terminal);
                else
                    killFocus(terminal);
            };
        }
        SetTheme = Load<SetThemeDelegate>("TerminalSetTheme");
    }

    internal CreateTerminalDelegate CreateTerminal { get; }
    internal DestroyTerminalDelegate DestroyTerminal { get; }
    internal SendOutputDelegate SendOutput { get; }
    internal TriggerResizeDelegate TriggerResize { get; }
    internal DpiChangedDelegate DpiChanged { get; }
    internal RegisterWriteCallbackDelegate RegisterWriteCallback { get; }
    internal SendKeyEventDelegate SendKeyEvent { get; }
    internal SendCharEventDelegate SendCharEvent { get; }
    internal Action<IntPtr, bool> SetFocused { get; }
    internal SetThemeDelegate SetTheme { get; }

    private static NativeTerminalApi Create()
    {
        const uint loadLibrarySearchDllLoadDir = 0x00000100;
        const uint loadLibrarySearchUserDirs = 0x00000400;
        const uint loadLibrarySearchDefaultDirs = 0x00001000;

        var path = ResolveLibraryPath();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The native terminal DLL path has no parent directory.");
        if (AddDllDirectory(directory) == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not add the native terminal DLL directory '{directory}' (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        var module = LoadLibraryEx(
            path,
            IntPtr.Zero,
            loadLibrarySearchDllLoadDir | loadLibrarySearchUserDirs | loadLibrarySearchDefaultDirs);
        if (module == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not load the native terminal DLL '{path}' for the " +
                $"{RuntimeInformation.ProcessArchitecture} process (Win32 error {Marshal.GetLastPInvokeError()}).");
        }
        return new NativeTerminalApi(module);
    }

    private static string ResolveLibraryPath()
    {
        var configured = Environment.GetEnvironmentVariable(DllEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            if (File.Exists(fullPath))
                return fullPath;
            throw new FileNotFoundException(
                $"{DllEnvironmentVariable} points to a file that does not exist.", fullPath);
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var value => value.ToString().ToLowerInvariant(),
        };
        var appLocalPath = Path.Combine(
            AppContext.BaseDirectory,
            "NativeTerminal",
            architecture,
            "Microsoft.Terminal.Control.dll");
        if (File.Exists(appLocalPath))
            return appLocalPath;

        throw new FileNotFoundException(
            "The native terminal surface is selected, but Microsoft.Terminal.Control.dll is not configured. " +
            $"Set {DllEnvironmentVariable}, or put the pinned DLL at '{appLocalPath}'.",
            appLocalPath);
    }

    private T Load<T>(string exportName) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_module, exportName, out var address))
        {
            throw new EntryPointNotFoundException(
                $"The native terminal ABI v{AdapterAbiVersion} requires export '{exportName}'.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private T? TryLoad<T>(string exportName) where T : Delegate =>
        NativeLibrary.TryGetExport(_module, exportName, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    internal struct TilSize
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TerminalTheme
    {
        internal uint DefaultBackground;
        internal uint DefaultForeground;
        internal uint DefaultSelectionBackground;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateTerminalDelegate(IntPtr parentHwnd, out IntPtr childHwnd, out IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DestroyTerminalDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    internal delegate void SendOutputDelegate(IntPtr terminal, [MarshalAs(UnmanagedType.LPWStr)] string data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int TriggerResizeDelegate(IntPtr terminal, int width, int height, out TilSize dimensions);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DpiChangedDelegate(IntPtr terminal, int newDpi);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    internal delegate void WriteCallback([MarshalAs(UnmanagedType.LPWStr)] string data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RegisterWriteCallbackDelegate(IntPtr terminal, WriteCallback callback);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SendKeyEventDelegate(
        IntPtr terminal,
        ushort virtualKey,
        ushort scanCode,
        ushort flags,
        [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SendCharEventDelegate(IntPtr terminal, char character, ushort scanCode, ushort flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetFocusedDelegate(IntPtr terminal, [MarshalAs(UnmanagedType.I1)] bool focused);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FocusDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    internal delegate void SetThemeDelegate(
        IntPtr terminal,
        TerminalTheme theme,
        [MarshalAs(UnmanagedType.LPWStr)] string fontFamily,
        short fontSize,
        int dpi);
}
