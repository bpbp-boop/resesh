using System.Runtime.InteropServices;
using System.Text;
using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>What copy puts on the clipboard and what paste sends to the program.</summary>
public sealed unsafe class ClipboardTextTests
{
    [NativeFact]
    public void CopyJoinsSoftWrappedRowsAndKeepsHardBreaks()
    {
        var handle = New(out _);
        try
        {
            // 40 characters wrap across three 16-column rows; then a real line break.
            Write(handle, new string('x', 40) + "\r\nnext");
            Assert.Equal(0, GhosttyNative.rvt_select(handle, 0, 0, 3, 3));
            Assert.Equal(new string('x', 40) + "\nnext", SelectionText(handle));
        }
        finally
        {
            GhosttyNative.rvt_free(handle);
        }
    }

    [NativeFact]
    public void WindowsLineEndingsPasteAsOneEnterPerLine()
    {
        var handle = New(out var events);
        try
        {
            Paste(handle, TerminalPaste.Normalize("one\r\ntwo\r\n"));
            Assert.Equal("one\rtwo\r", PtyText(events));

            events.Clear();
            Write(handle, "\u001b[?2004h"); // bracketed paste on
            Paste(handle, TerminalPaste.Normalize("one\r\ntwo"));
            Assert.Equal("\u001b[200~one\ntwo\u001b[201~", PtyText(events));
        }
        finally
        {
            GhosttyNative.rvt_free(handle);
            t_events = null;
        }
    }

    [Fact]
    public void NormalizeLeavesSingleLinesAlone()
    {
        Assert.Equal("a long single line", TerminalPaste.Normalize("a long single line"));
        Assert.Equal("a\nb\nc", TerminalPaste.Normalize("a\r\nb\rc"));
    }

    [ThreadStatic] private static List<byte[]>? t_events;

    [UnmanagedCallersOnly]
    private static void CollectEvent(IntPtr user, int kind, byte* data, nuint length)
    {
        if (kind == (int)GhosttyEventKind.Pty)
            t_events?.Add(new ReadOnlySpan<byte>(data, checked((int)length)).ToArray());
    }

    private static IntPtr New(out List<byte[]> events)
    {
        Assert.Null(GhosttyNative.TryLoad());
        events = t_events = [];
        var handle = GhosttyNative.rvt_new(16, 4, 100, &CollectEvent, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, handle);
        return handle;
    }

    private static void Write(IntPtr handle, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = bytes)
            GhosttyNative.rvt_write(handle, data, (nuint)bytes.Length);
    }

    private static void Paste(IntPtr handle, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = bytes)
            GhosttyNative.rvt_paste(handle, data, (nuint)bytes.Length);
    }

    private static string PtyText(List<byte[]> events) =>
        string.Concat(events.Select(e => Encoding.UTF8.GetString(e)));

    private static string SelectionText(IntPtr handle)
    {
        var buffer = GhosttyNative.rvt_selection_text(handle, out var length);
        Assert.True(buffer != null);
        try
        {
            return Encoding.UTF8.GetString(buffer, checked((int)length));
        }
        finally
        {
            GhosttyNative.rvt_free_buffer(buffer, length);
        }
    }

    private sealed class NativeFactAttribute : FactAttribute
    {
        public NativeFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "The real terminal shim uses Windows SRW locks.";
        }
    }
}
