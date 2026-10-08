using Windows.ApplicationModel.DataTransfer;

namespace Resesh.Terminal.Ghostty;

/// <summary>Clipboard reads for the ghostty surface (async code cannot live in its unsafe class).</summary>
internal static class GhosttyClipboard
{
    public static async Task PasteIntoAsync(Action<string> paste)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
                paste(await content.GetTextAsync());
        }
        catch (Exception exception)
        {
            TerminalSurface.TraceHook?.Invoke($"clipboard paste failed: {exception.Message}");
        }
    }
}
