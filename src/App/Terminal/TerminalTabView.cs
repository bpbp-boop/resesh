using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.App.Controls;
using Sessions.App.Dialogs;
using Sessions.App.ViewModels;
using Sessions.Core.Credentials;
using Sessions.Core.Models;
using Sessions.Core.Sftp;
using Sessions.Core.Ssh;
using Sessions.Terminal;

namespace Sessions.App.Terminal;

/// <summary>
/// The content of one tab: a TerminalControl plus the SSH connection lifecycle
/// (credential prompt, host key confirmation, connect/reconnect, teardown).
/// </summary>
public sealed class TerminalTabView : Grid, IDisposable
{
    private readonly TabViewModel _tab;
    private readonly ICredentialService _credentials;
    private readonly KnownHostsStore _knownHosts;
    private readonly TerminalControl _terminal = new();
    private readonly ProgressRing _spinner = new() { IsActive = false, Width = 48, Height = 48 };

    private SshTerminalSession? _session;
    private bool _connecting;
    private bool _disposed;

    // File pane (Phase 3): lives in column 2 behind a splitter; the resolved connect
    // secret is kept for the tab's lifetime so the SFTP channel never re-prompts.
    private SftpPaneView? _filePane;
    private CommunityToolkit.WinUI.Controls.GridSplitter? _paneSplitter;
    private string? _secret;
    private const double DefaultFilePaneWidth = 340;

    private Session Session => _tab.Session;

    /// <summary>Ctrl+F4 inside the terminal; the window routes it to the confirmed-close pathway.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the user clicks the lock overlay wanting to unlock.</summary>
    public event Action? UnlockRequested;

    /// <summary>Ctrl+Shift+\ inside the terminal (split right / move to other group).</summary>
    public event Action? SplitRequested;

    /// <summary>Raised (UI thread) when a connect to a session with no icon set identified
    /// the OS/vendor from the server banner. The window decides whether to persist it.</summary>
    public event Action<string>? IconSuggested;

    public TerminalTabView(TabViewModel tab, ICredentialService credentials, KnownHostsStore knownHosts)
    {
        _tab = tab;
        _credentials = credentials;
        _knownHosts = knownHosts;

        // Column 0: terminal; column 1: splitter (collapsed); column 2: file pane (width 0
        // until opened). The lock overlay spans all three.
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        Children.Add(_terminal);
        Children.Add(_spinner);

        _terminal.InputReceived += data => _session?.Write(data);
        _terminal.Resized += (cols, rows) => _session?.Resize(cols, rows);
        _terminal.ReconnectRequested += () => DispatcherQueue.TryEnqueue(() => _ = ConnectAsync(isReconnect: true));
        // The page's terminal is constructed with these (init handshake), so it opens
        // with the right theme/fonts instead of restyling just after first paint.
        var initial = App.Settings.Current.WithOverrides(Session.Overrides);
        _terminal.SetInitialOptions(
            initial.FontSize, initial.FontFamily, initial.Theme,
            initial.CopyOnSelect, initial.RightClickPaste, initial.Scrollback,
            BuildHighlightPayload());

        _terminal.Ready += (_, _) => DispatcherQueue.TryEnqueue(() =>
            _ = ConnectAsync(isReconnect: false));
        _terminal.CloseTabRequested += () => DispatcherQueue.TryEnqueue(() => CloseRequested?.Invoke());
        _terminal.SplitRequested += () => DispatcherQueue.TryEnqueue(() => SplitRequested?.Invoke());
        _terminal.FilePaneRequested += () => DispatcherQueue.TryEnqueue(ToggleFilePane);

        Loaded += async (_, _) =>
        {
            if (_session is null && !_connecting)
                await _terminal.InitializeAsync(); // Ready fires when the page is up
        };
    }

    /// <summary>Moves keyboard focus to this tab's terminal when the tab is selected.</summary>
    public void FocusTerminal()
    {
        if (!_disposed && !_tab.IsLocked)
            _terminal.FocusTerminal();
    }

    /// <summary>Adjusts the annotated scrollbar for the current group layout and focus.</summary>
    public void SetRulerPresentation(bool isSplit, bool isGroupFocused) =>
        _terminal.SetRulerPresentation(isSplit, isGroupFocused);

