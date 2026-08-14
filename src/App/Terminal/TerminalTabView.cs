using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.App.Dialogs;
using Sessions.App.ViewModels;
using Sessions.Core.Credentials;
using Sessions.Core.Models;
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

    private Session Session => _tab.Session;

    /// <summary>Ctrl+F4 inside the terminal; the window routes it to the confirmed-close pathway.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the user clicks the lock overlay wanting to unlock.</summary>
    public event Action? UnlockRequested;

    /// <summary>Ctrl+Shift+\ inside the terminal (split right / move to other group).</summary>
    public event Action? SplitRequested;

    public TerminalTabView(TabViewModel tab, ICredentialService credentials, KnownHostsStore knownHosts)
    {
        _tab = tab;
        _credentials = credentials;
        _knownHosts = knownHosts;

        Children.Add(_terminal);
        Children.Add(_spinner);

        _terminal.InputReceived += data => _session?.Write(data);
        _terminal.Resized += (cols, rows) => _session?.Resize(cols, rows);
        _terminal.ReconnectRequested += () => DispatcherQueue.TryEnqueue(() => _ = ConnectAsync(isReconnect: true));
        _terminal.Ready += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplySettings(App.Settings.Current);
            _ = ConnectAsync(isReconnect: false);
        });
        _terminal.CloseTabRequested += () => DispatcherQueue.TryEnqueue(() => CloseRequested?.Invoke());
        _terminal.SplitRequested += () => DispatcherQueue.TryEnqueue(() => SplitRequested?.Invoke());

        Loaded += async (_, _) =>
        {
            if (_session is null && !_connecting)
                await _terminal.InitializeAsync(); // Ready fires when the page is up
        };
    }

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

            if (isReconnect)
                _terminal.WriteDivider();
            _terminal.WriteNotice($"Connecting to {Session.Username}@{Session.Host}:{Session.Port} …");

            var session = new SshTerminalSession(_knownHosts)
            {
                HostKeyDecision = info => ConfirmHostKeyBlocking(info),
            };
            session.OutputReceived += data => _terminal.WriteOutput(data);
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

    /// <summary>Pushes app settings into the xterm page (fonts, theme, clipboard behavior).</summary>
    public void ApplySettings(Core.Storage.AppSettings settings) =>
        _terminal.ApplyOptions(
            fontSize: settings.FontSize,
            fontFamily: settings.FontFamily,
            theme: settings.Theme,
            copyOnSelect: settings.CopyOnSelect,
            rightClickPaste: settings.RightClickPaste,
            scrollback: settings.Scrollback);

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
        // Plan-mandated order: reader â†’ shell â†’ client (inside Disconnect) â†’ WebView2.
        _session?.Disconnect();
        _session = null;
        _terminal.Dispose();
    }
}
