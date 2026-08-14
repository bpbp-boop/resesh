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
            TerminalTypeBox.Text = existing.TerminalType;
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
        if (PasswordBox is null || KeyPathBox is null || PassphraseCheck is null)
            return;

        var auth = SelectedAuth;
        PasswordBox.Visibility = auth == AuthMethod.None ? Visibility.Collapsed : Visibility.Visible;
        PasswordBox.Header = auth == AuthMethod.PrivateKey ? "Key passphrase" : "Password";
        KeyPathBox.Visibility = auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PassphraseCheck.Visibility = auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
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
            Notes = NotesBox.Text,
            ColorTag = ColorChoices[Math.Max(0, ColorBox.SelectedIndex)].Hex,
            CredentialNeeded = _existing?.CredentialNeeded ?? false,
        };
        Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
    }
}
