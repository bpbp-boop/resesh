using System.Runtime.InteropServices;

namespace Resesh.Terminal.Ghostty;

/// <summary>One viewport cell as reseshvt.dll flattens it (see RvtCell in reseshvt.c).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyCell
{
    public uint Codepoint;
    public uint Foreground;
    public uint Background;
    public ushort Flags;
    public byte Wide;
    public byte GraphemeLength;

    public const ushort Bold = 1, Italic = 2, Underline = 4, Strike = 8, DefaultBackground = 16,
        Faint = 32, Invisible = 64, Selected = 128, Match = 256, MatchCurrent = 512;
    public const byte Narrow = 0, WideChar = 1, SpacerTail = 2, SpacerHead = 3;
}
