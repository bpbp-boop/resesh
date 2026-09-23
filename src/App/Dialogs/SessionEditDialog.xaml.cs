using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

public enum SessionSettingsTarget
{
    General,
    Theme,
    FontFamily,
    FontSize,
    Scrollback,
    AlwaysRecord,
}

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
        SshKeyStore keyStore, string? notice = null,
        SessionSettingsTarget initialTarget = SessionSettingsTarget.General)
    {
        InitializeComponent();
        DialogTheme.Apply(this);
        OverrideThemeBox.ItemsSource = new[] { new ThemeChoice("", "Use app setting") }
            .Concat(ThemeCatalog.All).ToList();
        _existing = existing;
        _keyStore = keyStore;
        Title = existing is null ? "New SSH session" : "Edit SSH session";
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
            DetachedSessionsBox.SelectedIndex = (int)existing.DetachedSessions;
            ShellIntegrationBox.SelectedIndex = existing.ShellIntegration switch
            {
                ShellIntegrationMode.Bash => 1,
                ShellIntegrationMode.Zsh => 2,
                ShellIntegrationMode.Fish => 3,
                ShellIntegrationMode.PowerShell => 4,
                _ => 0,
            };
            NotesBox.Text = existing.Notes;

            if (existing.Overrides is { } overrides)
            {
                OverrideThemeBox.SelectedItem = ThemeCatalog.All.FirstOrDefault(theme => theme.Id == overrides.Theme)
                    ?? OverrideThemeBox.Items[0];
                OverrideFontFamilyBox.Text = overrides.FontFamily ?? "";
                if (overrides.FontSize is { } fontSize)
                    OverrideFontSizeBox.Value = fontSize;
                if (overrides.Scrollback is { } scrollback)
                    OverrideScrollbackBox.Value = scrollback;
                OverrideRecordingBox.SelectedIndex = overrides.AlwaysRecord switch
                {
                    true => 1,
                    false => 2,
                    null => 0,
                };
            }
        }

        UpdateAuthFieldVisibility();
        UpdateAdvancedHeader();
        ShellIntegrationBox.SelectionChanged += (_, _) =>
        {
            UpdateAdvancedHeader();
            ClearFieldError(PersistentToggle, PersistentError);
        };
        NameBox.TextChanged += (_, _) => ClearFieldError(NameBox, NameError);
        HostBox.TextChanged += (_, _) => ClearFieldError(HostBox, HostError);
        KeyBox.SelectionChanged += (_, _) => ClearFieldError(KeyBox, KeyError);
        AuthBox.SelectionChanged += (_, _) => ClearFieldError(KeyBox, KeyError);
        if (DetachedSessionsBox.SelectedIndex < 0)
            DetachedSessionsBox.SelectedIndex = 0;
        DetachedSessionsBox.IsEnabled = PersistentToggle.IsOn;
        PersistentToggle.Toggled += (_, _) =>
        {
            ClearFieldError(PersistentToggle, PersistentError);
            DetachedSessionsBox.IsEnabled = PersistentToggle.IsOn;
        };
        if (initialTarget != SessionSettingsTarget.General)
            SectionBar.SelectedItem = TerminalSection;
        Opened += (_, _) =>
        {
            UpdateDialogLayout();
            XamlRoot.Changed += OnRootChanged;
            DispatcherQueue.TryEnqueue(() => InitialFocus(initialTarget)?.Focus(FocusState.Programmatic));
        };
        Closed += (_, _) => XamlRoot.Changed -= OnRootChanged;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDialogLayout();

    private void UpdateDialogLayout()
    {
        SessionForm.Width = Math.Min(600, Math.Max(240, XamlRoot.Size.Width - 96));
        SectionHost.Height = Math.Min(480, Math.Max(160, XamlRoot.Size.Height - 250));
    }

    private void UpdateAdvancedHeader()
    {
        var header = $"Advanced · Shell integration: {(ShellIntegrationBox.SelectedItem as ComboBoxItem)?.Content ?? "Off"}";
        AdvancedSection.Header = header;
        AutomationProperties.SetName(AdvancedSection, header);
    }

    private static void ClearFieldError(Control field, TextBlock error)
    {
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(field, "");
    }

    private static void SetFieldError(Control field, TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        AutomationProperties.SetItemStatus(field, message);
    }

    private AuthMethod SelectedAuth => (AuthMethod)Math.Max(0, AuthBox.SelectedIndex);

    private Guid? SelectedKeyId => (KeyBox.SelectedItem as KeyChoice)?.Id;

    private Control? InitialFocus(SessionSettingsTarget target) => target switch
    {
        SessionSettingsTarget.Theme => OverrideThemeBox,
        SessionSettingsTarget.FontFamily => OverrideFontFamilyBox,
        SessionSettingsTarget.FontSize => OverrideFontSizeBox,
        SessionSettingsTarget.Scrollback => OverrideScrollbackBox,
        SessionSettingsTarget.AlwaysRecord => OverrideRecordingBox,
        _ => NameBox,
    };

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
        Control? firstInvalid = null;
        void Validate(Control field, TextBlock error, bool invalid, string message)
        {
            ClearFieldError(field, error);
            if (!invalid)
                return;
            SetFieldError(field, error, message);
            firstInvalid ??= field;
        }
        Validate(NameBox, NameError, string.IsNullOrWhiteSpace(NameBox.Text), "Enter a session name.");
        Validate(HostBox, HostError, string.IsNullOrWhiteSpace(HostBox.Text), "Enter a host name or IP address.");
        Validate(KeyBox, KeyError, SelectedAuth == AuthMethod.PrivateKey && SelectedKeyId is null, "Select an SSH key.");

        if (firstInvalid is not null)
        {
            // Every required field lives on the Connection section.
            SectionBar.SelectedItem = ConnectionSection;
            firstInvalid.Focus(FocusState.Programmatic);
            firstInvalid.StartBringIntoView();
            args.Cancel = true;
            return;
        }

        var port = double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value;
        if (PersistentToggle.IsOn && ShellIntegrationBox.SelectedIndex == 4)
        {
            SetFieldError(PersistentToggle, PersistentError,
                "Turn off Persistent session to use PowerShell shell integration.");
            SectionBar.SelectedItem = TerminalSection;
            PersistentToggle.Focus(FocusState.Programmatic);
            PersistentToggle.StartBringIntoView();
            args.Cancel = true;
            return;
        }
        // An all-null overrides object is stored as null so sessions.json stays clean.
        var overrides = new TerminalOverrides
        {
            Theme = (OverrideThemeBox.SelectedItem as ThemeChoice)?.Id is { Length: > 0 } theme ? theme : null,
            FontFamily = string.IsNullOrWhiteSpace(OverrideFontFamilyBox.Text) ? null : OverrideFontFamilyBox.Text.Trim(),
            FontSize = double.IsNaN(OverrideFontSizeBox.Value) ? null : (int)OverrideFontSizeBox.Value,
            Scrollback = double.IsNaN(OverrideScrollbackBox.Value) ? null : (int)OverrideScrollbackBox.Value,
            AlwaysRecord = OverrideRecordingBox.SelectedIndex switch
            {
                1 => true,
                2 => false,
                _ => null,
            },
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
            DetachedSessions = DetachedSessionsBox.SelectedIndex switch
            {
                1 => DetachedSessionAction.StartNew,
                2 => DetachedSessionAction.EndDetachedAndStartNew,
                _ => DetachedSessionAction.Ask,
            },
            ShellIntegration = ShellIntegrationBox.SelectedIndex switch
            {
                1 => ShellIntegrationMode.Bash,
                2 => ShellIntegrationMode.Zsh,
                3 => ShellIntegrationMode.Fish,
                4 => ShellIntegrationMode.PowerShell,
                _ => ShellIntegrationMode.Disabled,
            },
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
