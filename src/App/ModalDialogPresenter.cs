using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App;

/// <summary>
/// Shows modal XAML dialogs while native child-window surfaces are hidden. Native HWNDs
/// always render above XAML popups, so each window must remove them for the full modal lifetime.
/// </summary>
internal static class ModalDialogPresenter
{
    private static readonly Dictionary<XamlRoot, int> OpenDialogCounts = [];

    internal static event Action<XamlRoot, bool>? OpenStateChanged;

    internal static async Task<ContentDialogResult> ShowModalAsync(this ContentDialog dialog)
    {
        var xamlRoot = dialog.XamlRoot
            ?? throw new InvalidOperationException("Modal dialogs require a XamlRoot.");

        Enter(xamlRoot);
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Exit(xamlRoot);
        }
    }

    private static void Enter(XamlRoot xamlRoot)
    {
        if (OpenDialogCounts.TryGetValue(xamlRoot, out var count))
        {
            OpenDialogCounts[xamlRoot] = count + 1;
            return;
        }

        OpenDialogCounts.Add(xamlRoot, 1);
        OpenStateChanged?.Invoke(xamlRoot, true);
    }

    private static void Exit(XamlRoot xamlRoot)
    {
        if (!OpenDialogCounts.TryGetValue(xamlRoot, out var count))
            return;
        if (count > 1)
        {
            OpenDialogCounts[xamlRoot] = count - 1;
            return;
        }

        OpenDialogCounts.Remove(xamlRoot);
        OpenStateChanged?.Invoke(xamlRoot, false);
    }
}
