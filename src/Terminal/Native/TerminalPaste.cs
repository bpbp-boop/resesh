namespace Resesh.Terminal.Native;

/// <summary>Prepares clipboard text for <c>rvt_paste</c>.</summary>
internal static class TerminalPaste
{
    /// <summary>Windows clipboard text ends lines with CR LF. Without bracketed paste,
    /// libghostty-vt turns each LF into CR (as xterm does), so CR LF would become CR CR: two
    /// Enters per line. Bracketed, the program would see CR LF verbatim. One LF per line
    /// gives one Enter unbracketed and the newline programs expect bracketed.</summary>
    public static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
}
