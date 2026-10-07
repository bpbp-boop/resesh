using System.Runtime.InteropServices;

namespace Resesh.Terminal.Ghostty;

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyFrameInfo
{
    public uint DefaultForeground;
    public uint DefaultBackground;
    public uint CursorColor;
    public ushort CursorX;
    public ushort CursorY;
    public byte CursorVisible;
    public byte CursorStyle; // 0 block, 1 bar, 2 underline, 3 hollow block
    public byte CursorBlinking;
    public byte Dirty;
    public ushort DirtyRows;
    public ushort Alternate; // the alternate screen (vim, htop) is active
    public ulong ScrollTotal;
    public ulong ScrollOffset;
    public ulong ScrollLength;
}

internal enum GhosttyEventKind
{
    Pty = 1,
    Bell = 2,
    Title = 3,
    WorkingDirectory = 4,
    Clipboard = 5,
    Osc = 6,
    Semantic = 7,
}

/// <summary>
/// P/Invoke surface of reseshvt.dll, which wraps libghostty-vt (ghostty-vt.dll). Both load
/// from the app's GhosttyVt directory; <see cref="TryLoad"/> reports why the surface is
/// unavailable instead of throwing at first use.
/// </summary>
internal static unsafe partial class GhosttyNative
{
    private const string Library = "reseshvt";
    private const int ExpectedAbi = 1;

    private static readonly Lazy<string?> LoadError = new(Load);

    /// <summary>Null when the native libraries loaded and match this build; otherwise why not.</summary>
    public static string? TryLoad() => LoadError.Value;

    private static string? Load()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "GhosttyVt");
            var vt = Path.Combine(dir, "ghostty-vt.dll");
            var shim = Path.Combine(dir, "reseshvt.dll");
            if (!File.Exists(vt) || !File.Exists(shim))
                return $"libghostty-vt not found in {dir} (run eng/build-ghostty-vt.ps1)";
            // reseshvt.dll imports ghostty-vt.dll by name; load it from the same directory first.
            NativeLibrary.Load(vt);
            var handle = NativeLibrary.Load(shim);
            NativeLibrary.SetDllImportResolver(typeof(GhosttyNative).Assembly, (name, _, _) =>
                name == Library ? handle : IntPtr.Zero);
            var abi = rvt_abi_version();
            return abi == ExpectedAbi ? null : $"reseshvt ABI {abi}, expected {ExpectedAbi}";
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    [LibraryImport(Library)] public static partial int rvt_abi_version();
    [LibraryImport(Library)]
    public static partial IntPtr rvt_new(ushort cols, ushort rows, nuint scrollbackLines,
        delegate* unmanaged<IntPtr, int, byte*, nuint, void> onEvent, IntPtr user);
    [LibraryImport(Library)] public static partial void rvt_free(IntPtr term);
    [LibraryImport(Library)] public static partial void rvt_write(IntPtr term, byte* data, nuint len);
    [LibraryImport(Library)] public static partial void rvt_resize(IntPtr term, ushort cols, ushort rows, uint cellWidth, uint cellHeight);
    [LibraryImport(Library)] public static partial void rvt_set_scrollback(IntPtr term, nuint lines);
    [LibraryImport(Library)] public static partial void rvt_set_colors(IntPtr term, uint fg, uint bg, uint cursor, uint* palette16);
    [LibraryImport(Library)]
    public static partial int rvt_read_frame(IntPtr term, GhosttyCell* cells, ushort cols, ushort rows,
        byte* dirtyRows, int forceAll, GhosttyFrameInfo* info);
    [LibraryImport(Library)] public static partial int rvt_cell_graphemes(IntPtr term, ushort x, ushort y, uint* output, int capacity);
    [LibraryImport(Library)] public static partial void rvt_scroll(IntPtr term, int mode, nint value);
    [LibraryImport(Library)] public static partial void rvt_reset(IntPtr term);
    [LibraryImport(Library)]
    public static partial int rvt_encode_key(IntPtr term, int virtualKey, int mods, int action, byte* utf8, nuint utf8Length,
        uint unshifted, byte* output, nuint capacity);
    [LibraryImport(Library)] public static partial int rvt_kitty_flags(IntPtr term);
    [LibraryImport(Library)] public static partial int rvt_mouse_tracking(IntPtr term);
    [LibraryImport(Library)]
    public static partial int rvt_encode_mouse(IntPtr term, int action, int button, int mods, float x, float y,
        int anyButtonPressed, byte* output, nuint capacity);
    [LibraryImport(Library)] public static partial int rvt_select(IntPtr term, ushort x0, ushort y0, ushort x1, ushort y1);
    [LibraryImport(Library)]
    public static partial int rvt_gesture(IntPtr term, int kind, double x, double y, ushort viewportX, ushort viewportY,
        ulong timeNanoseconds, uint paddingLeft, out int autoscroll, out int clicks, out int dragged);
    [LibraryImport(Library)] public static partial void rvt_select_clear(IntPtr term);
    [LibraryImport(Library)] public static partial int rvt_select_all(IntPtr term);
    [LibraryImport(Library)] public static partial byte* rvt_selection_text(IntPtr term, out nuint length);
    [LibraryImport(Library)] public static partial void rvt_free_buffer(byte* buffer, nuint length);
    [LibraryImport(Library)] public static partial byte* rvt_format_vt(IntPtr term, out nuint length);
    [LibraryImport(Library)] public static partial void rvt_buffer_info(IntPtr term, GhosttyBufferInfo* info);
    [LibraryImport(Library)] public static partial int rvt_line_text(IntPtr term, int line, byte* output, nuint capacity, int* wrapped);
    [LibraryImport(Library)] public static partial uint rvt_marker_new(IntPtr term, int line);
    [LibraryImport(Library)] public static partial int rvt_marker_line(IntPtr term, uint id);
    [LibraryImport(Library)] public static partial void rvt_marker_free(IntPtr term, uint id);
    [LibraryImport(Library)] public static partial int rvt_search_set(IntPtr term, byte* needle, nuint length);
    [LibraryImport(Library)] public static partial void rvt_search_step(IntPtr term, int direction);
    [LibraryImport(Library)] public static partial void rvt_search_status(IntPtr term, out nuint total, out nuint current);
    [LibraryImport(Library)] public static partial int rvt_search_lines(IntPtr term, int* output, int capacity, out int current);
    [LibraryImport(Library)] public static partial int rvt_paste(IntPtr term, byte* text, nuint length);

    public const int ScrollTop = 0, ScrollBottom = 1, ScrollDelta = 2, ScrollRow = 3;
    public const int ModShift = 1, ModCtrl = 2, ModAlt = 4, ModSuper = 8;
    public const int KeyRelease = 0, KeyPress = 1, KeyRepeat = 2;
    public const int MousePress = 0, MouseRelease = 1, MouseMotion = 2;
}
