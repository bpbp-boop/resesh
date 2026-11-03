using Windows.ApplicationModel.DataTransfer;

namespace Resesh.Terminal;

/// <summary>The one way resesh copies text, for the terminal and the app alike. Another
/// program holding the clipboard open makes <see cref="Clipboard.SetContent"/> throw
/// (CLIPBRD_E_CANT_OPEN), which must not crash a click handler; and only a flushed copy
/// outlives resesh, so text copied just before quitting is not lost.</summary>
public static class TextClipboard
{
    /// <summary>Copies <paramref name="text"/>; false when the clipboard could not be set.</summary>
    public static bool TryCopy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception exception)
        {
            TerminalSurface.TraceHook?.Invoke($"clipboard copy failed: {exception.Message}");
            return false;
        }
        try
        {
            Clipboard.Flush();
        }
        catch (Exception exception)
        {
            // The text is on the clipboard; it just goes when resesh exits.
            TerminalSurface.TraceHook?.Invoke($"clipboard flush failed: {exception.Message}");
        }
        return true;
    }
}
