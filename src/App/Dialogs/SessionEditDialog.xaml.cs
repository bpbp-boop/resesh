using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.Core.Models;
using Sessions.Core.Storage;

namespace Sessions.App.Dialogs;

public sealed partial class SessionEditDialog : ContentDialog
{
    private sealed class KeyChoice
    {
        public Guid Id { get; init; }
        public string Label { get; init; } = "";
    }

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
    private readonly SshKeyStore _keyStore;

    /// <summary>The saved session, or null if the dialog was cancelled.</summary>
    public Session? Result { get; private set; }

    /// <summary>New password to store, or null to leave the stored credential untouched.</summary>
    public string? Password { get; private set; }

    public SessionEditDialog(IEnumerable<string> folderPaths, Session? existing, string defaultFolder,
        SshKeyStore keyStore, string? notice = null)
    {
        InitializeComponent();
        _existing = existing;
        _keyStore = keyStore;
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

        PopulateIconPicker(existing?.Icon);
        PopulateKeyChoices(existing?.PrivateKeyId);

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Value = existing.Port;
            UsernameBox.Text = existing.Username;
            AuthBox.SelectedIndex = (int)existing.AuthMethod;
            var terminalIndex = TerminalTypeBox.Items.IndexOf(existing.TerminalType);
            if (terminalIndex >= 0)
                TerminalTypeBox.SelectedIndex = terminalIndex;
            else
                // Editable ComboBox resets Text set before it loads; apply it after.
                TerminalTypeBox.Loaded += (_, _) => TerminalTypeBox.Text = existing.TerminalType;
            PersistentToggle.IsOn = existing.Persistent;
            NotesBox.Text = existing.Notes;

            if (existing.Overrides is { } overrides)
            {
                OverrideThemeBox.SelectedIndex = overrides.Theme switch { "dark" => 1, "light" => 2, _ => 0 };
                OverrideFontFamilyBox.Text = overrides.FontFamily ?? "";
                if (overrides.FontSize is { } fontSize)
                    OverrideFontSizeBox.Value = fontSize;
                if (overrides.Scrollback is { } scrollback)
                    OverrideScrollbackBox.Value = scrollback;
            }
        }

