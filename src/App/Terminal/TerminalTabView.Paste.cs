using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Input;

namespace Resesh.App.Terminal;

/// <summary>Safe paste: asks before a paste that would press Enter, and sends it slowly to
/// sessions that set a line or character delay. Typing anything stops a slow paste.</summary>
public sealed partial class TerminalTabView
{
    private CancellationTokenSource? _pacedPaste;
    private string? _pasteProgress;
    private string _summaryBeforePaste = "";

    /// <summary>The surface's paste hook: false lets the surface paste at once.</summary>
    private bool InterceptPaste(string text)
    {
        if (_disposed || _tab.IsLocked)
            return false;
        var bracketed = _terminal.BracketedPaste;
        var confirm = App.Settings.Current.ConfirmMultilinePaste && PastePlan.NeedsConfirmation(text, bracketed);
        // A program with bracketed paste on takes the whole paste as one piece of text.
        var paced = !bracketed && PastePlan.IsPaced(Session.PasteLineDelay, Session.PasteCharacterDelay);
        if (!confirm && !paced)
            return false;
        _ = PasteAsync(text, confirm, paced);
        return true;
    }

    private async Task PasteAsync(string text, bool confirm, bool paced)
    {
        if (confirm && !await ConfirmPasteAsync(text))
        {
            _terminal.FocusTerminal();
            return;
        }
        if (_disposed || _tab.State != TabConnectionState.Connected)
            return;
        _terminal.FocusTerminal();
        if (!paced)
        {
            _terminal.PasteText(text);
            return;
        }

        CancelPacedPaste();
        var cancel = new CancellationTokenSource();
        _pacedPaste = cancel;
        var total = PastePlan.LineBreaks(text);
        _summaryBeforePaste = _tab.ConnectionSummary;
        ShowPasteProgress(0, total);
        var progress = new Progress<int>(sent => ShowPasteProgress(sent, total));
        try
        {
            // Chunks go out on the UI thread through the surface, as typing does; the waits
            // between them are timer awaits, so the window stays responsive.
            await PastePlan.SendAsync(
                PastePlan.Chunks(text, Session.PasteLineDelay, Session.PasteCharacterDelay),
                SendPacedChunk, progress, cancel.Token);
        }
        finally
        {
            if (ReferenceEquals(_pacedPaste, cancel))
            {
                _pacedPaste = null;
                EndPasteProgress();
            }
            cancel.Dispose();
        }
    }

    private void SendPacedChunk(byte[] bytes)
    {
        if (_disposed || _tab.State != TabConnectionState.Connected)
        {
            CancelPacedPaste();
            return;
        }
        _terminal.SendInput(bytes);
    }

    /// <summary>The surface's typed-input filter: a key stops a slow paste, and a lone Esc
    /// only stops it and is not sent.</summary>
    private bool StopsPacedPaste(byte[] data)
    {
        if (_pacedPaste is null)
            return false;
        CancelPacedPaste();
        return data is [0x1B];
    }

    private void CancelPacedPaste()
    {
        var cancel = _pacedPaste;
        if (cancel is null)
            return;
        _pacedPaste = null;
        cancel.Cancel();
        EndPasteProgress();
    }

    private void ShowPasteProgress(int sent, int total)
    {
        if (_disposed)
            return;
        // A disconnect clears the summary; don't paint progress over that.
        if (_pasteProgress is not null && _tab.ConnectionSummary != _pasteProgress)
            return;
        _pasteProgress = $"pasting line {Math.Min(sent + 1, Math.Max(total, 1))} of {Math.Max(total, 1)} · type to stop";
        _tab.ConnectionSummary = _pasteProgress;
    }

    private void EndPasteProgress()
    {
        if (_pasteProgress is not null && _tab.ConnectionSummary == _pasteProgress)
            _tab.ConnectionSummary = _summaryBeforePaste;
        _pasteProgress = null;
    }

    private async Task<bool> ConfirmPasteAsync(string text)
    {
        var lines = PastePlan.LineBreaks(text);
        var dialog = new ConfirmDialog(
            lines == 1 ? "Paste and run 1 line?" : $"Paste and run {lines} lines?",
            $"Each line runs on {Session.Name} as soon as it arrives.",
            "Paste")
        {
            XamlRoot = XamlRoot,
            Note = PastePlan.Preview(text),
            OptionText = "Don't ask again (turn it back on in Settings › Terminal)",
        };
        var confirmed = await dialog.ConfirmAsync();
        if (confirmed && dialog.IsOptionChecked)
            App.SaveSettings(App.Settings.Current with { ConfirmMultilinePaste = false });
        return confirmed;
    }
}
