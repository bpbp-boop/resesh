namespace Resesh.Terminal.Native;

/// <summary>The colors a terminal draws with, as 0xRRGGBB values: defaults plus the 16 ANSI
/// colors. The app builds one from the theme catalog in Core, which this project can't see.
/// <paramref name="SelectionForeground"/> null keeps selected text in its own color.</summary>
public sealed record TerminalTheme(uint Background, uint Foreground, uint Cursor, uint Selection,
    uint? SelectionForeground, uint[] Ansi)
{
    /// <summary>What a surface shows before the app sets its theme: plain light grey on black.</summary>
    internal static TerminalTheme Initial { get; } = new(0x000000, 0xCCCCCC, 0xCCCCCC, 0x444444, null,
    [
        0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
        0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
    ]);
}
