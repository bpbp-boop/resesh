using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.Core.Ssh;

namespace Sessions.App.Dialogs;

/// <summary>Small code-built dialogs used during the connect workflow.</summary>
public static class ConnectDialogs
{
    /// <summary>Prompts for a password/passphrase. Returns (secret, save) or null on cancel.</summary>
    public static async Task<(string Secret, bool Save)?> PromptCredentialAsync(
        XamlRoot xamlRoot, string title, string prompt)
    {
        var passwordBox = new PasswordBox { Header = prompt };
        var saveCheck = new CheckBox { Content = "Save in Windows Credential Manager", IsChecked = true };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { passwordBox, saveCheck } },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? (passwordBox.Password, saveCheck.IsChecked == true)
            : null;
    }

    /// <summary>First-connect host key confirmation.</summary>
    public static async Task<bool> ConfirmHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info)
    {
        var dialog = new ContentDialog
        {
            Title = "Verify Host Key",
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"First connection to {info.Host}:{info.Port}. Verify the host key fingerprint before trusting it.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = $"Key type: {info.KeyType}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") },
                    new TextBlock
                    {
                        Text = $"SHA256:{info.Sha256Fingerprint}",
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            },
            PrimaryButtonText = "Accept and Connect",
            CloseButtonText = "Reject",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
