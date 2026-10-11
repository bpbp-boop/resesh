using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

/// <summary>Asks for a password or key passphrase while connecting, with the option to save it.</summary>
public sealed partial class CredentialDialog : ContentDialog
{
    public string Prompt { get; }

    public string Secret => SecretBox.Password;
    public bool Save => SaveBox.IsChecked == true;

    /// <param name="saveLabel">The save checkbox text; null hides it (nothing is saved).</param>
    public CredentialDialog(string title, string prompt,
        string? saveLabel = "Save in Windows Credential Manager", string primaryText = "Connect")
    {
        Prompt = prompt;
        InitializeComponent();
        DialogTheme.Apply(this);
        Title = title;
        PrimaryButtonText = primaryText;
        if (saveLabel is null)
        {
            SaveBox.IsChecked = false;
            SaveBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        else
            SaveBox.Content = saveLabel;
        Opened += (_, _) => SecretBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }
}
