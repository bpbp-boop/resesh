using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App;

/// <summary>Provides a task-returning wrapper for WinUI content dialogs.</summary>
internal static class ModalDialogPresenter
{
    internal static async Task<ContentDialogResult> ShowModalAsync(this ContentDialog dialog) =>
        await dialog.ShowAsync();
}
