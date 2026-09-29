using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Host key confirmation: trust a key on first contact, or replace a changed one
/// after typing the host name.</summary>
public sealed partial class HostKeyDialog : ContentDialog
{
    public bool IsMismatch { get; }
    public bool IsFirstContact => !IsMismatch;
    public string Host { get; }

    public string FirstContactMessage { get; }
    public string KeyTypeLine { get; }
    public string OfferedFingerprint { get; }

    public string MismatchMessage { get; }
    public string PreviousKeyLine { get; }
    public string OfferedKeyLine { get; }
    public string ConfirmHeader { get; }

    public HostKeyDialog(HostKeyInfo info)
    {
        IsMismatch = info.Verdict == HostKeyVerdict.Mismatch;
        Host = info.Host;
        FirstContactMessage = $"First connection to {info.Host}:{info.Port}. Verify the host key fingerprint before trusting it.";
        KeyTypeLine = $"Key type: {info.KeyType}";
        OfferedFingerprint = $"SHA256:{info.Sha256Fingerprint}";
        MismatchMessage = $"The host key for {info.Host}:{info.Port} does not match the one previously trusted. " +
                          "This can mean the server was reinstalled or its key rotated — but it can also mean " +
                          "a man-in-the-middle attack. Only continue if you can explain the change.";
        PreviousKeyLine = info.Previous is { } previous ? $"{previous.KeyType} SHA256:{previous.Sha256}" : "(unavailable)";
        OfferedKeyLine = $"{info.KeyType} SHA256:{info.Sha256Fingerprint}";
        ConfirmHeader = $"Type the host name ({info.Host}) to confirm replacing the trusted key";

        InitializeComponent();
        if (IsMismatch)
        {
            Title = "⚠ Host Key Has Changed";
            PrimaryButtonText = "Replace Key and Connect";
            CloseButtonText = "Cancel";
            IsPrimaryButtonEnabled = false;
        }
        else
        {
            Title = "Verify Host Key";
            PrimaryButtonText = "Accept and Connect";
            CloseButtonText = "Reject";
        }
    }

    private void ConfirmBox_TextChanged(object sender, TextChangedEventArgs e) =>
        IsPrimaryButtonEnabled = string.Equals(ConfirmBox.Text.Trim(), Host, StringComparison.OrdinalIgnoreCase);
}
