using System.Reflection;
using System.Runtime.InteropServices;

namespace GhosttyVtBench;

[StructLayout(LayoutKind.Sequential)]
internal struct RvtCell
{
    public uint Cp;
    public uint Fg;
    public uint Bg;
    public ushort Flags;
    public byte Wide;
    public byte GraphemeLength;

    public const ushort Bold = 1, Italic = 2, Underline = 4, Strike = 8, DefaultBg = 16, Faint = 32, Invisible = 64;
    public const byte WideNarrow = 0, WideWide = 1, WideSpacerTail = 2, WideSpacerHead = 3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RvtFrameInfo
{
    public uint DefaultFg;
    public uint DefaultBg;
    public ushort CursorX;
    public ushort CursorY;
    public byte CursorVisible;
    public byte Dirty;
    public ushort DirtyRows;
}

internal static unsafe partial class Native
{
    private const string Vt = "ghostty-vt";
    private const string Shim = "rvt";

    public static void Configure(string nativeDir)
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
        {
            var file = Path.Combine(nativeDir, name + ".dll");
            return File.Exists(file) ? NativeLibrary.Load(file) : IntPtr.Zero;
        });
        // rvt.dll imports ghostty-vt.dll by name; load it first from the same directory.
        NativeLibrary.Load(Path.Combine(nativeDir, Vt + ".dll"));
    }

    [LibraryImport(Vt)] public static partial int ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort cols, ushort rows);
    [LibraryImport(Vt)] public static partial void ghostty_terminal_free(IntPtr terminal);
    [LibraryImport(Vt)] public static partial void ghostty_terminal_reset(IntPtr terminal);
    [LibraryImport(Vt)] public static partial int ghostty_terminal_resize(IntPtr terminal, ushort cols, ushort rows, uint cellW, uint cellH);
    [LibraryImport(Vt)] public static partial int ghostty_terminal_set(IntPtr terminal, int option, void* value);
    [LibraryImport(Vt)] public static partial int ghostty_terminal_get(IntPtr terminal, int data, void* value);
    [LibraryImport(Vt)] public static partial void ghostty_terminal_vt_write(IntPtr terminal, byte* data, nuint len);

    [LibraryImport(Vt)] public static partial int ghostty_render_state_new(IntPtr allocator, out IntPtr state);
    [LibraryImport(Vt)] public static partial void ghostty_render_state_free(IntPtr state);
    [LibraryImport(Vt)] public static partial int ghostty_render_state_update(IntPtr state, IntPtr terminal);
    [LibraryImport(Vt)] public static partial int ghostty_render_state_begin_update(IntPtr state, IntPtr terminal);
    [LibraryImport(Vt)] public static partial int ghostty_render_state_end_update(IntPtr state);

    [LibraryImport(Vt)] public static partial int ghostty_snapshot_encode_buf(IntPtr terminal, byte* buf, nuint len, out nuint written);
    [LibraryImport(Vt)] public static partial int ghostty_snapshot_decoder_new_buf(IntPtr allocator, out IntPtr decoder, byte* ptr, nuint len);
    [LibraryImport(Vt)] public static partial int ghostty_snapshot_decoder_decode(IntPtr decoder, out IntPtr terminal);
    [LibraryImport(Vt)] public static partial void ghostty_snapshot_decoder_free(IntPtr decoder);

    [LibraryImport(Shim)] public static partial IntPtr rvt_reader_new();
    [LibraryImport(Shim)] public static partial int rvt_read_frame(IntPtr reader, IntPtr state, RvtCell* cells, ushort cols, ushort rows, byte* dirtyRows, RvtFrameInfo* info);

    [LibraryImport("winmm.dll")] public static partial uint timeBeginPeriod(uint ms);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [LibraryImport("kernel32.dll")] public static partial IntPtr GetCurrentProcess();
    [LibraryImport("kernel32.dll")] public static partial IntPtr GetCurrentThread();

    /// <summary>CPU cycles charged to this process. Unlike TotalProcessorTime this is not
    /// sampled on 15.6 ms clock ticks, so short bursts are counted.</summary>
    public static ulong ProcessCycles() { QueryProcessCycleTime(GetCurrentProcess(), out var c); return c; }

    /// <summary>Cycles per millisecond, measured by spinning one thread for 200 ms.</summary>
    public static double CalibrateCyclesPerMs()
    {
        QueryThreadCycleTime(GetCurrentThread(), out var c0);
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 200) { }
        QueryThreadCycleTime(GetCurrentThread(), out var c1);
        return (c1 - c0) / System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}
