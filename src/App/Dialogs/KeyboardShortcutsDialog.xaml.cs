using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

/// <summary>The shortcut reference as a dialog, sized to the window it opens over.</summary>
public sealed partial class KeyboardShortcutsDialog : ContentDialog
{
    private const double PreferredWidth = 640;
    private const double PreferredHeight = 600;
    private const double HorizontalChrome = 72;
    private const double VerticalChrome = 180;

    private KeyboardShortcutsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Reference.FocusSearch();
    }

    public static async Task OpenAsync(XamlRoot xamlRoot)
    {
        var dialog = new KeyboardShortcutsDialog { XamlRoot = xamlRoot };
        void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => dialog.FitToWindow();
        dialog.FitToWindow();
        xamlRoot.Changed += XamlRootChanged;
        try
        {
            await dialog.ShowModalAsync();
        }
        finally
        {
            xamlRoot.Changed -= XamlRootChanged;
        }
    }

    private void FitToWindow()
    {
        var size = XamlRoot.Size;
        var width = Math.Min(PreferredWidth, Math.Max(240, size.Width - HorizontalChrome));
        var height = Math.Min(PreferredHeight, Math.Max(180, size.Height - VerticalChrome));
        Reference.Width = width;
        Reference.Height = height;
        Resources["ContentDialogMaxWidth"] = width + 48;
        Resources["ContentDialogMaxHeight"] = Math.Max(280, size.Height - 24);
    }
}
