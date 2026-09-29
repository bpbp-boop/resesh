using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Warns when a registered key path now contains a different public key.</summary>
public sealed partial class ChangedPrivateKeyDialog : ContentDialog
{
    public string KeyName { get; }
    public string PreviousLine { get; }
    public string CurrentLine { get; }
    public string ConfirmHeader { get; }

    public ChangedPrivateKeyDialog(SshKeyChangedException change)
    {
        KeyName = change.KeyName;
        PreviousLine = $"Previous: {change.PreviousFingerprint}";
        CurrentLine = $"Current:  {change.CurrentFingerprint}";
        ConfirmHeader = $"Type the key name ({change.KeyName}) to accept the replacement";
        InitializeComponent();
    }

    private void ConfirmBox_TextChanged(object sender, TextChangedEventArgs e) =>
        IsPrimaryButtonEnabled = ConfirmBox.Text.Trim().Equals(KeyName, StringComparison.Ordinal);
}
