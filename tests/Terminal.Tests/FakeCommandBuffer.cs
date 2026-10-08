using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>An in-memory ICommandBuffer: lines, soft wraps, a cursor, and trimming from the top.</summary>
internal sealed class FakeMarker(FakeBuffer buffer, int line) : ICommandMarker
{
    public int Origin = line;
    public bool Disposed;
    public int Line => Disposed ? -1 : Origin - buffer.Trimmed < 0 ? -1 : Origin - buffer.Trimmed;
    public bool IsDisposed => Disposed || Line < 0;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeBuffer : ICommandBuffer
{
    public readonly List<(string Text, bool Wrapped)> Lines = [];
    public int Trimmed; // lines dropped from the top of the scrollback
    public bool IsAlternate { get; set; }
    public int CursorLine { get; set; }
    public int CursorX { get; set; }
    public int ViewportTop { get; set; }
    public int Rows { get; set; } = 10;
    public int Length => Lines.Count - Trimmed;
    public string? LineText(int line) => line >= 0 && line + Trimmed < Lines.Count ? Lines[line + Trimmed].Text.TrimEnd() : null; // trimmed, like the real buffer
    public bool IsWrapped(int line) => line >= 0 && line + Trimmed < Lines.Count && Lines[line + Trimmed].Wrapped;
    public ICommandMarker? CreateMarker(int line) => new FakeMarker(this, line + Trimmed);

    public int Add(string text, bool wrapped = false)
    {
        Lines.Add((text, wrapped));
        CursorLine = Length - 1;
        return Length - 1;
    }
}
