using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

public sealed unsafe class NativeFrameTests
{
    [WindowsNativeFact]
    public void HyperlinkHoverCoversEverySoftWrappedRow()
    {
        using var terminal = new NativeTerminal();
        const string uri = "https://example.com/login";
        terminal.Write("pre\u001b]8;;" + uri + "\u001b\\" + new string('x', 34) + "\u001b]8;;\u001b\\suffix");
        terminal.ReadFrame(forceAll: true);
        var buffer = new TerminalCommandBuffer(() => terminal.Handle);
        buffer.Refresh();
        foreach (var (x, y) in new[] { (3, 0), (8, 1), (4, 2) })
        {
            var hit = buffer.HyperlinkAt(x, y)!.Value;
            Assert.Equal(uri, hit.Uri);
            Assert.Equal([(0, 3, 16), (1, 0, 16), (2, 0, 5)], hit.Spans);
        }
        Assert.Null(buffer.HyperlinkAt(2, 0));
        Assert.Null(buffer.HyperlinkAt(5, 2));
    }

    [WindowsNativeFact]
    public void HyperlinkHoverStopsAtHardBreaksAndDifferentLinks()
    {
        using var terminal = new NativeTerminal();
        const string open = "\u001b]8;;https://example.com\u001b\\";
        const string close = "\u001b]8;;\u001b\\";
        terminal.Write(open + new string('a', 16) + close + "\r\n"
            + open + new string('b', 16) + close
            + "\u001b]8;;https://other.example\u001b\\next" + close);
        terminal.ReadFrame(forceAll: true);
        var buffer = new TerminalCommandBuffer(() => terminal.Handle);
        buffer.Refresh();
        Assert.Equal([(0, 0, 16)], buffer.HyperlinkAt(0, 0)!.Value.Spans);
        Assert.Equal([(1, 0, 16)], buffer.HyperlinkAt(0, 1)!.Value.Spans);
        Assert.Equal([(2, 0, 4)], buffer.HyperlinkAt(0, 2)!.Value.Spans);
    }

    [WindowsNativeFact]
    public void MarkersGrowBeyond4096AndKeepTheirIds()
    {
        using var terminal = new NativeTerminal();
        var markers = new uint[9000];
        for (var i = 0; i < markers.Length; i++)
        {
            markers[i] = GhosttyNative.rvt_marker_new(terminal.Handle, 0);
            Assert.Equal((uint)i + 1, markers[i]);
        }
        terminal.Write("line\r\nnext\r\n");
        foreach (var marker in markers)
            Assert.Equal(0, GhosttyNative.rvt_marker_line(terminal.Handle, marker));
        GhosttyNative.rvt_marker_free(terminal.Handle, markers[17]);
        Assert.Equal(-1, GhosttyNative.rvt_marker_line(terminal.Handle, markers[17]));
        Assert.Equal(markers[17], GhosttyNative.rvt_marker_new(terminal.Handle, 1));
        Assert.Equal(1, GhosttyNative.rvt_marker_line(terminal.Handle, markers[17]));
        Assert.Equal(-1, GhosttyNative.rvt_marker_line(terminal.Handle, uint.MaxValue));
        GhosttyNative.rvt_marker_free(terminal.Handle, uint.MaxValue);
        Assert.Equal(0, GhosttyNative.rvt_marker_line(terminal.Handle, markers[^1]));
    }

    [WindowsNativeFact]
    public void FrameReportsTheOsc22PointerShape()
    {
        using var terminal = new NativeTerminal();
        terminal.ReadFrame();
        Assert.Equal(GhosttyNative.MouseShapeText, terminal.Info.MouseShape);
        terminal.Write("\u001b]22;pointer\u001b\\");
        terminal.ReadFrame();
        Assert.Equal(3, terminal.Info.MouseShape);
        terminal.Write("\u001b]22;default\u0007");
        terminal.ReadFrame();
        Assert.Equal(0, terminal.Info.MouseShape);
    }

    [ThreadStatic] private static List<(int Kind, byte[] Data)>? t_events;

    [UnmanagedCallersOnly]
    private static void CollectEvent(IntPtr user, int kind, byte* data, nuint length) =>
        t_events?.Add((kind, new ReadOnlySpan<byte>(data, checked((int)length)).ToArray()));

