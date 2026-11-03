using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

/// <summary>Asks before a paste that would run commands, showing its first and last lines in
/// a wide monospace preview. Its own dialog rather than <see cref="ConfirmDialog"/>, whose
/// narrow wrapped note mangles long config lines. Cancel is the default button.</summary>
public sealed partial class PasteConfirmDialog : ContentDialog
{
    public string Message { get; }
    public string Preview { get; }

    /// <summary>The "don't ask again" answer, read after the dialog closes.</summary>
    public bool IsDontAskChecked => DontAskBox.IsChecked == true;

    /// <param name="dontAskText">The opt-out's label, which names where to turn the
    /// warning back on; the caller takes that from the Settings catalog. Null hides the
    /// opt-out, for a confirmation that setting does not control.</param>
    public PasteConfirmDialog(string title, string message, string preview, string? dontAskText)
    {
        Message = message;
        Preview = preview;
        InitializeComponent();
        Title = title;
        if (dontAskText is null)
            DontAskBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        else
            DontAskBox.Content = dontAskText;
    }

    /// <summary>Shows the dialog; true when the user chose Paste.</summary>
    public async Task<bool> ConfirmAsync() =>
        await this.ShowModalAsync() == ContentDialogResult.Primary;
}
