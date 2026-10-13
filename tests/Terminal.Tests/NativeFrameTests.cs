using System.Diagnostics;
using System.Text;
using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

public sealed unsafe class NativeFrameTests
{
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
