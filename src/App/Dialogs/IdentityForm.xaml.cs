using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Models;

namespace Resesh.App.Dialogs;

/// <summary>The fields of one identity: username, name, and how it signs in. Shared by the
/// identity editor and the folder identity dialog.</summary>
public sealed partial class IdentityForm : UserControl
{
    private sealed class KeyChoice
    {
        public Guid Id { get; init; }
        public string Label { get; init; } = "";
    }

    // Keeps the key picker's last-used folder separate from other pickers in the app.
    private static readonly Guid KeyPickerClientId = new("6d0c7f43-1f4e-4f7b-9a4f-2f5c3a8e91d2");

    private Identity? _existing;

    /// <summary>Raised when an error should be shown above the form (for example, an unreadable key file).</summary>
    public event Action<string>? ErrorRaised;

    public IdentityForm()
    {
        InitializeComponent();
        UsernameBox.TextChanged += (_, _) => UsernameError.Visibility = Visibility.Collapsed;
        KeyBox.SelectionChanged += (_, _) => KeyError.Visibility = Visibility.Collapsed;
        Load(null, hasStoredPassword: false);
    }

    /// <summary>Fills the form; <paramref name="hasStoredPassword"/> lets a blank password keep the saved one.</summary>
    public void Load(Identity? existing, bool hasStoredPassword)
    {
        _existing = existing;
        UsernameBox.Text = existing?.Username ?? "";
        NameBox.Text = existing?.Name ?? "";
        NameBox.PlaceholderText = "Defaults to the username";
        ProofBox.SelectedIndex = existing?.Proof switch
        {
            IdentityProof.Key => 1,
            IdentityProof.Ask => 2,
            _ => 0,
        };
        PasswordBox.Password = "";
        PasswordHint.Text = hasStoredPassword
            ? "Stored in Windows Credential Manager. Leave blank to keep the current password."
            : "Stored in Windows Credential Manager. Leave blank to be asked the first time you connect.";
        PopulateKeys(existing?.KeyId);
        UpdateProof();
    }

    public void FocusFirstField() => UsernameBox.Focus(FocusState.Programmatic);

    private IdentityProof Proof => ProofBox.SelectedIndex switch
    {
        1 => IdentityProof.Key,
        2 => IdentityProof.Ask,
        _ => IdentityProof.Password,
    };

    /// <summary>Validates and returns the identity plus a new password to store (null keeps
    /// the stored one), or null after showing what is missing.</summary>
    public (Identity Identity, string? Password)? TryBuild()
    {
        var username = UsernameBox.Text.Trim();
        var keyId = (KeyBox.SelectedItem as KeyChoice)?.Id;
        Control? firstInvalid = null;
        if (username.Length == 0)
        {
            UsernameError.Text = "Enter a username.";
            UsernameError.Visibility = Visibility.Visible;
            firstInvalid = UsernameBox;
        }
        if (Proof == IdentityProof.Key && keyId is null)
        {
            KeyError.Text = "Select an SSH key.";
            KeyError.Visibility = Visibility.Visible;
            firstInvalid ??= KeyBox;
        }
        if (firstInvalid is not null)
        {
            firstInvalid.Focus(FocusState.Programmatic);
            return null;
        }

        var identity = (_existing ?? new Identity()) with
        {
            Name = NameBox.Text.Trim(),
            Username = username,
            Proof = Proof,
            KeyId = Proof == IdentityProof.Key ? keyId : null,
        };
        var password = Proof == IdentityProof.Password && PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        return (identity, password);
    }

    private void PopulateKeys(Guid? selectedId)
    {
        KeyBox.Items.Clear();
        foreach (var key in App.SshKeys.Keys)
        {
            var detail = key.IsAvailable ? key.Algorithm ?? "SSH key" : "unavailable";
            var choice = new KeyChoice { Id = key.Id, Label = $"{key.Name} — {detail}" };
            KeyBox.Items.Add(choice);
            if (key.Id == selectedId)
                KeyBox.SelectedItem = choice;
        }
    }

    private void ProofBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProof();

    private void UpdateProof()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (PasswordPanel is null || KeyPanel is null || AskHint is null)
            return;
        var proof = Proof;
        PasswordPanel.Visibility = proof == IdentityProof.Password ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = proof == IdentityProof.Key ? Visibility.Visible : Visibility.Collapsed;
        AskHint.Visibility = proof == IdentityProof.Ask ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var path = Interop.Win32FileDialog.PickFile(hwnd, KeyPickerClientId, sshDir, "Select private key file");
        if (path is null)
            return;
        try
        {
            var key = App.SshKeys.RegisterExternal(path);
            PopulateKeys(key.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorRaised?.Invoke(ex.Message);
        }
    }
}
