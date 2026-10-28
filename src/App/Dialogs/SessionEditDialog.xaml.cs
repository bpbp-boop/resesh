using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

public sealed partial class SessionEditDialog : ContentDialog
{
    // DisplayMemberPath reads properties by name; this generates that lookup for AOT.
    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial class KeyChoice
    {
        public Guid Id { get; init; }
        public string Label { get; init; } = "";
    }

    // DisplayMemberPath reads properties by name; this generates that lookup for AOT.
    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial class LoginChoice
    {
        public SessionLogin Login { get; init; }
        public Identity? Identity { get; init; }
        public string Label { get; init; } = "";
    }

    internal static readonly (string Name, string? Hex)[] ColorChoices =
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
        SessionSettingsTarget initialTarget = SessionSettingsTarget.General,
        SessionKind newKind = SessionKind.Ssh)
    {
        InitializeComponent();
        DialogTheme.Apply(this);
        OverrideThemeBox.ItemsSource = new[] { new ThemeChoice("", "Use app setting") }
            .Concat(ThemeCatalog.All).ToList();
        _existing = existing;
        _keyStore = keyStore;
        TitleText.Text = existing is null ? "New session" : "Edit session";
        AutomationProperties.SetName(this, TitleText.Text); // the title is an element, not a string
        PasswordHint.Text = existing is null
            ? "Stored in Windows Credential Manager"
            : "Stored in Windows Credential Manager — leave blank to keep the current one";

        if (notice is not null)
        {
            ConnectedNote.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(ConnectedNote, notice);
            AutomationProperties.SetName(ConnectedNote, notice);
        }

        FolderBox.ItemsSource = folderPaths.ToList();
        FolderBox.Text = FolderPaths.Normalize(existing?.FolderPath ?? defaultFolder);

        foreach (var (name, _) in ColorChoices)
            ColorBox.Items.Add(name);
        ColorBox.SelectedIndex = Math.Max(0, Array.FindIndex(ColorChoices, c => c.Hex == existing?.ColorTag));

        PopulateIconPicker(existing?.Icon);
        PopulateKeyChoices(existing?.PrivateKeyId);
        // New sessions inherit the folder's login; saved ones keep whatever they had.
        PopulateLoginChoices(existing is null ? SessionLogin.Inherit : existing.Login, existing?.IdentityId);
        FolderBox.TextSubmitted += (_, _) => UpdateLoginFields();
        FolderBox.SelectionChanged += (_, _) => UpdateLoginFields();
        FolderBox.LostFocus += (_, _) => UpdateLoginFields();

        _updatingProtocol = true;
        ProtocolBox.SelectedIndex = (existing?.Kind ?? newKind) == SessionKind.Telnet ? 1 : 0;
        PortBox.Value = IsTelnet ? TelnetDefaultPort : SshDefaultPort;
        _updatingProtocol = false;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Value = existing.Port;
            UsernameBox.Text = existing.Username;
            AuthBox.SelectedIndex = (int)existing.AuthMethod;
            TelnetEncodingBox.SelectedIndex = existing.TelnetEncoding switch
            {
                TelnetTextEncoding.Utf8 => 1,
                TelnetTextEncoding.Cp437 => 2,
                _ => 0,
            };
            TelnetReport80x25Toggle.IsOn = existing.TelnetReport80x25;
            TelnetFixed80ColumnsToggle.IsOn = existing.TelnetFixed80Columns;
            var terminalIndex = TerminalTypeBox.Items.IndexOf(existing.TerminalType);
            if (terminalIndex >= 0)
                TerminalTypeBox.SelectedIndex = terminalIndex;
            else
                // Editable ComboBox resets Text set before it loads; apply it after.
                TerminalTypeBox.Loaded += (_, _) => TerminalTypeBox.Text = existing.TerminalType;
            PersistentToggle.IsOn = existing.Persistent;
            PersistenceBackendBox.SelectedIndex = existing.SessionAgent ? 1 : 0;
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
                OverrideHistoryBox.SelectedIndex = overrides.KeepCommandHistory switch
                {
                    true => 1,
                    false => 2,
                    null => 0,
                };
            }
        }

        UpdateLoginFields();
        PopulateNetwork(existing);
        UpdateProtocolVisibility();
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
        if (PersistenceBackendBox.SelectedIndex < 0)
            PersistenceBackendBox.SelectedIndex = 0;
        DetachedSessionsBox.IsEnabled = PersistenceBackendBox.IsEnabled = PersistentToggle.IsOn;
        PersistentToggle.Toggled += (_, _) =>
        {
            ClearFieldError(PersistentToggle, PersistentError);
            DetachedSessionsBox.IsEnabled = PersistenceBackendBox.IsEnabled = PersistentToggle.IsOn;
        };
        if (initialTarget != SessionSettingsTarget.General)
            SectionBar.SelectedItem = AppearanceSection;
        DialogSizing.FitSections(this, SessionForm, SectionHost, preferredWidth: 600, preferredHeight: 480);
        Opened += (_, _) => DispatcherQueue.TryEnqueue(() => InitialFocus(initialTarget)?.Focus(FocusState.Programmatic));
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

    private const int SshDefaultPort = 22;
    private const int TelnetDefaultPort = 23;
    private bool _updatingProtocol;

    private bool IsTelnet => ProtocolBox.SelectedIndex == 1;

    private AuthMethod SelectedAuth => IsTelnet ? AuthMethod.None : (AuthMethod)Math.Max(0, AuthBox.SelectedIndex);

    private Guid? SelectedKeyId => (KeyBox.SelectedItem as KeyChoice)?.Id;

    private LoginChoice SelectedLogin => LoginBox.SelectedItem as LoginChoice
        ?? new LoginChoice { Login = SessionLogin.Inherit };

    private void PopulateLoginChoices(SessionLogin login, Guid? identityId)
    {
        var choices = new List<LoginChoice> { new() { Login = SessionLogin.Inherit, Label = "Inherit from folder" } };
        choices.AddRange(App.Identities.Identities.Select(identity => new LoginChoice
        {
            Login = SessionLogin.Identity,
            Identity = identity,
            Label = $"{identity.Name} ({identity.Username}, {ProofText(identity)})",
        }));
        choices.Add(new LoginChoice { Login = SessionLogin.Own, Label = "This session only" });
        LoginBox.ItemsSource = choices;
        LoginBox.SelectedItem = choices.FirstOrDefault(c => c.Login == login
                && (login != SessionLogin.Identity || c.Identity?.Id == identityId))
            ?? choices[0];
    }

    internal static string ProofText(Identity identity) => identity.Proof switch
    {
        IdentityProof.Key => App.SshKeys.Find(identity.KeyId ?? Guid.Empty)?.Name is { } keyName ? $"key {keyName}" : "key",
        IdentityProof.Ask => "asks for the password",
        _ => "saved password",
    };

    private void LoginBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLoginFields();

    /// <summary>Own logins show the username and credential fields; identity logins show
    /// where the login comes from and an optional username override.</summary>
    private void UpdateLoginFields()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (LoginDescription is null || UsernameBox is null || AuthBox is null || FolderBox is null)
            return;
        var choice = SelectedLogin;
        var own = choice.Login == SessionLogin.Own;
        AuthBox.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        UsernameBox.Header = own ? "Username" : "Username override (optional)";

        Identity? identity = choice.Identity;
        if (choice.Login == SessionLogin.Inherit)
        {
            var folder = FolderPaths.Normalize(FolderBox.Text);
            if (App.Logins.InheritedAt(folder) is { } found)
            {
                var (inherited, from) = found;
                identity = inherited;
                LoginDescription.Text = $"Uses {inherited.Name} ({inherited.Username}, {ProofText(inherited)}) from "
                    + (from.Length == 0 ? "the top level." : $"{from}.");
            }
            else
            {
                LoginDescription.Text = "No folder login yet. You'll be asked when connecting, and can remember "
                    + "the answer for every session in the folder.";
            }
        }
        else if (choice.Login == SessionLogin.Identity)
            LoginDescription.Text = "This session always uses this identity, whatever folder it is in.";
        else
            LoginDescription.Text = "Stored with this session. Choose an identity to share one login across sessions.";

        UsernameBox.PlaceholderText = own ? "admin" : identity?.Username ?? "Asked when connecting";
        UpdateAuthFieldVisibility();
    }

    private Control? InitialFocus(SessionSettingsTarget target) => target switch
    {
        SessionSettingsTarget.Theme => OverrideThemeBox,
        SessionSettingsTarget.FontFamily => OverrideFontFamilyBox,
        SessionSettingsTarget.FontSize => OverrideFontSizeBox,
        SessionSettingsTarget.Scrollback => OverrideScrollbackBox,
        SessionSettingsTarget.AlwaysRecord => OverrideRecordingBox,
        SessionSettingsTarget.CommandHistory => OverrideHistoryBox,
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

    // ---- network: jump host, proxy, tunnels ----

    // DisplayMemberPath reads properties by name; this generates that lookup for AOT.
    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial class JumpChoice
    {
        public Guid Id { get; init; }
        public string Label { get; init; } = "";
    }

    private readonly List<Tunnel> _tunnels = [];

    /// <summary>New proxy password to store, or null to leave the stored one untouched.</summary>
    public string? ProxyPassword { get; private set; }

    private int ViaIndex => IsTelnet && ViaBox.SelectedIndex == 2 ? 0 : Math.Max(0, ViaBox.SelectedIndex);

    private ProxyKind SelectedProxyKind => ProxyKindBox.SelectedIndex switch
    {
        1 => ProxyKind.Socks4,
        2 => ProxyKind.Http,
        _ => ProxyKind.Socks5,
    };

    private void PopulateNetwork(Session? existing)
    {
        // Any SSH session can be a jump host, except this one and those reached through it.
        var choices = App.Store.Sessions
            .Where(candidate => candidate.Kind == SessionKind.Ssh
                && (existing is null || !Resesh.Core.Ssh.JumpRoute.WouldLoop(existing.Id, candidate.Id, App.Store.Find)))
            .OrderBy(candidate => candidate.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new JumpChoice
            {
                Id = candidate.Id,
                Label = (candidate.FolderPath.Length > 0 ? $"{candidate.FolderPath}/" : "") + $"{candidate.Name} ({candidate.Host})",
            })
            .ToList();
        if (existing?.JumpHostId is { } jumpId && choices.All(choice => choice.Id != jumpId))
        {
            // Keep a jump that was deleted, or now loops, visible so saving does not silently drop it.
            choices.Insert(0, new JumpChoice
            {
                Id = jumpId,
                Label = App.Store.Find(jumpId) is { } loop ? $"{loop.Name} (reached through this session)" : "(deleted session)",
            });
        }
        JumpBox.ItemsSource = choices;
        JumpBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == existing?.JumpHostId);

        if (existing?.Proxy is { } proxy)
        {
            ProxyKindBox.SelectedIndex = proxy.Kind switch
            {
                ProxyKind.Socks4 => 1,
                ProxyKind.Http => 2,
                _ => 0,
            };
            ProxyHostBox.Text = proxy.Host;
            ProxyPortBox.Value = proxy.Port;
            ProxyUsernameBox.Text = proxy.Username;
            ProxyPasswordHint.Text = "The proxy password is stored in Windows Credential Manager. Leave it blank to keep the current one.";
        }
        ViaBox.SelectedIndex = existing?.JumpHostId is not null ? 1 : existing?.Proxy is not null ? 2 : 0;

        _tunnels.AddRange(existing?.Tunnels ?? []);
        RenderTunnels();
        JumpBox.SelectionChanged += (_, _) => ClearFieldError(JumpBox, JumpError);
        ProxyHostBox.TextChanged += (_, _) => ClearFieldError(ProxyHostBox, ProxyHostError);
        UpdateNetworkFields();
        TunnelFields.TunnelAdded += AddTunnel;
    }

    private void ViaBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateNetworkFields();

    private void UpdateNetworkFields()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (JumpPanel is null || ProxyPanel is null || ViaDescription is null || TunnelsPanel is null || ViaProxyItem is null)
            return;
        // Telnet can ride a jump host's SSH connection; it has no proxy support or tunnels.
        ViaProxyItem.IsEnabled = !IsTelnet;
        if (IsTelnet && ViaBox.SelectedIndex == 2)
            ViaBox.SelectedIndex = 0;
        TunnelsPanel.Visibility = IsTelnet ? Visibility.Collapsed : Visibility.Visible;

        var via = ViaIndex;
        JumpPanel.Visibility = via == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProxyPanel.Visibility = via == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViaDescription.Text = via switch
        {
            1 when IsTelnet => "Connects to the jump host over SSH, then to this host from there. Telnet is "
                + "still unencrypted between the jump host and this host.",
            1 => "Connects to the jump host first, with its own login and host key, then to this host from "
                + "there. The jump host can have a jump host of its own.",
            2 => "Connects to the proxy, which connects on to this host.",
            _ => "Connects straight from this computer.",
        };
    }

    private void ProxyKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyPortBox is null)
            return;
        // Follow the type's usual port unless the user typed a custom one.
        var port = double.IsNaN(ProxyPortBox.Value) ? 0 : (int)ProxyPortBox.Value;
        if (port is 0 or 1080 or 8080)
            ProxyPortBox.Value = ProxySettings.DefaultPort(SelectedProxyKind);
    }

    private void AddTunnel(Tunnel tunnel)
    {
        if (TunnelForm.Conflict(tunnel, _tunnels) is { } conflict)
        {
            TunnelFields.ShowError(conflict);
            return;
        }
        _tunnels.Add(tunnel);
        TunnelFields.Reset();
        RenderTunnels();
    }

    private void RenderTunnels()
    {
        TunnelList.Children.Clear();
        foreach (var tunnel in _tunnels)
            TunnelList.Children.Add(TunnelForm.Row(tunnel.Describe(), "Remove", () =>
            {
                _tunnels.Remove(tunnel);
                RenderTunnels();
            }));
    }

    /// <summary>Stores or clears the proxy password for the saved session. Called by the
    /// owner after it saves <see cref="Result"/>.</summary>
    internal void SaveProxyPassword()
    {
        if (Result is not { } saved)
            return;
        if (saved.Proxy is not { Username.Length: > 0 })
            App.Credentials.DeleteProxy(saved.Id);
        else if (ProxyPassword is not null)
            App.Credentials.WriteProxy(saved.Id, ProxyPassword);
    }

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Fires during InitializeComponent for the initially-selected item, before the panels exist.
        if (ConnectionPanel is null || NetworkPanel is null || TerminalPanel is null || AppearancePanel is null || NotesBox is null)
            return;

        var selected = sender.SelectedItem;
        NetworkPanel.Visibility = selected == NetworkSection ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPanel.Visibility = selected == ConnectionSection ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = selected == TerminalSection ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = selected == AppearanceSection ? Visibility.Visible : Visibility.Collapsed;
        NotesBox.Visibility = selected == NotesSection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAuthFieldVisibility();

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before PortBox exists.
        if (PortBox is null || _updatingProtocol)
            return;
        // Follow the protocol's well-known port unless the user typed a custom one.
        var port = double.IsNaN(PortBox.Value) ? 0 : (int)PortBox.Value;
        if (port is 0 or SshDefaultPort or TelnetDefaultPort)
            PortBox.Value = IsTelnet ? TelnetDefaultPort : SshDefaultPort;
        UpdateProtocolVisibility();
    }

    /// <summary>Telnet has no username, authentication, keys, shell integration or tmux:
    /// the server's own login prompt arrives in the terminal.</summary>
    private void UpdateProtocolVisibility()
    {
        if (SshOptionsPanel is null || PersistentPanel is null || TelnetNotice is null)
            return;
        var telnet = IsTelnet;
        SshOptionsPanel.Visibility = telnet ? Visibility.Collapsed : Visibility.Visible;
        PersistentPanel.Visibility = telnet ? Visibility.Collapsed : Visibility.Visible;
        TelnetEncodingBox.Visibility = telnet ? Visibility.Visible : Visibility.Collapsed;
        TelnetSizePanel.Visibility = telnet ? Visibility.Visible : Visibility.Collapsed;
        TelnetNotice.IsOpen = telnet;
        // A closed InfoBar still takes a slot in the StackPanel, and with it a row of spacing.
        TelnetNotice.Visibility = telnet ? Visibility.Visible : Visibility.Collapsed;
        UpdateNetworkFields();
    }

    private void UpdateAuthFieldVisibility()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (PasswordPanel is null || KeyPathPanel is null || KeyHint is null)
            return;

        var auth = SelectedLogin.Login == SessionLogin.Own ? SelectedAuth : AuthMethod.None;
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
        var telnet = IsTelnet;
        var login = telnet ? new LoginChoice { Login = SessionLogin.Own } : SelectedLogin;
        var ownLogin = login.Login == SessionLogin.Own;
        var auth = ownLogin ? SelectedAuth : AuthMethod.Password;
        Validate(KeyBox, KeyError, ownLogin && auth == AuthMethod.PrivateKey && SelectedKeyId is null, "Select an SSH key.");

        var requiredInvalid = firstInvalid;
        var via = ViaIndex;
        Validate(JumpBox, JumpError, via == 1 && JumpBox.SelectedItem is null, "Select the session to connect through.");
        Validate(ProxyHostBox, ProxyHostError, via == 2 && string.IsNullOrWhiteSpace(ProxyHostBox.Text), "Enter the proxy's host name or IP address.");
        var proxy = via == 2
            ? new ProxySettings
            {
                Kind = SelectedProxyKind,
                Host = ProxyHostBox.Text.Trim(),
                Port = double.IsNaN(ProxyPortBox.Value)
                    ? ProxySettings.DefaultPort(SelectedProxyKind)
                    : Math.Clamp((int)ProxyPortBox.Value, 1, 65535),
                Username = ProxyUsernameBox.Text.Trim(),
            }
            : null;

        if (firstInvalid is not null)
        {
            // Required fields live on the Connection section; the route on Network.
            SectionBar.SelectedItem = requiredInvalid is null ? NetworkSection : ConnectionSection;
            firstInvalid.Focus(FocusState.Programmatic);
            firstInvalid.StartBringIntoView();
            args.Cancel = true;
            return;
        }

        var port = double.IsNaN(PortBox.Value) ? (telnet ? TelnetDefaultPort : SshDefaultPort) : (int)PortBox.Value;
        if (!telnet && PersistentToggle.IsOn && ShellIntegrationBox.SelectedIndex == 4)
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
            KeepCommandHistory = OverrideHistoryBox.SelectedIndex switch
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
            Kind = telnet ? SessionKind.Telnet : SessionKind.Ssh,
            Name = NameBox.Text.Trim(),
            FolderPath = FolderPaths.Normalize(FolderBox.Text),
            Host = HostBox.Text.Trim(),
            Port = Math.Clamp(port, 1, 65535),
            Username = telnet ? "" : UsernameBox.Text.Trim(),
            InheritLogin = login.Login == SessionLogin.Inherit,
            IdentityId = login.Login == SessionLogin.Identity ? login.Identity?.Id : null,
            AuthMethod = telnet ? AuthMethod.None : auth,
            PrivateKeyId = ownLogin && auth == AuthMethod.PrivateKey ? SelectedKeyId : null,
            PrivateKeyPath = null,
            PassphraseRequired = false,
            TerminalType = string.IsNullOrWhiteSpace(TerminalTypeBox.Text) ? "xterm-256color" : TerminalTypeBox.Text.Trim(),
            TelnetEncoding = TelnetEncodingBox.SelectedIndex switch
            {
                1 => TelnetTextEncoding.Utf8,
                2 => TelnetTextEncoding.Cp437,
                _ => TelnetTextEncoding.Auto,
            },
            TelnetReport80x25 = telnet && TelnetReport80x25Toggle.IsOn,
            TelnetFixed80Columns = telnet && TelnetFixed80ColumnsToggle.IsOn,
            Persistent = !telnet && PersistentToggle.IsOn,
            SessionAgent = !telnet && PersistentToggle.IsOn && PersistenceBackendBox.SelectedIndex == 1,
            DetachedSessions = DetachedSessionsBox.SelectedIndex switch
            {
                1 => DetachedSessionAction.StartNew,
                2 => DetachedSessionAction.EndDetachedAndStartNew,
                _ => DetachedSessionAction.Ask,
            },
            ShellIntegration = telnet ? ShellIntegrationMode.Disabled : ShellIntegrationBox.SelectedIndex switch
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
            Agent = _existing?.Agent,
            CredentialNeeded = _existing?.CredentialNeeded ?? false,
            JumpHostId = via == 1 ? (JumpBox.SelectedItem as JumpChoice)?.Id : null,
            Proxy = proxy,
            Tunnels = telnet || _tunnels.Count == 0 ? null : _tunnels.ToList(),
            Overrides = overrides.IsEmpty ? null : overrides,
        };
        ProxyPassword = proxy is not null && ProxyPasswordBox.Password.Length > 0 ? ProxyPasswordBox.Password : null;
        Password = ownLogin && auth == AuthMethod.Password && PasswordBox.Password.Length > 0
            ? PasswordBox.Password
            : null;
    }
}