    [WindowsNativeFact]
    public void ProgramStatusReportsReachTheHostAndTheProbeIsAnswered()
    {
        Assert.Null(GhosttyNative.TryLoad());
        var events = t_events = [];
        var handle = GhosttyNative.rvt_new(80, 24, 100, &CollectEvent, IntPtr.Zero);
        try
        {
            void Write(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                fixed (byte* data = bytes)
                    GhosttyNative.rvt_write(handle, data, (nuint)bytes.Length);
            }
            string[] Oscs() => events.Where(e => e.Kind == (int)GhosttyEventKind.Osc)
                .Select(e => Encoding.UTF8.GetString(e.Data)).ToArray();

            // What oh-my-pi 18 sends at startup, byte for byte.
            Write("\u001b]7501;state=idle:app=omp\u001b\\");
            Assert.Equal(["7501;0;0;-1\u001F\u001Fomp\u001F\u001F"], Oscs());

            events.Clear();
            Write("\u001b]7501;state=blocked:kind=permission:app=terraform:msg=QXBwbHk/\u0007");
            Assert.Equal(["7501;3;1;-1\u001F\u001Fterraform\u001F\u001FApply?"], Oscs());

            // Programs that wait for support before reporting ask first.
            events.Clear();
            Write("\u001b]7501;?\u001b\\");
            var reply = Assert.Single(events, e => e.Kind == (int)GhosttyEventKind.Pty);
            Assert.StartsWith("\u001b]7501;", Encoding.UTF8.GetString(reply.Data));
        }
        finally
        {
            GhosttyNative.rvt_free(handle);
            t_events = null;
        }
    }

    [WindowsNativeFact]
    public void TrimmedNativeMarkersCanBeFreedAndReused()
    {
        using var terminal = new NativeTerminal();
        GhosttyNative.rvt_set_scrollback(terminal.Handle, 4);
        var marker = GhosttyNative.rvt_marker_new(terminal.Handle, 0);
        Assert.NotEqual(0u, marker);
        terminal.Write(string.Concat(Enumerable.Repeat("line\r\n", 10000)));
        Assert.Equal(-1, GhosttyNative.rvt_marker_line(terminal.Handle, marker));
        GhosttyNative.rvt_marker_free(terminal.Handle, marker);
        Assert.Equal(marker, GhosttyNative.rvt_marker_new(terminal.Handle, 0));
        Assert.Equal(0, GhosttyNative.rvt_marker_line(terminal.Handle, marker));
    }

    [WindowsNativeFact]
    public void NativeAdapterRetiresInvalidMarkersOnLineAndDisposedReads()
    {
        using var terminal = new NativeTerminal();
        GhosttyNative.rvt_set_scrollback(terminal.Handle, 4);
        var buffer = new TerminalCommandBuffer(() => terminal.Handle);
        var first = Assert.IsAssignableFrom<ICommandMarker>(buffer.CreateMarker(0));
        var second = Assert.IsAssignableFrom<ICommandMarker>(buffer.CreateMarker(0));
        terminal.Write(string.Concat(Enumerable.Repeat("line\r\n", 10000)));
        Assert.Equal(-1, first.Line);
        Assert.True(second.IsDisposed);
        var reused = GhosttyNative.rvt_marker_new(terminal.Handle, 0);
        Assert.Equal(2u, reused);
        first.Dispose();
        second.Dispose();
        Assert.Equal(0, GhosttyNative.rvt_marker_line(terminal.Handle, reused));
        GhosttyNative.rvt_marker_free(terminal.Handle, reused);
        buffer.Detach();
    }

    [WindowsNativeFact]
    public void NativeAdapterDetachFreesMarkersWhenTerminalIsReused()
    {
        using var terminal = new NativeTerminal();
        var buffer = new TerminalCommandBuffer(() => terminal.Handle);
        var first = Assert.IsAssignableFrom<ICommandMarker>(buffer.CreateMarker(0));
        var second = Assert.IsAssignableFrom<ICommandMarker>(buffer.CreateMarker(1));
        buffer.Detach();
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(-1, GhosttyNative.rvt_marker_line(terminal.Handle, 1));
        Assert.Equal(-1, GhosttyNative.rvt_marker_line(terminal.Handle, 2));
        var reused = GhosttyNative.rvt_marker_new(terminal.Handle, 0);
        Assert.InRange(reused, 1u, 2u);
        first.Dispose();
        second.Dispose();
        buffer.Detach();
        Assert.Equal(0, GhosttyNative.rvt_marker_line(terminal.Handle, reused));
        GhosttyNative.rvt_marker_free(terminal.Handle, reused);
    }

