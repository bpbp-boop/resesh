using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Resesh.Terminal;

/// <summary>Versioned adapter for the resesh ABI exported by Microsoft.Terminal.Control.dll.</summary>
internal sealed class NativeTerminalApi
{
    internal const ushort AbiMajor = 1;
    internal const ushort AbiMinor = 1;
    internal const string DllEnvironmentVariable = "RESESH_NATIVE_TERMINAL_DLL";

    private const uint ThemeOption = 0x00000001;
    private const uint InteractionOption = 0x00000002;
    private const uint EnableBuiltinGlyphs = 0x00000001;
    private const uint EnableColorGlyphs = 0x00000002;
    private const uint DetectUrls = 0x00000004;
    private const uint CopyOnSelect = 0x00000008;
    private const uint RightClickPaste = 0x00000010;
    private const uint SnapOnInput = 0x00000020;
    private const uint AllowOscClipboard = 0x00000040;
    private const uint AllowOscNotifications = 0x00000080;
    private const uint ReadOnly = 0x00000100;
    private const uint CopyHtml = 0x00000001;
    private const uint CopyRtf = 0x00000002;
    private const uint FilterCrLf = 0x00000001;
    private const uint FilterControlCodes = 0x00000002;
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private static readonly Lazy<NativeTerminalApi> Shared = new(Create);
    internal static NativeTerminalApi Instance => Shared.Value;

    private readonly IntPtr _module;
    private readonly CreateDelegate _create;
    private readonly DestroyDelegate _destroy;
    private readonly RegisterEventCallbackDelegate _registerEventCallback;
    private readonly SendOutputDelegate _sendOutput;
    private readonly SendKeyEventDelegate _sendKeyEvent;
    private readonly SendCharEventDelegate _sendCharEvent;
    private readonly SetFocusedDelegate _setFocused;
    private readonly ResizePixelsDelegate _resizePixels;
    private readonly SetOptionsDelegate _setOptions;
    private readonly CopySelectionDelegate _copySelection;
    private readonly PasteTextDelegate _pasteText;

    private NativeTerminalApi(IntPtr module, string libraryPath)
    {
        _module = module;
        LibraryPath = libraryPath;

        var getAbiVersion = Load<GetAbiVersionDelegate>("ReseshTerminalGetAbiVersion");
        var packedVersion = getAbiVersion();
        var major = checked((ushort)(packedVersion >> 16));
        var minor = checked((ushort)(packedVersion & 0xffff));
        if (major != AbiMajor || minor < AbiMinor)
        {
            throw new InvalidOperationException(
                $"The native terminal ABI is {major}.{minor}; resesh requires {AbiMajor}.{AbiMinor}.");
        }
        AbiVersion = new Version(major, minor);

        BuildId = ReadBuildId(Load<GetBuildIdDelegate>("ReseshTerminalGetBuildId"));
        _create = Load<CreateDelegate>("ReseshTerminalCreate");
        _destroy = Load<DestroyDelegate>("ReseshTerminalDestroy");
        _registerEventCallback = Load<RegisterEventCallbackDelegate>("ReseshTerminalRegisterEventCallback");
        _sendOutput = Load<SendOutputDelegate>("ReseshTerminalSendOutput");
        _sendKeyEvent = Load<SendKeyEventDelegate>("ReseshTerminalSendKeyEvent");
        _sendCharEvent = Load<SendCharEventDelegate>("ReseshTerminalSendCharEvent");
        _setFocused = Load<SetFocusedDelegate>("ReseshTerminalSetFocused");
        _resizePixels = Load<ResizePixelsDelegate>("ReseshTerminalResizePixels");
        _setOptions = Load<SetOptionsDelegate>("ReseshTerminalSetOptions");
        _copySelection = Load<CopySelectionDelegate>("ReseshTerminalCopySelection");
        _pasteText = Load<PasteTextDelegate>("ReseshTerminalPasteText");
    }

    internal string LibraryPath { get; }
    internal Version AbiVersion { get; }
    internal string BuildId { get; }

