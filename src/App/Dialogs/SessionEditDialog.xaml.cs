using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.Core.Models;
using Sessions.Core.Storage;

namespace Sessions.App.Dialogs;

public sealed partial class SessionEditDialog : ContentDialog
{
    private static readonly (string Name, string? Hex)[] ColorChoices =
    [
        ("None", null),
        ("Red", "#E74856"),
        ("Orange", "#FF8C00"),
        ("Yellow", "#FFB900"),
        ("Green", "#10893E"),
        ("Blue", "#0078D7"),
        ("Purple", "#886CE4"),
    ];

    private readonly Session? _existing;

    /// <summary>The saved session, or null if the dialog was cancelled.</summary>
    public Session? Result { get; private set; }

    /// <summary>New password/passphrase to store, or null to leave the stored credential untouched.</summary>
    public string? Password { get; private set; }

    public SessionEditDialog(IEnumerable<string> folderPaths, Session? existing, string defaultFolder, string? notice = null)
    {
        InitializeComponent();
        _existing = existing;
        Title = existing is null ? "New Session" : "Edit Session";
        PasswordHint.Text = existing is null
            ? "Stored in Windows Credential Manager"
            : "Stored in Windows Credential Manager — leave blank to keep the current one";

        if (notice is not null)
        {
            NoticeBar.Message = notice;
            NoticeBar.IsOpen = true;
        }

        FolderBox.ItemsSource = folderPaths.ToList();
        FolderBox.Text = FolderPaths.Normalize(existing?.FolderPath ?? defaultFolder);

        foreach (var (name, _) in ColorChoices)
            ColorBox.Items.Add(name);
        ColorBox.SelectedIndex = Math.Max(0, Array.FindIndex(ColorChoices, c => c.Hex == existing?.ColorTag));

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Value = existing.Port;
            UsernameBox.Text = existing.Username;
            AuthBox.SelectedIndex = (int)existing.AuthMethod;
            KeyPathBox.Text = existing.PrivateKeyPath ?? "";
            PassphraseCheck.IsChecked = existing.PassphraseRequired;

            var terminalIndex = TerminalTypeBox.Items.IndexOf(existing.TerminalType);
            if (terminalIndex >= 0)
                TerminalTypeBox.SelectedIndex = terminalIndex;
            else
                // Editable ComboBox resets Text set before it loads; apply it after.
                TerminalTypeBox.Loaded += (_, _) => TerminalTypeBox.Text = existing.TerminalType;
            PersistentToggle.IsOn = existing.Persistent;
            NotesBox.Text = existing.Notes;
        }

        UpdateAuthFieldVisibility();
    }

    private AuthMethod SelectedAuth => (AuthMethod)Math.Max(0, AuthBox.SelectedIndex);

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAuthFieldVisibility();

    private void UpdateAuthFieldVisibility()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (PasswordPanel is null || KeyPathPanel is null || PassphraseCheck is null)
            return;

        var auth = SelectedAuth;
        PasswordPanel.Visibility = auth == AuthMethod.None ? Visibility.Collapsed : Visibility.Visible;
        PasswordBox.Header = auth == AuthMethod.PrivateKey ? "Key passphrase" : "Password";
        KeyPathPanel.Visibility = auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PassphraseCheck.Visibility = auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keeps the key picker's last-used folder separate from other pickers in the app.
    private static readonly Guid KeyPickerClientId = new("b3f9c1e4-8f6a-4d2b-9c0e-5a7d31e8b246");

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var path = Interop.Win32FileDialog.PickFile(hwnd, KeyPickerClientId, sshDir, "Select private key file");
        if (path is not null)
            KeyPathBox.Text = path;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(HostBox.Text))
            errors.Add("Host is required.");
        if (SelectedAuth == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(KeyPathBox.Text))
            errors.Add("Private key file is required for key authentication.");

        if (errors.Count > 0)
        {
            ValidationText.Text = string.Join(" ", errors);
            ValidationText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        var port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value;
        Result = new Session
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = NameBox.Text.Trim(),
            FolderPath = FolderPaths.Normalize(FolderBox.Text),
            Host = HostBox.Text.Trim(),
            Port = Math.Clamp(port, 1, 65535),
            Username = UsernameBox.Text.Trim(),
            AuthMethod = SelectedAuth,
            PrivateKeyPath = SelectedAuth == AuthMethod.PrivateKey ? KeyPathBox.Text.Trim() : null,
            PassphraseRequired = SelectedAuth == AuthMethod.PrivateKey && PassphraseCheck.IsChecked == true,
            TerminalType = string.IsNullOrWhiteSpace(TerminalTypeBox.Text) ? "xterm-256color" : TerminalTypeBox.Text.Trim(),
            Persistent = PersistentToggle.IsOn,
            Notes = NotesBox.Text,
            ColorTag = ColorChoices[Math.Max(0, ColorBox.SelectedIndex)].Hex,
            CredentialNeeded = _existing?.CredentialNeeded ?? false,
        };
        Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
    }
}
