using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App;

/// <summary>Provides a task-returning wrapper for WinUI content dialogs.</summary>
internal static class ModalDialogPresenter
{
    internal static async Task<ContentDialogResult> ShowModalAsync(this ContentDialog dialog)
    {
        // Popup roots do not inherit the window's requested theme automatically.
        Dialogs.DialogTheme.Apply(dialog);
        if (dialog.XamlRoot?.Content is FrameworkElement owner)
            dialog.RequestedTheme = owner.ActualTheme;
        return await dialog.ShowAsync();
    }
}