    internal IntPtr CreateTerminal(
        IntPtr parentHwnd,
        NativeTerminalCreationSettings settings,
        out IntPtr childHwnd)
    {
        var theme = settings.Theme;
        var flags = EnableBuiltinGlyphs | EnableColorGlyphs | DetectUrls | SnapOnInput;
        if (settings.CopyOnSelect)
            flags |= CopyOnSelect;
        if (settings.RightClickPaste)
            flags |= RightClickPaste;
        if (settings.AllowOscClipboard)
            flags |= AllowOscClipboard;
        if (settings.AllowOscNotifications)
            flags |= AllowOscNotifications;
        if (settings.ReadOnly)
            flags |= ReadOnly;

        const string wordDelimiters = " ./\\()\"'-:,.;<>~!@#$%^&*|+=[]{}~?\u2502";
        var options = new CreateOptions
        {
            StructSize = checked((uint)Marshal.SizeOf<CreateOptions>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            ParentHwnd = parentHwnd,
            InitialColumns = settings.InitialColumns,
            InitialRows = settings.InitialRows,
            HistorySize = settings.HistorySize,
            Flags = flags,
            FontFamily = settings.FontFamily,
            FontFamilyLength = checked((uint)settings.FontFamily.Length),
            FontSize = checked((short)settings.FontSize),
            FontWeight = 400,
            DefaultBackground = theme.DefaultBackground,
            DefaultForeground = theme.DefaultForeground,
            SelectionBackground = theme.DefaultSelectionBackground,
            CursorColor = theme.CursorColor,
            CursorStyle = theme.CursorStyle,
            ColorTable = theme.ColorTable,
            CopyFormatting = CopyHtml | CopyRtf,
            PasteFiltering = FilterCrLf | FilterControlCodes,
            WordDelimiters = wordDelimiters,
            WordDelimitersLength = checked((uint)wordDelimiters.Length),
        };
        ThrowIfFailed(_create(in options, out childHwnd, out var terminal));
        return terminal;
    }

    internal void DestroyTerminal(IntPtr terminal)
    {
        if (terminal != IntPtr.Zero)
            ThrowIfFailed(_destroy(terminal));
    }

    internal void RegisterEventCallback(IntPtr terminal, EventCallback callback) =>
        ThrowIfFailed(_registerEventCallback(terminal, callback, IntPtr.Zero));

    internal void SendOutput(IntPtr terminal, string text) =>
        ThrowIfFailed(_sendOutput(terminal, text, checked((uint)text.Length)));

    internal void SendKeyEvent(IntPtr terminal, ushort virtualKey, ushort scanCode, ushort flags, bool keyDown) =>
        ThrowIfFailed(_sendKeyEvent(terminal, virtualKey, scanCode, flags, keyDown ? (byte)1 : (byte)0));

    internal void SendCharEvent(IntPtr terminal, char character, ushort scanCode, ushort flags) =>
        ThrowIfFailed(_sendCharEvent(terminal, character, scanCode, flags));

    internal void SetFocused(IntPtr terminal, bool focused) =>
        ThrowIfFailed(_setFocused(terminal, focused ? (byte)1 : (byte)0));

    internal bool CopySelection(IntPtr terminal, bool clearSelection)
    {
        var result = _copySelection(terminal, clearSelection ? (byte)1 : (byte)0);
        ThrowIfFailed(result);
        return result == 0;
    }

    internal void PasteText(IntPtr terminal, string text) =>
        ThrowIfFailed(_pasteText(terminal, text, checked((uint)text.Length)));

    internal TilSize ResizePixels(IntPtr terminal, int width, int height)
    {
        ThrowIfFailed(_resizePixels(terminal, width, height, out var columns, out var rows));
        return new TilSize { X = columns, Y = rows };
    }

    internal void SetTheme(IntPtr terminal, TerminalTheme theme, string fontFamily, short fontSize, int dpi)
    {
        var options = new TerminalOptions
        {
            StructSize = checked((uint)Marshal.SizeOf<TerminalOptions>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            Flags = ThemeOption,
            DefaultBackground = theme.DefaultBackground,
            DefaultForeground = theme.DefaultForeground,
            DefaultSelectionBackground = theme.DefaultSelectionBackground,
            CursorColor = theme.CursorColor,
            CursorStyle = theme.CursorStyle,
            ColorTable = theme.ColorTable,
            FontFamily = fontFamily,
            FontFamilyLength = checked((uint)fontFamily.Length),
            FontSize = fontSize,
            Dpi = dpi,
        };
        ThrowIfFailed(_setOptions(terminal, in options));
    }

    internal void SetInteraction(
        IntPtr terminal,
        bool copyOnSelect,
        bool rightClickPaste,
        bool readOnly)
    {
        var interactionFlags = readOnly ? ReadOnly : 0;
        if (copyOnSelect)
            interactionFlags |= CopyOnSelect;
        if (rightClickPaste)
            interactionFlags |= RightClickPaste;
        var options = new TerminalOptions
        {
            StructSize = checked((uint)Marshal.SizeOf<TerminalOptions>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            Flags = InteractionOption,
            ColorTable = new uint[16],
            FontFamily = string.Empty,
            InteractionFlags = interactionFlags,
            CopyFormatting = CopyHtml | CopyRtf,
            PasteFiltering = FilterCrLf | FilterControlCodes,
        };
        ThrowIfFailed(_setOptions(terminal, in options));
    }

    private static NativeTerminalApi Create()
    {
        var path = ResolveLibraryPath();
        VerifyArchitecture(path);
        VerifyAppLocalHash(path);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The native terminal DLL path has no parent directory.");
        var directoryCookie = AddDllDirectory(directory);
        if (directoryCookie == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not add the native terminal DLL directory '{directory}' (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        IntPtr module;
        int loadError = 0;
        try
        {
            module = LoadLibraryEx(
                path,
                IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero)
                loadError = Marshal.GetLastPInvokeError();
        }
        finally
        {
            _ = RemoveDllDirectory(directoryCookie);
        }

        if (module == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not load the native terminal DLL '{path}' (Win32 error {loadError}).");
        }
        try
        {
            return new NativeTerminalApi(module, path);
        }
        catch
        {
            _ = FreeLibrary(module);
            throw;
        }
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

        var appLocalPath = Path.Combine(
            AppContext.BaseDirectory,
            "NativeTerminal",
            ArchitectureName(),
            "Microsoft.Terminal.Control.dll");
        if (File.Exists(appLocalPath))
            return appLocalPath;

        throw new FileNotFoundException(
            "The native terminal surface is selected, but its pinned DLL is missing. " +
            $"Set {DllEnvironmentVariable}, or build the artifact at '{appLocalPath}'.",
            appLocalPath);
    }

    private static void VerifyArchitecture(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5a4d)
            throw new BadImageFormatException("The native terminal DLL has no DOS header.", path);
        stream.Position = 0x3c;
        var peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 6)
            throw new BadImageFormatException("The native terminal DLL has an invalid PE header offset.", path);
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
            throw new BadImageFormatException("The native terminal DLL has no PE signature.", path);

        var machine = reader.ReadUInt16();
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (ushort)0x8664,
            Architecture.Arm64 => (ushort)0xaa64,
            var architecture => throw new PlatformNotSupportedException(
                $"The native terminal does not support {architecture} processes."),
        };
        if (machine != expected)
        {
            throw new BadImageFormatException(
                $"The native terminal DLL machine 0x{machine:x4} does not match " +
                $"the {RuntimeInformation.ProcessArchitecture} process (0x{expected:x4}).",
                path);
        }
    }

    private static void VerifyAppLocalHash(string libraryPath)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DllEnvironmentVariable)))
            return;

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "NativeTerminal", "native-terminal.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The native terminal artifact manifest is missing.", manifestPath);

        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var architecture = ArchitectureName();
        var expected = document.RootElement
            .GetProperty("artifacts")
            .GetProperty(architecture)
            .GetProperty("Microsoft.Terminal.Control.dll")
            .GetString();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidDataException($"The native terminal manifest has no DLL hash for {architecture}.");

        using var library = File.OpenRead(libraryPath);
        var actual = Convert.ToHexString(SHA256.HashData(library)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The native terminal DLL hash is {actual}, but the manifest requires {expected}.");
        }
    }

    private static string ArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var architecture => throw new PlatformNotSupportedException(
            $"The native terminal does not support {architecture} processes."),
    };

    private static string ReadBuildId(GetBuildIdDelegate getBuildId)
    {
        _ = getBuildId(IntPtr.Zero, 0, out var required);
        if (required is 0 or > 4096)
            throw new InvalidDataException("The native terminal returned an invalid build ID length.");

        var buffer = Marshal.AllocHGlobal(checked((int)required * sizeof(char)));
        try
        {
            ThrowIfFailed(getBuildId(buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its build ID length.");
            return Marshal.PtrToStringUni(buffer, checked((int)required - 1))
                ?? throw new InvalidDataException("The native terminal returned an empty build ID.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private T Load<T>(string exportName) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_module, exportName, out var address))
        {
            throw new EntryPointNotFoundException(
                $"The native terminal ABI {AbiMajor}.{AbiMinor} requires export '{exportName}'.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDllDirectory(IntPtr cookie);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CreateOptions
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal IntPtr ParentHwnd;
        internal int InitialColumns;
        internal int InitialRows;
        internal int HistorySize;
        internal uint Flags;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FontFamily;
        internal uint FontFamilyLength;
        internal short FontSize;
        internal ushort FontWeight;
        internal uint DefaultBackground;
        internal uint DefaultForeground;
        internal uint SelectionBackground;
        internal uint CursorColor;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;

        internal uint CopyFormatting;
        internal uint PasteFiltering;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string WordDelimiters;
        internal uint WordDelimitersLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEvent
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal uint Type;
        internal uint Flags;
        internal ulong Sequence;
        internal IntPtr Text;
        internal uint TextLength;
        internal IntPtr Html;
        internal uint HtmlLength;
        internal IntPtr Rtf;
        internal uint RtfLength;
    }

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
        internal uint CursorColor;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TerminalOptions
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal uint Flags;
        internal uint DefaultBackground;
        internal uint DefaultForeground;
        internal uint DefaultSelectionBackground;
        internal uint CursorColor;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FontFamily;
        internal uint FontFamilyLength;
        internal short FontSize;
        internal ushort Reserved;
        internal int Dpi;
        internal uint InteractionFlags;
        internal uint CopyFormatting;
        internal uint PasteFiltering;
    }

    internal readonly record struct NativeTerminalCreationSettings(
        int InitialColumns,
        int InitialRows,
        int HistorySize,
        string FontFamily,
        int FontSize,
        TerminalTheme Theme,
        bool CopyOnSelect,
        bool RightClickPaste,
        bool AllowOscClipboard,
        bool AllowOscNotifications,
        bool ReadOnly);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuildIdDelegate(IntPtr buffer, uint capacity, out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDelegate(in CreateOptions options, out IntPtr childHwnd, out IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DestroyDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventCallback(IntPtr context, in NativeEvent eventData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegisterEventCallbackDelegate(
        IntPtr terminal,
        EventCallback callback,
        IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SendOutputDelegate(
        IntPtr terminal,
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint textLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SendKeyEventDelegate(
        IntPtr terminal,
        ushort virtualKey,
        ushort scanCode,
        ushort flags,
        byte keyDown);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SendCharEventDelegate(
        IntPtr terminal,
        char character,
        ushort scanCode,
        ushort flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFocusedDelegate(IntPtr terminal, byte focused);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizePixelsDelegate(
        IntPtr terminal,
        int width,
        int height,
        out int columns,
        out int rows);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetOptionsDelegate(IntPtr terminal, in TerminalOptions options);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CopySelectionDelegate(IntPtr terminal, byte clearSelection);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PasteTextDelegate(
        IntPtr terminal,
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint textLength);
}
