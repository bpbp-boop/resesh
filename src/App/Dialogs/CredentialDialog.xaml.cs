using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

/// <summary>Asks for a password or key passphrase while connecting, with the option to save it.</summary>
public sealed partial class CredentialDialog : ContentDialog
{
    public string Prompt { get; }

    public string Secret => SecretBox.Password;
    public bool Save => SaveBox.IsChecked == true;

    public CredentialDialog(string title, string prompt)
    {
        Prompt = prompt;
        InitializeComponent();
        Title = title;
        Opened += (_, _) => SecretBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }
}