    /// <summary>Kicks off a fresh connection using the terminal's current size.</summary>
    public async Task ConnectAsync(bool isReconnect)
    {
        if (_connecting || _disposed || _session?.IsConnected == true)
            return;
        _connecting = true;
        _spinner.IsActive = true;
        _tab.State = TabConnectionState.Connecting;

        // Tear down the previous (dead) session so its blocked reader thread is released.
        var stale = _session;
        _session = null;
        stale?.Disconnect();

        try
        {
            if (string.IsNullOrWhiteSpace(Session.Username))
            {
                _tab.State = TabConnectionState.Disconnected;
                _terminal.NotifyDisconnected(
                    "No username set for this session. Right-click it in the tree and choose Edit… to add one.");
                return;
            }

            var secret = await ResolveSecretAsync();
            if (secret is null && NeedsSecret())
            {
                _tab.State = TabConnectionState.Disconnected;
                _terminal.NotifyDisconnected("No credential provided.");
                return;
            }
            _secret = secret; // reused by the file pane's SFTP connection

            if (isReconnect)
                _terminal.WriteDivider();
            _terminal.WriteNotice($"Connecting to {Session.Username}@{Session.Host}:{Session.Port} …");

            var session = new SshTerminalSession(_knownHosts)
            {
                HostKeyDecision = info => ConfirmHostKeyBlocking(info),
            };
            session.OutputReceived += data =>
            {
                _terminal.WriteOutput(data);
                // Benign cross-thread reads: worst case is one redundant enqueue.
                if (!_tab.IsActive && !_tab.HasUnseenOutput)
                    DispatcherQueue.TryEnqueue(() => _tab.NotifyOutputActivity());
            };
            session.Closed += ex => DispatcherQueue.TryEnqueue(() =>
            {
                _tab.State = TabConnectionState.Disconnected;
                _tab.ConnectionSummary = "";
                _terminal.NotifyDisconnected(ex is null ? "Connection closed." : $"Connection lost: {ex.Message}");
            });

            var cols = _terminal.Columns;
            var rows = _terminal.Rows;
            var bootstrap = Session.Persistent
                ? TmuxPersistence.BootstrapCommand(Session.Id, _tab.TmuxSlot)
                : null;
            await Task.Run(() => session.Connect(Session, secret, Session.TerminalType, cols, rows, bootstrap));

            _session = session;
            _tab.State = TabConnectionState.Connected;
            _tab.ConnectionSummary = string.Join(" • ",
                new[] { session.Encryption, session.HostKeyFingerprint }.Where(s => !string.IsNullOrEmpty(s)));
            _terminal.NotifyConnected();
            _terminal.FocusTerminal();

            // Icon auto-suggest: only for sessions where the user never chose anything
            // (null; an explicit "none" also blocks this).
            if (Session.Icon is null && SessionIcons.SuggestFromBanner(session.ServerBanner) is { } suggestedIcon)
                IconSuggested?.Invoke(suggestedIcon);
        }
        catch (SshSessionException ex)
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected(ex.Message);
        }
        catch (Exception ex)
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected($"Unexpected error: {ex.Message}");
        }
        finally
        {
            _connecting = false;
            _spinner.IsActive = false;
        }
    }

    private bool NeedsSecret() =>
        Session.AuthMethod == AuthMethod.Password
        || (Session.AuthMethod == AuthMethod.PrivateKey && Session.PassphraseRequired);

    /// <summary>Stored credential, or a prompt; null if the user cancelled. Empty string = no secret needed.</summary>
    private async Task<string?> ResolveSecretAsync()
    {
        if (!NeedsSecret())
            return "";

        var stored = _credentials.Read(Session.Id);
        if (!string.IsNullOrEmpty(stored))
            return stored;

        var isPassphrase = Session.AuthMethod == AuthMethod.PrivateKey;
        var result = await ConnectDialogs.PromptCredentialAsync(
            XamlRoot,
            $"Connect to {Session.Name}",
            isPassphrase
                ? $"Passphrase for {Session.PrivateKeyPath}"
                : $"Password for {Session.Username}@{Session.Host}");
        if (result is not { } cred)
            return null;

        if (cred.Save && cred.Secret.Length > 0)
            _credentials.Write(Session.Id, cred.Secret);
        return cred.Secret;
    }

    /// <summary>Marshals the host-key decision onto the UI thread; called from the connect thread.</summary>
    private bool ConfirmHostKeyBlocking(HostKeyInfo info)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await ConnectDialogs.ConfirmHostKeyAsync(XamlRoot, info));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>Pushes app settings into the xterm page (fonts, theme, clipboard behavior),
    /// with this session's overrides layered on top.</summary>
    public void ApplySettings(Core.Storage.AppSettings settings)
    {
        var effective = settings.WithOverrides(Session.Overrides);
        _terminal.ApplyOptions(
            fontSize: effective.FontSize,
            fontFamily: effective.FontFamily,
            theme: effective.Theme,
            copyOnSelect: effective.CopyOnSelect,
            rightClickPaste: effective.RightClickPaste,
            scrollback: effective.Scrollback);
        _terminal.ApplyHighlights(BuildHighlightPayload());
    }

    /// <summary>Enabled highlight rules for this session (global state + session deltas),
    /// shaped for the page's addon.</summary>
    private IReadOnlyList<object> BuildHighlightPayload() =>
        App.Highlights.ResolveForSession(Session.Overrides)
            .Select(r => (object)new
            {
                id = r.Id,
                name = r.Name,
                pattern = r.Pattern,
                color = r.Color,
                bold = r.Bold,
                underline = r.Underline,
                matchCase = r.MatchCase,
                showInOverview = r.ShowInOverview,
            })
            .ToList();

    /// <summary>
    /// Kills the remote tmux session (persistent sessions only); the attached channel then
    /// closes on its own, which the normal Closed handler reports as disconnected.
    /// </summary>
    public void EndRemoteSession()
    {
        var session = _session;
        if (Session.Persistent && session is not null)
            _ = Task.Run(() => session.TryRunCommand(TmuxPersistence.KillCommand(Session.Id, _tab.TmuxSlot)));
    }

    /// <summary>Local, user-initiated disconnect: tab stays open showing the notice.</summary>
    public void DisconnectLocal()
    {
        var session = _session;
        _session = null;
        session?.Disconnect();
        _tab.State = TabConnectionState.Disconnected;
        _tab.ConnectionSummary = "";
        _terminal.NotifyDisconnected("Disconnected.");
    }

    // ---- file pane (Phase 3) ----

    public bool IsFilePaneOpen => _filePane is not null && ColumnDefinitions[2].Width.Value > 0;

    public void ToggleFilePane()
    {
        if (IsFilePaneOpen)
            HideFilePane();
        else
            ShowFilePane();
    }

    /// <summary>Opens (or reveals) the pane; a non-null <paramref name="initialPath"/> also
    /// navigates there. A non-null <paramref name="notice"/> is shown in the pane's status
    /// line once the listing lands (instead of the item count).</summary>
    public void ShowFilePane(string? initialPath = null, string? notice = null)
    {
        if (_disposed)
            return;
        if (_filePane is null)
        {
            _filePane = new SftpPaneView(() => Session, CreateSftpSessionAsync, OpenInExplorerAsync);
            _filePane.CloseRequested += HideFilePane;
            Grid.SetColumn(_filePane, 2);
            Children.Add(_filePane);

            _paneSplitter = new CommunityToolkit.WinUI.Controls.GridSplitter
            {
                Width = 8,
                ResizeBehavior = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeBehavior.PreviousAndNext,
                ResizeDirection = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns,
            };
            Grid.SetColumn(_paneSplitter, 1);
            Children.Add(_paneSplitter);
        }

        if (!IsFilePaneOpen)
        {
            var width = App.Settings.Current.FilePaneWidth is { } saved and > 100 ? saved : DefaultFilePaneWidth;
            ColumnDefinitions[2].Width = new GridLength(width);
        }
        _paneSplitter!.Visibility = Visibility.Visible;
        _filePane.Visibility = Visibility.Visible;
        if (initialPath is not null || _filePane.IsLoaded)
            _ = _filePane.NavigateAsync(initialPath ?? _filePane.CurrentPath, notice);
    }

    public void HideFilePane()
    {
        if (!IsFilePaneOpen)
            return;
        SaveFilePaneWidth();
        ColumnDefinitions[2].Width = new GridLength(0);
        _paneSplitter!.Visibility = Visibility.Collapsed;
        _terminal.FocusTerminal();
    }

    /// <summary>
    /// "Open file pane at current folder": persistent (tmux) sessions report their cwd over
    /// the exec side-channel; plain sessions fall back to the remote home directory. When
    /// the query fails, the pane says WHY it opened at home instead of failing silently.
    /// </summary>
    public async Task OpenFilePaneAtCurrentFolderAsync()
    {
        string? path = null;
        string? failure = null;
        var session = _session;
        if (Session.Persistent && session is not null && session.IsConnected)
        {
            var result = await Task.Run(() => session.RunCommand(TmuxPersistence.CurrentPathCommand()));
            if (result is null)
                failure = "the connection did not accept the query";
            else if (!result.Success)
                failure = string.IsNullOrWhiteSpace(result.Error) ? "the tmux query failed" : $"tmux: {result.Error.Trim()}";
            else
            {
                path = TmuxPersistence.ParseCurrentPath(result.Output, Session.Id, _tab.TmuxSlot);
                if (path is null)
                    failure = "no matching tmux session in the reply";
            }
        }
        ShowFilePane(path, failure is null ? null : $"Couldn't read the current folder ({failure}) — opened home instead.");
    }

    private Interop.SshfsMount? _sshfsMount;

    /// <summary>Mount the remote filesystem, then open Explorer on it. Password sessions
    /// use the sshfs UNC provider (mounted first — Explorer alone can't authenticate a UNC
    /// argument and silently opens Documents); key sessions spawn sshfs.exe directly with
    /// the session's key, since the UNC API cannot carry one. Runs on a background thread;
    /// the pane surfaces failures.</summary>
    private async Task OpenInExplorerAsync(string remotePath)
    {
        var session = Session;
        string root;
        if (session.AuthMethod == AuthMethod.PrivateKey)
        {
            var mount = _sshfsMount;
            if (mount is not { IsAlive: true })
            {
                mount?.Dispose();
                mount = await Task.Run(() => Interop.SshfsIntegration.MountWithIdentity(session));
                _sshfsMount = mount;
            }
            root = mount.Root;
        }
        else
        {
            var password = session.AuthMethod == AuthMethod.Password ? _secret : null;
            root = await Task.Run(() => Interop.SshfsIntegration.Connect(session, password));
        }
        Interop.SshfsIntegration.OpenInExplorer(root, remotePath);
    }

    /// <summary>Connect factory handed to the pane: reuses the tab's resolved secret
    /// (prompting only if the tab never connected) and the shared host-key trust.</summary>
    private async Task<SftpSession> CreateSftpSessionAsync()
    {
        var secret = _secret ?? await ResolveSecretAsync();
        if (secret is null)
            throw new SshSessionException(SshFailureKind.AuthenticationFailed, "No credential provided.");
        _secret = secret;
        var sftp = new SftpSession(_knownHosts);
        try
        {
            await Task.Run(() => sftp.Connect(Session, secret));
        }
        catch
        {
            sftp.Dispose();
            throw;
        }
        return sftp;
    }

    private void SaveFilePaneWidth()
    {
        var width = ColumnDefinitions[2].Width.Value;
        if (width > 100 && Math.Abs(width - (App.Settings.Current.FilePaneWidth ?? 0)) > 1)
            App.Settings.Save(App.Settings.Current with { FilePaneWidth = width });
    }

    // ---- lock overlay (per plan: obscure output, block input, buffer continues) ----

    private Button? _lockOverlay;

    public void ShowLockOverlay()
    {
        _terminal.SetInputEnabled(false); // blocks keyboard/pointer into the WebView2
        if (_lockOverlay is null)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
            };
            panel.Children.Add(new FontIcon { Glyph = "", FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(new TextBlock
            {
                Text = "Session locked — click to unlock",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 220)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            // A Button so the overlay is focusable and swallows keyboard input while locked.
            _lockOverlay = new Button
            {
                Content = panel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(242, 24, 24, 24)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
            };
            _lockOverlay.Click += (_, _) => UnlockRequested?.Invoke();
            Grid.SetColumnSpan(_lockOverlay, 3); // cover the file pane and splitter too
        }
        if (!Children.Contains(_lockOverlay))
            Children.Add(_lockOverlay);
        _lockOverlay.Focus(FocusState.Programmatic);
    }

    public void HideLockOverlay()
    {
        if (_lockOverlay is not null)
            Children.Remove(_lockOverlay);
        _terminal.SetInputEnabled(true);
        _terminal.FocusTerminal();
    }

    /// <summary>Clean disconnect keeping the tab and scrollback (used by tab close and app teardown).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (IsFilePaneOpen)
            SaveFilePaneWidth();
        _filePane?.Dispose();
        _filePane = null;
        _sshfsMount?.Dispose(); // killing the sshfs process unmounts the drive
        _sshfsMount = null;
        // Plan-mandated order: reader â†’ shell â†’ client (inside Disconnect) â†’ WebView2.
        _session?.Disconnect();
        _session = null;
        _terminal.Dispose();
    }
}