        UpdateAuthFieldVisibility();
    }

    private AuthMethod SelectedAuth => (AuthMethod)Math.Max(0, AuthBox.SelectedIndex);

    private Guid? SelectedKeyId => (KeyBox.SelectedItem as KeyChoice)?.Id;

    private void PopulateKeyChoices(Guid? selectedId)
    {
        KeyBox.Items.Clear();
        foreach (var key in _keyStore.Keys)
        {
            var detail = key.IsAvailable ? key.Algorithm ?? "SSH key" : "unavailable";
            var choice = new KeyChoice { Id = key.Id, Label = $"{key.Name} — {detail}" };
            KeyBox.Items.Add(choice);
            if (key.Id == selectedId)
                KeyBox.SelectedItem = choice;
        }
    }

    // ---- icon picker ----

    /// <summary>Selected icon key: null = auto-detect, SessionIcons.None = explicitly none.</summary>
    private string? _selectedIcon;

    private void PopulateIconPicker(string? currentKey)
    {
        var entries = App.Icons.PickerEntries();
        // A key whose file has gone missing (deleted custom icon) still round-trips.
        if (!string.IsNullOrEmpty(currentKey)
            && !entries.Any(e => string.Equals(e.Key, currentKey, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new Icons.IconChoice(currentKey, $"{currentKey} (missing)", null, ""));
        }
        IconGrid.ItemsSource = entries;
        _selectedIcon = string.IsNullOrEmpty(currentKey) ? null : currentKey;
        UpdateIconButton(entries.First(e => string.Equals(e.Key, _selectedIcon, StringComparison.OrdinalIgnoreCase)));
    }

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        var choice = (Icons.IconChoice)e.ClickedItem;
        _selectedIcon = choice.Key;
        UpdateIconButton(choice);
        IconFlyout.Hide();
    }

    private void UpdateIconButton(Icons.IconChoice choice)
    {
        // The button shows the icon at 16, not the picker-tile 24 — fetch its own size.
        var image = App.Icons.GetImage(choice.Key, Icons.SessionIconCatalog.ListIconSize);
        IconButtonImage.Source = image;
        IconButtonImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        IconButtonText.Text = choice.Key is null ? "Auto-detect" : choice.Name;
    }

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Fires during InitializeComponent for the initially-selected item, before the panels exist.
        if (ConnectionPanel is null || TerminalPanel is null || NotesBox is null)
            return;

        var selected = sender.SelectedItem;
        ConnectionPanel.Visibility = selected == ConnectionSection ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = selected == TerminalSection ? Visibility.Visible : Visibility.Collapsed;
        NotesBox.Visibility = selected == NotesSection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAuthFieldVisibility();

    private void UpdateAuthFieldVisibility()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (PasswordPanel is null || KeyPathPanel is null || KeyHint is null)
            return;

        var auth = SelectedAuth;
        PasswordPanel.Visibility = auth == AuthMethod.Password ? Visibility.Visible : Visibility.Collapsed;
        KeyPathPanel.Visibility = auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        KeyHint.Visibility = auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keeps the key picker's last-used folder separate from other pickers in the app.
    private static readonly Guid KeyPickerClientId = new("b3f9c1e4-8f6a-4d2b-9c0e-5a7d31e8b246");

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var path = Interop.Win32FileDialog.PickFile(hwnd, KeyPickerClientId, sshDir, "Select private key file");
        if (path is null)
            return;
        try
        {
            var key = _keyStore.RegisterExternal(path);
            PopulateKeyChoices(key.Id);
            NoticeBar.IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            NoticeBar.Severity = InfoBarSeverity.Error;
            NoticeBar.Message = ex.Message;
            NoticeBar.IsOpen = true;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(HostBox.Text))
            errors.Add("Host is required.");
        if (SelectedAuth == AuthMethod.PrivateKey && SelectedKeyId is null)
            errors.Add("Select an SSH key for key authentication.");

        if (errors.Count > 0)
        {
            ValidationText.Text = string.Join(" ", errors);
            ValidationText.Visibility = Visibility.Visible;
            // Every required field lives on the Connection section.
            SectionBar.SelectedItem = ConnectionSection;
            args.Cancel = true;
            return;
        }

        var port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value;
        // An all-null overrides object is stored as null so sessions.json stays clean.
        var overrides = new TerminalOverrides
        {
            Theme = OverrideThemeBox.SelectedIndex switch { 1 => "dark", 2 => "light", _ => null },
            FontFamily = string.IsNullOrWhiteSpace(OverrideFontFamilyBox.Text) ? null : OverrideFontFamilyBox.Text.Trim(),
            FontSize = double.IsNaN(OverrideFontSizeBox.Value) ? null : (int)OverrideFontSizeBox.Value,
            Scrollback = double.IsNaN(OverrideScrollbackBox.Value) ? null : (int)OverrideScrollbackBox.Value,
            // Highlight deltas are edited from the tab's Highlighting menu, not here — carry them through.
            EnabledRules = _existing?.Overrides?.EnabledRules,
            DisabledRules = _existing?.Overrides?.DisabledRules,
        };
        Result = new Session
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = NameBox.Text.Trim(),
            FolderPath = FolderPaths.Normalize(FolderBox.Text),
            Host = HostBox.Text.Trim(),
            Port = Math.Clamp(port, 1, 65535),
            Username = UsernameBox.Text.Trim(),
            AuthMethod = SelectedAuth,
            PrivateKeyId = SelectedAuth == AuthMethod.PrivateKey ? SelectedKeyId : null,
            PrivateKeyPath = null,
            PassphraseRequired = false,
            TerminalType = string.IsNullOrWhiteSpace(TerminalTypeBox.Text) ? "xterm-256color" : TerminalTypeBox.Text.Trim(),
            Persistent = PersistentToggle.IsOn,
            Notes = NotesBox.Text,
            ColorTag = ColorChoices[Math.Max(0, ColorBox.SelectedIndex)].Hex,
            Icon = _selectedIcon,
            CredentialNeeded = _existing?.CredentialNeeded ?? false,
            Overrides = overrides.IsEmpty ? null : overrides,
        };
        Password = SelectedAuth == AuthMethod.Password && PasswordBox.Password.Length > 0
            ? PasswordBox.Password
            : null;
    }
}
