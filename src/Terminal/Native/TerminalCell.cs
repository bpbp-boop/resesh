using System.Runtime.InteropServices;

namespace Resesh.Terminal.Native;

/// <summary>One viewport cell as reseshvt.dll flattens it (see RvtCell in reseshvt.c).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TerminalCell
{
    public uint Codepoint;
    public uint Foreground;
    public uint Background;
    public ushort Flags;
    public byte Wide;
    public byte GraphemeLength;
    public uint UnderlineColor; // 0x00RRGGBB, or DefaultUnderlineColor (draw in the foreground)

    public const ushort Bold = 1, Italic = 2, Underline = 4, Strike = 8, DefaultBackground = 16,
        Faint = 32, Invisible = 64, Selected = 128, Match = 256, MatchCurrent = 512, Overline = 8192;
    /// <summary>With <see cref="Underline"/>: bits 10-12 hold the SGR 4:n style (0 or 1 single,
    /// 2 double, 3 curly, 4 dotted, 5 dashed).</summary>
    public const int UnderlineStyleShift = 10;
    public const ushort UnderlineStyleMask = 7 << UnderlineStyleShift;
    public const uint DefaultUnderlineColor = 0x01000000;

    public readonly int UnderlineStyle => (Flags & UnderlineStyleMask) >> UnderlineStyleShift;
    public const byte Narrow = 0, WideChar = 1, SpacerTail = 2, SpacerHead = 3;
}