    [WindowsNativeFact]
    public void SynchronizedOutputKeepsThePreviousFrameUntilReleased()
    {
        using var terminal = new NativeTerminal();
        terminal.Write("old");
        Assert.True(terminal.ReadFrame(forceAll: true) > 0);
        var before = terminal.Info;
        Array.Clear(terminal.Dirty);
        terminal.Write("\u001b[?2026h\rnew");
        Assert.Equal(0, terminal.ReadFrame(forceAll: true));
        Assert.Equal(1, terminal.Info.RenderHeld);
        Assert.Equal(0, terminal.Info.Dirty);
        Assert.Equal(0, terminal.Info.DirtyRows);
        Assert.Equal((uint)'o', terminal.Cells[0].Codepoint);
        Assert.Equal(before.CursorX, terminal.Info.CursorX);
        Assert.All(terminal.Dirty, value => Assert.Equal(0, value));
        terminal.Write("\u001b[?2026l");
        Assert.True(terminal.ReadFrame() > 0);
        Assert.Equal(0, terminal.Info.RenderHeld);
        Assert.Equal((uint)'n', terminal.Cells[0].Codepoint);
        Assert.Equal((uint)'w', terminal.Cells[2].Codepoint);
    }

    [WindowsNativeFact]
    public void TerminalResetReleasesSynchronizedOutput()
    {
        using var terminal = new NativeTerminal();
        terminal.Write("old");
        terminal.ReadFrame(forceAll: true);
        terminal.Write("\u001b[?2026h\rnew");
        Assert.Equal(0, terminal.ReadFrame());
        terminal.Write("\u001bcreset");
        Assert.True(terminal.ReadFrame(forceAll: true) > 0);
        Assert.Equal(0, terminal.Info.RenderHeld);
        Assert.Equal((uint)'r', terminal.Cells[0].Codepoint);
    }

    [WindowsNativeFact]
    public void SynchronizedOutputTimesOutWithoutFurtherOutput()
    {
        using var terminal = new NativeTerminal();
        terminal.Write("old");
        terminal.ReadFrame(forceAll: true);
        terminal.Write("\u001b[?2026h\rnew");
        Assert.Equal(0, terminal.ReadFrame());
        Assert.Equal(1, terminal.Info.RenderHeld);
        var watch = Stopwatch.StartNew();
        while (terminal.Info.RenderHeld != 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(20);
            terminal.ReadFrame();
        }
        Assert.Equal(0, terminal.Info.RenderHeld);
        Assert.Equal((uint)'n', terminal.Cells[0].Codepoint);
        // The timeout resets the terminal mode as well as releasing this renderer's hold.
        terminal.Write("\u001b[?2026h\rnext");
        Assert.Equal(0, terminal.ReadFrame());
        Assert.Equal(1, terminal.Info.RenderHeld);
        Assert.Equal((uint)'w', terminal.Cells[2].Codepoint);
    }

    [WindowsNativeFact]
    public void ResizeReleasesSynchronizedOutput()
    {
        using var terminal = new NativeTerminal();
        terminal.Write("old");
        terminal.ReadFrame(forceAll: true);
        terminal.Write("\u001b[?2026h\rnew");
        Assert.Equal(0, terminal.ReadFrame());
        GhosttyNative.rvt_resize(terminal.Handle, NativeTerminal.Columns, NativeTerminal.Rows, 8, 17);
        Assert.True(terminal.ReadFrame(forceAll: true) > 0);
        Assert.Equal(0, terminal.Info.RenderHeld);
        Assert.Equal((uint)'n', terminal.Cells[0].Codepoint);
    }

    private sealed class WindowsNativeFactAttribute : FactAttribute
    {
        public WindowsNativeFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "The real terminal shim uses Windows SRW locks.";
        }
    }

    private sealed class NativeTerminal : IDisposable
    {
        public const ushort Columns = 16, Rows = 4;
        public IntPtr Handle { get; }
        public TerminalCell[] Cells { get; } = new TerminalCell[Columns * Rows];
        public byte[] Dirty { get; } = new byte[Rows];
        public GhosttyFrameInfo Info;

        public NativeTerminal()
        {
            Assert.Null(GhosttyNative.TryLoad());
            Handle = GhosttyNative.rvt_new(Columns, Rows, 10000, null, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, Handle);
        }

        public void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            fixed (byte* data = bytes)
                GhosttyNative.rvt_write(Handle, data, (nuint)bytes.Length);
        }

        public int ReadFrame(bool forceAll = false)
        {
            fixed (TerminalCell* cells = Cells)
            fixed (byte* dirty = Dirty)
            fixed (GhosttyFrameInfo* info = &Info)
                return GhosttyNative.rvt_read_frame(Handle, cells, Columns, Rows, dirty, forceAll ? 1 : 0, info);
        }

        public void Dispose() => GhosttyNative.rvt_free(Handle);
    }
}
