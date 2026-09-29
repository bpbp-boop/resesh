using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

/// <summary>A titled message with one button: results, notices, and errors.</summary>
public sealed partial class MessageDialog : ContentDialog
{
    public string Message { get; }

    public MessageDialog(string title, string message, string closeText = "OK")
    {
        Message = message;
        InitializeComponent();
        Title = title;
        CloseButtonText = closeText;
    }

    public static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message, string closeText = "OK") =>
        await new MessageDialog(title, message, closeText) { XamlRoot = xamlRoot }.ShowModalAsync();
}
