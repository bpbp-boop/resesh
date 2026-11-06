using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace Resesh.App;

/// <summary>Provides a task-returning wrapper for WinUI content dialogs.</summary>
internal static class ModalDialogPresenter
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    /// <summary>Completes once every dialog queued on the window before this call has closed,
    /// without showing anything. A prompt whose text describes the window's current state
    /// (the exit confirmation's session count) waits here and builds its dialog afterwards,
    /// so the text isn't from before the dialogs ahead of it were answered.</summary>
    internal static async Task WaitForQueuedDialogsAsync(XamlRoot xamlRoot)
    {
        var dialogGate = DialogGates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        await dialogGate.WaitAsync();
        dialogGate.Release();
    }

    /// <summary>
    /// Shows <paramref name="dialog"/> once every dialog queued before it on the same window
    /// has closed. A cancelled token means the asker is gone (its tab closed or its connect
    /// was replaced): a dialog still queued is never shown, and one on screen is hidden.
    /// Both return <see cref="ContentDialogResult.None"/>, as do a dialog with no window and
    /// a window that closed, so fire-and-forget callers never see an exception from here.
    /// </summary>
    internal static async Task<ContentDialogResult> ShowModalAsync(
        this ContentDialog dialog, CancellationToken cancellationToken = default)
    {
        var xamlRoot = dialog.XamlRoot;
        if (xamlRoot is null || cancellationToken.IsCancellationRequested)
            return ContentDialogResult.None;
        var dialogGate = DialogGates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        try
        {
            await dialogGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ContentDialogResult.None;
        }
        try
        {
            // Asked to stop while queued, or the window closed while this dialog waited.
            if (cancellationToken.IsCancellationRequested || xamlRoot.Content is null)
                return ContentDialogResult.None;
            // Popup roots do not inherit the window's requested theme automatically.
            Dialogs.DialogTheme.Apply(dialog);
            if (xamlRoot.Content is FrameworkElement owner)
                dialog.RequestedTheme = owner.ActualTheme;
            // Cancellation can come from any thread; Hide must run on the dialog's.
            var dispatcher = dialog.DispatcherQueue;
            using var hide = cancellationToken.Register(() => dispatcher.TryPost(dialog.Hide));
            try
            {
                var result = await dialog.ShowAsync();
                return cancellationToken.IsCancellationRequested ? ContentDialogResult.None : result;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || xamlRoot.Content is null)
            {
                return ContentDialogResult.None;
            }
        }
        finally
        {
            dialogGate.Release();
        }
    }
}
