using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

internal static class DialogSizing
{
    /// <summary>
    /// Stock ContentDialog caps its width at 548, which leaves slightly less than 500 for
    /// content once padding and the scroll gutter are taken; a 500 minimum then clips on the
    /// right. Raise the cap instead, and shrink the minimum on small windows.
    /// </summary>
    public static void FitContent(ContentDialog dialog, FrameworkElement content, XamlRoot root)
    {
        const double contentWidth = 500;
        var available = Math.Max(280, root.Size.Width - 24);
        dialog.Resources["ContentDialogMaxWidth"] = Math.Min(contentWidth + 100, available);
        content.MinWidth = Math.Min(contentWidth, available - 100);
    }
}
