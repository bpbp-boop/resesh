using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Backend;
using Resesh.Core.Input;

namespace Resesh.App.Terminal;

/// <summary>Safe paste: asks before a paste that would press Enter, and sends it slowly to
/// sessions that set a line or character delay. Typing anything stops a slow paste, and so
/// does the connection ending: a paste belongs to the connection it was made on and never
/// carries on into a reconnect.</summary>
public sealed partial class TerminalTabView
{
    private CancellationTokenSource? _pacedPaste;
    private string? _pasteProgress;
    private string _summaryBeforePaste = "";

    /// <summary>The surface's paste hook: false lets the surface paste at once.</summary>
    private bool InterceptPaste(string text) => InterceptPaste(text, insert: false);

    /// <summary>Pastes a command the app types for the user to review (a history entry)
    /// through the same safe-paste path as a clipboard paste. Completes once the text is on
    /// its way: false when there is nothing to paste, the user declined the confirmation, or
    /// the connection ended while it was open.</summary>
    private async Task<bool> PasteForReviewAsync(string text)
    {
        // A trailing line break would press Enter on the last line too.
        text = PastePlan.Normalize(text).TrimEnd('\n');
        if (text.Length == 0)
            return false;
        if (StartSafePaste(text, insert: true) is { } paste)
            return await paste;
        _terminal.PasteText(text);
        return true;
    }

    private bool InterceptPaste(string text, bool insert)
    {
        if (StartSafePaste(text, insert) is not { } paste)
            return false;
        _ = paste;
        return true;
    }

    /// <summary>Starts a paste that needs asking or pacing; null when the surface can paste it
    /// at once.</summary>
    /// <param name="insert">The text is a command inserted for review, which promises not to
    /// run it: a line break that would press Enter always asks, whatever the paste setting,
    /// and the dialog offers no opt-out because that setting would not silence it.</param>
    private Task<bool>? StartSafePaste(string text, bool insert)
    {
        if (_disposed || _tab.IsLocked)
            return null;
        var bracketed = _terminal.BracketedPaste;
        var confirm = (insert || App.Settings.Current.ConfirmMultilinePaste) && PastePlan.NeedsConfirmation(text, bracketed);
        // A program with bracketed paste on takes the whole paste as one piece of text.
        var paced = !bracketed && PastePlan.IsPaced(Session.PasteLineDelay, Session.PasteCharacterDelay);
        if (!confirm && !paced)
            return null;
        return PasteAsync(text, confirm, paced, insert, _connection);
    }

    /// <summary>Stops a slow paste as soon as the tab leaves the connected state.</summary>
    private void WirePaste()
    {
        _tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.State) && _tab.State != TabConnectionState.Connected)
                CancelPacedPaste();
        };
    }

    private bool IsLive(TerminalConnectionLifetime connection) =>
        !_disposed && ReferenceEquals(_connection, connection) && !connection.Token.IsCancellationRequested
        && _tab.State == TabConnectionState.Connected;

    /// <summary>Asks first when <paramref name="confirm"/>, then pastes or starts a slow paste.
    /// Completes when the text is on its way (a slow paste carries on): false when the user
    /// declined or the connection ended meanwhile.</summary>
    private async Task<bool> PasteAsync(string text, bool confirm, bool paced, bool insert, TerminalConnectionLifetime connection)
    {
        if (confirm && !await ConfirmPasteAsync(text, insert))
        {
            // A declined insert goes back to the history it came from, which is still open.
            if (!insert)
                _terminal.FocusTerminal();
            return false;
        }
        // The connection may have dropped or been replaced while the dialog was open.
        if (!IsLive(connection))
            return false;
        _terminal.FocusTerminal();
        if (!paced)
        {
            _terminal.PasteText(text);
            return true;
        }
        _ = SendPacedAsync(text, connection);
        return true;
    }

    private async Task SendPacedAsync(string text, TerminalConnectionLifetime connection)
    {
        CancelPacedPaste();
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
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
                chunk => SendPacedChunk(chunk, connection), progress, cancel.Token);
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

    /// <summary>Sends one piece raw, not through the surface's paste encoder (DECISIONS
    /// 2026-10-30). A piece that ends a line presses Enter, so the surface is told, as it is
    /// for a typed Enter or an unbracketed paste, and a shell without integration still gets
    /// the command marked and recorded.</summary>
    private void SendPacedChunk(PasteChunk chunk, TerminalConnectionLifetime connection)
    {
        if (!IsLive(connection))
        {
            CancelPacedPaste();
            return;
        }
        _terminal.SendInput(chunk.Bytes, pressesEnter: chunk.EndsLine);
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

    private async Task<bool> ConfirmPasteAsync(string text, bool insert)
    {
        var lines = PastePlan.LineBreaks(text);
        var verb = insert ? "Insert" : "Paste";
        var dialog = new PasteConfirmDialog(
            lines == 1 ? $"{verb} and run 1 line?" : $"{verb} and run {lines} lines?",
            verb,
            insert
                ? $"The shell on {Session.Name} has no bracketed paste, so each line break presses Enter and runs that line as soon as it arrives."
                : $"Each line runs on {Session.Name} as soon as it arrives.",
            PastePlan.Preview(text),
            insert ? null : $"Don't ask again (turn it back on in {SettingsCatalog.PathTo(GlobalSettingsTarget.ConfirmMultilinePaste)})")
        {
            XamlRoot = XamlRoot,
        };
        var confirmed = await dialog.ConfirmAsync();
        if (confirmed && dialog.IsDontAskChecked)
            App.SaveSetting(s => s with { ConfirmMultilinePaste = false }, nameof(SettingsViewModel.ConfirmMultilinePaste));
        return confirmed;
    }
}
