using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace Resesh.App;

/// <summary>Provides a task-returning wrapper for WinUI content dialogs.</summary>
internal static class ModalDialogPresenter
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    internal static async Task<ContentDialogResult> ShowModalAsync(this ContentDialog dialog)
    {
        var xamlRoot = dialog.XamlRoot
            ?? throw new InvalidOperationException("Content dialogs require a XamlRoot.");
        var dialogGate = DialogGates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        await dialogGate.WaitAsync();
        try
        {
            // Popup roots do not inherit the window's requested theme automatically.
            Dialogs.DialogTheme.Apply(dialog);
            if (xamlRoot.Content is FrameworkElement owner)
                dialog.RequestedTheme = owner.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            dialogGate.Release();
        }
    }
}
