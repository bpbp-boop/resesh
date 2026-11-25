using System.Text;

namespace Resesh.Terminal.Native;

/// <summary>
/// Resets terminal modes a program may have left behind (SGR mouse tracking, Kitty keyboard
/// protocol, alternate screen, bracketed paste, focus reporting, synchronized output,
/// scrolling region, cursor visibility/style, OSC 8 hyperlink, OSC 22 pointer shape, dynamic
/// colors, or a half-received escape sequence) without clearing scrollback or primary screen
/// content. Used on disconnect, before reconnect, and for the manual Reset Terminal action.
/// </summary>
internal static unsafe class TerminalModeReset
{
    private const string Cancel = "\u0018";

    // Sent only while the alternate screen is active: pops its Kitty keyboard stack, then
    // leaves it (restoring the saved primary cursor when ?1049h saved it) and clears the
    // legacy alternate-screen bits. Never sent on the primary screen, where CSI ? 1049 l
    // unconditionally calls restoreCursor() and jumps to a stale saved position or (0, 0).
    private const string LeaveAlternateScreen = "\u001b[<99u\u001b[?1049l\u001b[?1047l\u001b[?47l";

    // Primary screen reset:
    // - CSI < 99 u: pops the entire Kitty keyboard stack (length 8 in libghostty-vt).
    // - CSI ! p (DECSTR): resets cursor visibility/style, SGR, charsets, scroll margins,
    //   saved cursor, modifyOtherKeys, palette, and modes 2/4/?1/?6/?7/?45/?66/?69/?1045.
    // - Modes DECSTR leaves behind: SRM (12), LNM (20), slow scroll (?4), reverse video (?5),
    //   autorepeat (?8), mouse tracking (?9/?1000/?1002/?1003), cursor blink (?12),
    //   allow-132 (?40), backarrow (?67), focus events (?1004), mouse formats
    //   (?1005/?1006/?1015/?1016), alt scroll (?1007), keypad/alt (?1035/?1036/?1039),
    //   bracketed paste (?2004), synchronized output (?2026, releases any render hold),
    //   grapheme clustering (?2027, enabled by default in rvt_new), color scheme (?2031),
    //   visibility (?2033), in-band size reports (?2048), kitty paste events (?5522).
    // - OSC 8 (close hyperlink), OSC 22 (reset pointer shape to text), OSC 110/111/112
    //   (reset dynamic foreground/background/cursor color overrides).
    private const string ResetPrimaryModes =
        "\u001b[<99u"
        + "\u001b[!p"
        + "\u001b[12h\u001b[20l"
        + "\u001b[?4l\u001b[?5l\u001b[?8l\u001b[?9l\u001b[?12l\u001b[?40l\u001b[?67l"
        + "\u001b[?1000l\u001b[?1002l\u001b[?1003l\u001b[?1004l\u001b[?1005l\u001b[?1006l\u001b[?1007h"
        + "\u001b[?1015l\u001b[?1016l\u001b[?1035h\u001b[?1036h\u001b[?1039l"
        + "\u001b[?2004l\u001b[?2026l\u001b[?2027h\u001b[?2031l\u001b[?2033l\u001b[?2048l\u001b[?5522l"
        + "\u001b]8;;\u001b\\"
        + "\u001b]22;\u001b\\"
        + "\u001b]110\u001b\\\u001b]111\u001b\\\u001b]112\u001b\\";

    private static readonly byte[] PrimarySequence = Encoding.ASCII.GetBytes(Cancel + ResetPrimaryModes);
    private static readonly byte[] AlternateSequence = Encoding.ASCII.GetBytes(Cancel + LeaveAlternateScreen + ResetPrimaryModes);

    /// <summary>The VT byte sequence that resets <paramref name="term"/> from its current screen
    /// state. Returning it as one slice lets callers emit the same transition into recording and
    /// rewind capture streams as into the live terminal.</summary>
    public static ReadOnlySpan<byte> SequenceFor(IntPtr term)
    {
        if (term == IntPtr.Zero)
            return [];
        GhosttyBufferInfo info;
        GhosttyNative.rvt_buffer_info(term, &info);
        return info.Alternate != 0 ? AlternateSequence : PrimarySequence;
    }

    public static void Apply(IntPtr term)
    {
        var sequence = SequenceFor(term);
        if (sequence.IsEmpty)
            return;
        fixed (byte* p = sequence)
            GhosttyNative.rvt_write(term, p, (nuint)sequence.Length);
    }
}
