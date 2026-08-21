using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Agents;
using Resesh.Core.Backend;
using Resesh.Core.Credentials;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Recording;
using Resesh.Core.Sftp;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Resesh.Terminal;

namespace Resesh.App.Terminal;

/// <summary>
/// The content of one tab: a TerminalControl plus the shell lifecycle for either target
/// kind — SSH (credential prompt, host key confirmation, connect/reconnect, teardown) or
/// a local ConPTY process (launch, exit code, restart). The live shell is an
/// <see cref="ITerminalBackend"/>; SSH-only surfaces keep a typed reference beside it.
/// </summary>
public sealed class TerminalTabView : Grid, IDisposable
{
    private readonly TabViewModel _tab;
    private readonly ICredentialService _credentials;
    private readonly KnownHostsStore _knownHosts;
    private readonly SshKeyStore _sshKeys;
    private readonly IReadOnlySet<int> _tmuxSlotsAlreadyOpen;
    private readonly TerminalControl _terminal = new();
    private readonly ProgressRing _spinner = new() { IsActive = false, Width = 48, Height = 48 };

    private ITerminalBackend? _backend;
    private SshTerminalSession? _ssh; // set when _backend is the SSH implementation
    private bool _connecting;
    private bool _disposed;
    private TerminalCapture? _capture;
    private TerminalPlayerView? _rewindPlayer;
    private bool _rewindAvailable;
    private readonly Osc7WorkingDirectoryTracker _workingDirectory = new();

    // Agent awareness (Phase 6.2). One tracker per tab, fed only by this tab's own page
    // and backend — a session cannot describe another tab's agent, whatever it writes.
    private readonly AgentTracker _agent;
    private DispatcherQueueTimer? _agentPoll;
    private bool _agentPollBusy;

    // File pane (Phase 3): lives in column 2 behind a splitter; the resolved connect
    // secret is kept for the tab's lifetime so the SFTP channel never re-prompts.
    private SftpPaneView? _filePane;
    private CommunityToolkit.WinUI.Controls.GridSplitter? _paneSplitter;
    private Border? _paneSplitterLine;
    private ThemeVisualPalette _chromePalette = ThemeVisualPalette.For(App.Settings.Current.Theme);
    private bool _paneSplitterActive;
    private string? _secret;
    private Session? _resolvedSshSession;
    private const double DefaultFilePaneWidth = 340;

    private Session Session => _tab.Session;

    /// <summary>Ctrl+F4 inside the terminal; the window routes it to the confirmed-close pathway.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the user clicks the lock overlay wanting to unlock.</summary>
    public event Action? UnlockRequested;

    /// <summary>Ctrl+Shift+\ inside the terminal (split right / move to other group).</summary>
    public event Action? SplitRequested;

    /// <summary>Ctrl+Shift+T inside the terminal (open the default local profile).</summary>
    public event Action? NewLocalTabRequested;

    /// <summary>Raised (UI thread) when a connect to a session with no icon set identified
    /// the OS/vendor from the server banner. The window decides whether to persist it.</summary>
    public event Action<string>? IconSuggested;

    /// <summary>Raised (UI thread) when this tab's agent moved into a state worth alerting
    /// on. The window decides whether anything is shown — an agent event may change UI
    /// state and draw attention, never send input or approve anything.</summary>
    public event Action<TabViewModel, AgentSnapshot>? AgentAlert;

    /// <summary>Raised when the file pane opens or closes so group chrome can follow it.</summary>
    public event Action? FilePaneOpenChanged;

    /// <summary>True while the terminal page's commands panel is open. Kept from page
    /// reports because Ctrl+Shift+O and the panel's ✕ change the state page-side.</summary>
    public bool IsCommandsPanelOpen { get; private set; }

    /// <summary>Raised when the commands panel opens or closes so the tab-strip toggle
    /// button can mirror it.</summary>
    public event Action? CommandsPanelOpenChanged;

    /// <summary>Raised when recording or rewind availability changes.</summary>
    public event Action? CaptureStateChanged;

    public bool IsRecording => _capture?.IsRecording == true;
    public bool CanRecord => _capture is not null && !_disposed;
    public bool IsRewinding => _rewindPlayer is not null;
    public bool CanRewind => _rewindAvailable;
    public string? RecordingPath => _capture?.RecordingPath;

    public TerminalTabView(TabViewModel tab, ICredentialService credentials, KnownHostsStore knownHosts,
        SshKeyStore sshKeys,
        IReadOnlySet<int>? tmuxSlotsAlreadyOpen = null)
    {
        _tab = tab;
        _credentials = credentials;
        _knownHosts = knownHosts;
        _sshKeys = sshKeys;
        _tmuxSlotsAlreadyOpen = tmuxSlotsAlreadyOpen ?? new HashSet<int>();

        // Column 0: terminal; column 1: splitter (collapsed); column 2: file pane (width 0
        // until opened). The lock overlay spans all three.
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        Children.Add(_terminal);
        Children.Add(_spinner);

        _terminal.InputReceived += data =>
        {
            _backend?.Write(data);
            // Answering is what unblocks a waiting agent, so input clears a sticky badge.
            ApplyAgent(tracker => tracker.ObserveUserInput());
        };
        _terminal.Resized += (cols, rows) =>
        {
            _backend?.Resize(cols, rows);
            _capture?.CaptureResize(cols, rows, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        };
        _terminal.OutputObserved += (data, unixMs) => _capture?.CaptureOutput(data, unixMs);
        _terminal.KeyframeCaptured += (state, cols, rows, unixMs) =>
            _capture?.CaptureKeyframe(state, cols, rows, unixMs);
        _terminal.ReconnectRequested += () => DispatcherQueue.TryEnqueue(() => _ = ConnectAsync(isReconnect: true));
        // The page's terminal is constructed with these (init handshake), so it opens
        // with the right theme/fonts instead of restyling just after first paint.
        var initial = App.Settings.Current.WithOverrides(Session.Overrides);
        _terminal.SetInitialOptions(
            initial.FontSize, initial.FontFamily, initial.Theme,
            initial.CopyOnSelect, initial.RightClickPaste, initial.Scrollback,
            BuildHighlightPayload());
        _terminal.SetPromptPlatform(Session.Icon);

        _terminal.Ready += (cols, rows) => DispatcherQueue.TryEnqueue(() =>
        {
            EnsureCapture(cols, rows);
            if (initial.AlwaysRecord)
                TryStartAutomaticRecording();
            _ = ConnectAsync(isReconnect: false);
        });
        _terminal.TitleChanged += title => DispatcherQueue.TryEnqueue(() => _tab.ApplyTerminalTitle(title));
        _terminal.CommandChanged += text => DispatcherQueue.TryEnqueue(() => _tab.ApplyRunningCommand(text));
        _terminal.PromptContextChanged += (context, platform) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _tab.ApplyPromptContext(context, platform);
                if (platform is "nokia" or "juniper" or "cisco" && Session.Icon is null)
                    IconSuggested?.Invoke(platform);
            });
        _terminal.WorkingDirectoryReported += payload => DispatcherQueue.TryEnqueue(() =>
        {
            if (Osc7WorkingDirectoryParser.TryParse(payload, out var report) && report is not null)
                _workingDirectory.Observe(report);
        });
        _terminal.ContextReported += payload => DispatcherQueue.TryEnqueue(() =>
        {
            if (Osc3008ContextParser.TryParse(payload, out var context) && context is
                { Action: Osc3008ContextAction.Start, Type: "shell" or "command", WorkingDirectory: not null })
            {
                _workingDirectory.Observe(new Osc7WorkingDirectory(
                    context.Hostname ?? "", context.WorkingDirectory));
            }
        });
        _terminal.CloseTabRequested += () => DispatcherQueue.TryEnqueue(() => CloseRequested?.Invoke());
        _terminal.SplitRequested += () => DispatcherQueue.TryEnqueue(() => SplitRequested?.Invoke());
        _terminal.FilePaneRequested += () => DispatcherQueue.TryEnqueue(ToggleFilePane);
        _terminal.CommandsPanelOpenChanged += open => DispatcherQueue.TryEnqueue(() =>
        {
            IsCommandsPanelOpen = open;
            CommandsPanelOpenChanged?.Invoke();
        });
        _terminal.NewLocalTabRequested += () => DispatcherQueue.TryEnqueue(() => NewLocalTabRequested?.Invoke());

        _agent = new AgentTracker(Session.Agent);
        WireAgentSignals();

        Loaded += async (_, _) =>
        {
            if (_backend is null && !_connecting)
                await _terminal.InitializeAsync(); // Ready fires when the page is up
        };
    }

    private void EnsureCapture(int columns, int rows)
    {
        if (_capture is not null)
            return;
        var settings = App.Settings.Current;
        _capture = new TerminalCapture(
            columns,
            rows,
            maximumAge: TimeSpan.FromMinutes(Math.Clamp(settings.RewindMinutes, 1, 24 * 60)),
            maximumBytes: Math.Clamp(settings.RewindMegabytes, 1, 1024) * 1024L * 1024L);
        _capture.Changed += OnCaptureChanged;
        _capture.RecordingChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(SyncCaptureState);
        SyncCaptureState();
    }

    private void OnCaptureChanged()
    {
        if (_rewindAvailable || _capture is null)
            return;
        var snapshot = _capture.Snapshot();
        if (snapshot.Keyframe is null && snapshot.Events.Count == 0)
            return;
        _rewindAvailable = true;
        DispatcherQueue.TryEnqueue(SyncCaptureState);
    }

    private void SyncCaptureState()
    {
        _tab.IsRecording = IsRecording;
        _tab.HasRewind = CanRewind;
        CaptureStateChanged?.Invoke();
    }

    private void TryStartAutomaticRecording()
    {
        try
        {
            StartRecordingCore();
        }
        catch (Exception exception)
        {
            _terminal.WriteNotice($"Automatic recording could not start: {exception.Message}");
        }
    }

    private string StartRecordingCore()
    {
        var capture = _capture ?? throw new InvalidOperationException("The terminal is not ready.");
        var settings = App.Settings.Current;
        return capture.StartRecording(
            settings.RecordingDirectory, Session.Name, Session.TerminalType);
    }

    public async Task ToggleRecordingAsync()
    {
        if (_capture is null)
            return;
        if (_capture.IsRecording)
        {
            _capture.StopRecording();
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Start recording?",
            Content = "The recording captures all output echoed to this terminal. It can include secrets that a server prints.",
            PrimaryButtonText = "Start recording",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            StartRecordingCore();
        }
        catch (Exception exception)
        {
            await new ContentDialog
            {
                Title = "Recording could not start",
                Content = exception.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    public Task ToggleRewindAsync()
    {
        if (_rewindPlayer is not null)
        {
            ReturnToLive();
            return Task.CompletedTask;
        }
        if (_capture is null || !CanRewind)
            return Task.CompletedTask;

        var player = new TerminalPlayerView(_capture);
        player.CloseRequested += ReturnToLive;
        _rewindPlayer = player;
        Grid.SetColumnSpan(player, 3);
        Children.Add(player);
        _terminal.SetInputEnabled(false);
        SyncCaptureState();
        return Task.CompletedTask;
    }

    private void ReturnToLive()
    {
        if (_rewindPlayer is not { } player)
            return;
        _rewindPlayer = null;
        Children.Remove(player);
        player.Dispose();
        if (!_tab.IsLocked)
        {
            _terminal.SetInputEnabled(true);
            _terminal.FocusTerminal();
        }
        SyncCaptureState();
    }

    // ---- agent awareness (Phase 6.2) ----

    /// <summary>The identity the user pinned from the tab menu; null = follow detection.</summary>
    public string? AgentOverride => _agent.ManualOverride;

    /// <summary>What the tab is showing right now (identity, attention, and its source).</summary>
    public AgentSnapshot AgentState => _agent.Current;

    /// <summary>Tab menu: pin an identity, "auto" (null), or <c>AgentIdentities.None</c>.</summary>
    public void SetAgentOverride(string? key) => ApplyAgent(tracker => tracker.SetManualOverride(key));

    /// <summary>Re-reads the session's saved default after Session Options changed it.</summary>
    public void RefreshAgentDefault()
    {
        _agent.SessionDefault = Session.Agent;
        PushAgentState();
    }

    /// <summary>
    /// Subscribes to every evidence source. The page forwards raw escape sequences, titles
    /// and marked commands; all of the mapping happens in the tracker. Detection can say
    /// WHICH agent is running; only an adapter's structured event may say it is waiting.
    /// </summary>
    private void WireAgentSignals()
    {
        _terminal.AgentOscReceived += (code, data) =>
            ApplyAgent(tracker => tracker.ObserveEvent(AgentOsc.Parse(code, data)));
        _terminal.BellReceived += () =>
            ApplyAgent(tracker => tracker.ObserveEvent(AgentOsc.Bell()));
        _terminal.TitleChanged += title => ApplyAgent(tracker => tracker.ObserveTitle(title));
        _terminal.CommandObserved += command => ApplyAgent(tracker => tracker.ObserveCommand(command));

        _tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.IsActive) && _tab.IsActive)
                ApplyAgent(tracker => tracker.ObserveViewed());
            else if (e.PropertyName == nameof(TabViewModel.Session))
                RefreshAgentDefault();
        };
        PushAgentState(); // a session default shows before anything has been observed
    }

    private void ApplyAgent(Func<AgentTracker, bool> observe)
    {
        if (_disposed || !observe(_agent))
            return;
        PushAgentState();
    }

    private void PushAgentState()
    {
        var snapshot = _agent.Current;
        _tab.Agent = snapshot;
        if (snapshot.Attention.IsAlert())
            AgentAlert?.Invoke(_tab, snapshot);
    }

    /// <summary>
    /// Local tabs learn their agent from job-object membership: the processes inside this
    /// shell's own job. It needs no shell integration, works for PowerShell and cmd (whose
    /// prompts the command-mark regex deliberately can't discover), and — unlike any screen
    /// heuristic — an agent's disappearance is as definite as its arrival.
    /// </summary>
    private void StartAgentPolling()
    {
        if (_agent.Suppressed || !Session.IsLocal)
            return;
        _agentPoll ??= CreateAgentPollTimer();
        if (!_agentPoll.IsRunning)
            _agentPoll.Start();
    }

    private DispatcherQueueTimer CreateAgentPollTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => PollLocalAgentProcesses();
        return timer;
    }

    private async void PollLocalAgentProcesses()
    {
        if (_disposed || _agentPollBusy)
            return;
        if (_backend is not LocalTerminalSession { IsRunning: true } local || _agent.Suppressed)
        {
            _agentPoll?.Stop();
            return;
        }
        _agentPollBusy = true;
        try
        {
            // Off the UI thread: one kernel call plus a name lookup per process in the job.
            var names = await Task.Run(local.GetJobProcessNames);
            if (!_disposed)
                ApplyAgent(tracker => tracker.ObserveProcesses(names));
        }
        catch (Exception ex)
        {
            TerminalControl.TraceHook?.Invoke($"agent poll: {ex.Message}");
        }
        finally
        {
            _agentPollBusy = false;
        }
    }

    /// <summary>The shell ended: everything detected about the agent is stale.</summary>
    private void EndAgentTracking()
    {
        _agentPoll?.Stop();
        ApplyAgent(tracker => tracker.ObserveEnded());
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

    /// <summary>Opens or closes the terminal's typed-commands panel (tab-strip button).</summary>
    public void ToggleCommandsPanel()
    {
        if (!_disposed && !_tab.IsLocked)
            _terminal.ToggleCommandsPanel();
    }

    /// <summary>Kicks off a fresh connection/launch using the terminal's current size.</summary>
    public async Task ConnectAsync(bool isReconnect)
    {
        if (_connecting || _disposed || _ssh?.IsConnected == true
            || _backend is LocalTerminalSession { IsRunning: true })
            return;
        _connecting = true;
        _spinner.IsActive = true;
        _tab.State = TabConnectionState.Connecting;
        _workingDirectory.Reset();

        // Tear down the previous (dead) backend so its blocked reader thread is released.
        var stale = _backend;
        _backend = null;
        _ssh = null;
        stale?.Stop();

        try
        {
            if (Session.IsLocal)
                await LaunchLocalAsync(isReconnect);
            else
                await ConnectSshAsync(isReconnect);
        }
        finally
        {
            _connecting = false;
            _spinner.IsActive = false;
        }
    }

    /// <summary>Local lifecycle: start the ConPTY process; a later natural exit keeps the
    /// tab open, reports the exit code neutrally, and Enter/Restart relaunches.</summary>
    private async Task LaunchLocalAsync(bool isReconnect)
    {
        try
        {
            if (isReconnect)
                _terminal.WriteDivider(); // restart-in-place: scrollback preserved

            var local = new LocalTerminalSession();
            local.OutputReceived += data =>
            {
                _terminal.WriteOutput(data);
                if (!_tab.IsActive && !_tab.HasUnseenOutput)
                    DispatcherQueue.TryEnqueue(() => _tab.NotifyOutputActivity());
            };
            local.Exited += code => DispatcherQueue.TryEnqueue(() =>
            {
                _tab.ExitCode = code;
                _tab.State = TabConnectionState.Exited;
                _tab.ConnectionSummary = "";
                EndAgentTracking();
                _terminal.NotifyDisconnected($"Process exited with code {code}.", action: "restart", neutral: true);
            });

            var cols = _terminal.Columns;
            var rows = _terminal.Rows;
            await Task.Run(() => local.Start(Session, cols, rows));

            _backend = local;
            _tab.ExitCode = null;
            _tab.State = TabConnectionState.Connected;
            _tab.ConnectionSummary = $"pid {local.ProcessId}";
            _terminal.NotifyConnected();
            _terminal.FocusTerminal();
            StartAgentPolling();
        }
        catch (LocalSessionException ex)
        {
            // Launch failure (unlike a normal exit) is an error state — red dot, warning text.
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected(ex.Message, action: "restart");
        }
        catch (Exception ex)
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected($"Unexpected error: {ex.Message}", action: "restart");
        }
    }

    private async Task ConnectSshAsync(bool isReconnect)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Session.Username))
            {
                _tab.State = TabConnectionState.Disconnected;
                _terminal.NotifyDisconnected(
                    "No username set for this session. Right-click it in the tree and choose Edit… to add one.");
                return;
            }

            var resolved = await ResolveSshCredentialAsync();
            if (resolved is null)
            {
                _tab.State = TabConnectionState.Disconnected;
                _terminal.NotifyDisconnected("No credential provided.");
                return;
            }
            var (connectionSession, secret) = resolved;
            _secret = secret; // reused by the file pane's SFTP connection
            _resolvedSshSession = connectionSession;

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
                EndAgentTracking();
                _terminal.NotifyDisconnected(ex is null ? "Connection closed." : $"Connection lost: {ex.Message}");
            });

            var cols = _terminal.Columns;
            var rows = _terminal.Rows;
            Func<SshTerminalSession, string?>? bootstrapFactory = Session.Persistent
                ? connected => SelectTmuxBootstrapBlocking(connected, isReconnect)
                : null;
            await Task.Run(() => session.Connect(
                connectionSession, secret, Session.TerminalType, cols, rows,
                bootstrapCommandFactory: bootstrapFactory,
                interactiveResponder: PromptKeyboardInteractiveBlocking));

            _backend = session;
            _ssh = session;
            _tab.State = TabConnectionState.Connected;
            _tab.ConnectionSummary = string.Join(" • ",
                new[] { session.Encryption, session.HostKeyFingerprint }.Where(s => !string.IsNullOrEmpty(s)));
            _terminal.NotifyConnected();
            _terminal.FocusTerminal();

            // Icon auto-suggest: only for sessions where the user never chose anything
            // (null; an explicit "none" also blocks this).
            if (SessionIcons.SuggestFromBanner(session.ServerBanner) is { } suggestedIcon)
            {
                _terminal.SetPromptPlatform(suggestedIcon);
                if (Session.Icon is null)
                    IconSuggested?.Invoke(suggestedIcon);
            }
        }
        catch (SshSessionException ex)
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected(ex.Message);
        }
        catch (OperationCanceledException)
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected("Connection cancelled.");
        }
        catch (Exception ex)
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>Runs after SSH authentication and before the interactive shell opens.</summary>
    private string SelectTmuxBootstrapBlocking(SshTerminalSession connected, bool isReconnect)
    {
        var result = connected.RunCommand(TmuxPersistence.DiscoveryCommand());
        var remoteSessions = result is { Success: true }
            ? TmuxPersistence.ParseSessions(result.Output, Session.Id)
            : [];

        // Reconnect keeps the target selected for this tab. If it no longer exists, the
        // normal bootstrap recreates it with the same name.
        if (isReconnect)
            return TmuxPersistence.BootstrapCommand(Session.Id, _tab.TmuxSlot);

        var available = remoteSessions
            .Where(remote => !_tmuxSlotsAlreadyOpen.Contains(remote.Slot))
            .ToList();
        var newSlot = TmuxPersistence.NextAvailableSlot(
            remoteSessions.Select(remote => remote.Slot).Concat(_tmuxSlotsAlreadyOpen));

        var selectedSlot = available.Count switch
        {
            0 => newSlot,
            1 => available[0].Slot,
            _ => SelectTmuxSessionBlocking(available, newSlot),
        };
        _tab.TmuxSlot = selectedSlot;
        return TmuxPersistence.BootstrapCommand(Session.Id, selectedSlot);
    }

    /// <summary>Marshals tmux selection onto the UI thread while the SSH worker waits.</summary>
    private int SelectTmuxSessionBlocking(IReadOnlyList<TmuxSessionInfo> sessions, int newSlot)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await ConnectDialogs.SelectTmuxSessionAsync(XamlRoot, sessions, newSlot));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            throw new OperationCanceledException("The tmux session selector could not open.");
        }
        return tcs.Task.GetAwaiter().GetResult()
            ?? throw new OperationCanceledException("tmux session selection was cancelled.");
    }

    private sealed record ResolvedSshCredential(Session Session, string Secret);

    /// <summary>Resolves a session password or a registered key plus its shared passphrase.</summary>
    private async Task<ResolvedSshCredential?> ResolveSshCredentialAsync()
    {
        if (Session.AuthMethod == AuthMethod.None)
            return new ResolvedSshCredential(Session, "");

        if (Session.AuthMethod == AuthMethod.PrivateKey)
            return await ResolvePrivateKeyAsync();

        var stored = _credentials.Read(Session.Id);
        if (!string.IsNullOrEmpty(stored))
            return new ResolvedSshCredential(Session, stored);

        var result = await ConnectDialogs.PromptCredentialAsync(
            XamlRoot,
            $"Connect to {Session.Name}",
            $"Password for {Session.Username}@{Session.Host}");
        if (result is not { } cred)
            return null;

        if (cred.Save && cred.Secret.Length > 0)
            _credentials.Write(Session.Id, cred.Secret);
        return new ResolvedSshCredential(Session, cred.Secret);
    }

    private async Task<ResolvedSshCredential?> ResolvePrivateKeyAsync()
    {
        if (Session.PrivateKeyId is not { } keyId || _sshKeys.Find(keyId) is not { } key)
            throw new InvalidOperationException("The session does not have a registered SSH key.");
        if (!key.IsAvailable)
            throw new FileNotFoundException(
                $"The SSH key '{key.Name}' is unavailable. Use File > SSH Keys to locate it.", key.Path);

        var secret = key.IsEncrypted == true
            ? _credentials.ReadKey(keyId) ?? _credentials.Read(Session.Id)
            : null;

        SshKeyReference validated;
        if (key.IsEncrypted == true && string.IsNullOrEmpty(secret))
        {
            var prompted = await PromptAndValidateKeyAsync(key);
            if (prompted is null)
                return null;
            (validated, secret) = prompted.Value;
        }
        else
        {
            try
            {
                validated = await ValidateKeyAsync(keyId, secret);
                if (validated.IsEncrypted == true && string.IsNullOrEmpty(secret))
                {
                    var prompted = await PromptAndValidateKeyAsync(validated);
                    if (prompted is null)
                        return null;
                    (validated, secret) = prompted.Value;
                }
            }
            catch (SshKeyPassphraseException)
            {
                var prompted = await PromptAndValidateKeyAsync(
                    key, "The stored passphrase was not accepted.");
                if (prompted is null)
                    return null;
                (validated, secret) = prompted.Value;
            }
        }

        return new ResolvedSshCredential(
            Session with { PrivateKeyPath = validated.Path, PassphraseRequired = validated.IsEncrypted == true },
            secret ?? "");
    }

    private async Task<(SshKeyReference Key, string Secret)?> PromptAndValidateKeyAsync(
        SshKeyReference key, string? notice = null)
    {
        while (true)
        {
            var prompted = await ConnectDialogs.PromptCredentialAsync(
                XamlRoot,
                $"Unlock {key.Name}",
                notice is null ? $"Passphrase for {key.Name}" : $"{notice} Passphrase for {key.Name}");
            if (prompted is null)
                return null;
            try
            {
                var validated = await ValidateKeyAsync(key.Id, prompted.Value.Secret);
                if (prompted.Value.Save && prompted.Value.Secret.Length > 0)
                    _credentials.WriteKey(key.Id, prompted.Value.Secret);
                return (validated, prompted.Value.Secret);
            }
            catch (SshKeyPassphraseException)
            {
                notice = "The passphrase was not accepted.";
            }
        }
    }

    private async Task<SshKeyReference> ValidateKeyAsync(Guid keyId, string? passphrase)
    {
        try
        {
            return await Task.Run(() => _sshKeys.Validate(keyId, passphrase));
        }
        catch (SshKeyChangedException change)
        {
            if (!await ConnectDialogs.ConfirmChangedPrivateKeyAsync(XamlRoot, change))
                throw new OperationCanceledException("SSH key replacement was not accepted.");
            return await Task.Run(() => _sshKeys.Validate(keyId, passphrase, acceptChanged: true));
        }
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

    /// <summary>Marshals server keyboard-interactive challenges onto the UI thread.</summary>
    private IReadOnlyList<string>? PromptKeyboardInteractiveBlocking(
        IReadOnlyList<KeyboardInteractivePrompt> prompts)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await ConnectDialogs.PromptKeyboardInteractiveAsync(
                    XamlRoot, $"Authenticate to {Session.Name}", prompts));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            return null;
        }
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>Pushes app settings into the xterm page (fonts, theme, clipboard behavior),
    /// with this session's overrides layered on top.</summary>
    public void ApplySettings(Core.Storage.AppSettings settings)
    {
        _chromePalette = ThemeVisualPalette.For(settings.Theme);
        ApplyPaneSplitterTheme();
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
    /// Kills the remote tmux session (persistent sessions only). Waiting for the command
    /// lets a caller close the tab only after the server accepted the request.
    /// </summary>
    public async Task<bool> TryEndRemoteSessionAsync()
    {
        var session = _ssh;
        return Session.Persistent
            && session is not null
            && await Task.Run(() => session.TryRunCommand(
                TmuxPersistence.KillCommand(Session.Id, _tab.TmuxSlot)));
    }

    /// <summary>User-initiated Disconnect (SSH) / Stop (local): tab stays open showing the
    /// notice. Stopping a local shell kills its whole process tree via the job object.</summary>
    public void DisconnectLocal()
    {
        var backend = _backend;
        _backend = null;
        _ssh = null;
        backend?.Stop();
        _tab.ConnectionSummary = "";
        EndAgentTracking();
        if (Session.IsLocal)
        {
            _tab.ExitCode = null;
            _tab.State = TabConnectionState.Exited;
            _terminal.NotifyDisconnected("Stopped.", action: "restart", neutral: true);
        }
        else
        {
            _tab.State = TabConnectionState.Disconnected;
            _terminal.NotifyDisconnected("Disconnected.");
        }
    }

    /// <summary>Opens the local profile's starting directory in Explorer (local tabs'
    /// stand-in for the remote file pane until a local files provider exists).</summary>
    public void OpenWorkingFolder()
    {
        var directory = Environment.ExpandEnvironmentVariables(Session.Local?.StartingDirectory?.Trim() ?? "");
        if (directory.Length == 0)
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(directory))
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = false,
            });
        }
    }

    // ---- file pane (Phase 3) ----

    public bool IsFilePaneOpen => _filePane is not null && ColumnDefinitions[2].Width.Value > 0;

    public void ToggleFilePane()
    {
        if (!_tab.Capabilities.RemoteFiles)
            return; // remote-only surface; local tabs use Open Working Folder instead
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
        if (_disposed || !_tab.Capabilities.RemoteFiles)
            return;
        var wasOpen = IsFilePaneOpen;
        if (_filePane is null)
        {
            _filePane = new SftpPaneView(() => Session, CreateSftpSessionAsync, OpenInExplorerAsync);
            _filePane.CloseRequested += HideFilePane;
            Grid.SetColumn(_filePane, 2);
            Children.Add(_filePane);

            _paneSplitter = new CommunityToolkit.WinUI.Controls.GridSplitter
            {
                Width = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                ResizeBehavior = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeBehavior.PreviousAndNext,
                ResizeDirection = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns,
            };
            _paneSplitterLine = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_chromePalette.Divider),
                IsHitTestVisible = false,
            };
            _paneSplitter.PointerEntered += (_, _) => SetPaneSplitterActive(active: true);
            _paneSplitter.PointerExited += (_, _) => SetPaneSplitterActive(active: false);
            _paneSplitter.ManipulationStarted += (_, _) => SetPaneSplitterActive(active: true);
            _paneSplitter.ManipulationCompleted += (_, _) => SetPaneSplitterActive(active: false);
            ColumnDefinitions[1].Width = new GridLength(1);
            Grid.SetColumn(_paneSplitterLine, 1);
            Children.Add(_paneSplitterLine);
            Grid.SetColumn(_paneSplitter, 1);
            Children.Add(_paneSplitter);
        }

        if (!IsFilePaneOpen)
        {
            var width = App.Settings.Current.FilePaneWidth is { } saved and > 100 ? saved : DefaultFilePaneWidth;
            ColumnDefinitions[2].Width = new GridLength(width);
        }
        _paneSplitter!.Visibility = Visibility.Visible;
        _paneSplitterLine!.Visibility = Visibility.Visible;
        _filePane.Visibility = Visibility.Visible;
        if (!wasOpen)
            FilePaneOpenChanged?.Invoke();
        if (initialPath is not null || _filePane.IsLoaded)
            _ = _filePane.NavigateAsync(initialPath ?? _filePane.CurrentPath, notice);
    }

    private void SetPaneSplitterActive(bool active)
    {
        if (_paneSplitterLine is null)
            return;
        _paneSplitterActive = active;
        ApplyPaneSplitterTheme();
        _paneSplitterLine.Width = active ? 3 : 1;
    }

    private void ApplyPaneSplitterTheme()
    {
        if (_paneSplitterLine is null)
            return;
        _paneSplitterLine.Background = _paneSplitterActive
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SessionSplitterHoverBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(_chromePalette.Divider);
    }

    public void HideFilePane()
    {
        if (!IsFilePaneOpen)
            return;
        SaveFilePaneWidth();
        ColumnDefinitions[2].Width = new GridLength(0);
        _paneSplitter!.Visibility = Visibility.Collapsed;
        _paneSplitterLine!.Visibility = Visibility.Collapsed;
        FilePaneOpenChanged?.Invoke();
        _terminal.FocusTerminal();
    }

    /// <summary>
    /// "Open file pane at terminal folder": use the persistent-session side channel first,
    /// then a validated OSC 7 report, a zero-input Linux process query, and a path-shaped
    /// shell prompt. If all sources fail, the pane says why it opened at home.
    /// </summary>
    public async Task OpenFilePaneAtCurrentFolderAsync()
    {
        if (!_tab.Capabilities.RemoteFiles)
            return;
        string? path = null;
        string? failure = null;
        var canUsePromptFallback = true;
        var session = _ssh;
        if (Session.Persistent && session is not null && session.IsConnected)
        {
            var result = await Task.Run(() => session.RunCommand(TmuxPersistence.CurrentPathCommand()));
            if (result is null)
                failure = "the connection did not accept the query";
            else if (!result.Success)
                failure = string.IsNullOrWhiteSpace(result.Error)
                    ? "the persistent session query failed"
                    : $"persistent session: {result.Error.Trim()}";
            else
            {
                path = TmuxPersistence.ParseCurrentPath(result.Output, Session.Id, _tab.TmuxSlot);
                if (path is null)
                    failure = "no matching persistent session in the reply";
            }
        }

        if (path is null && _workingDirectory.Path is { } reportedPath)
            path = reportedPath;

        if (path is null && _workingDirectory.HostMismatch)
        {
            failure = "the shell reported a folder from another host";
            canUsePromptFallback = false;
        }

        // A plain Linux session can report its foreground shell's cwd through /proc on
        // a separate SSH channel. This sends no terminal input and changes no remote file.
        if (path is null && canUsePromptFallback && !Session.Persistent &&
            session is not null && session.IsConnected)
        {
            var commandResult = await Task.Run(() => session.RunCommand(RemoteWorkingDirectoryProbe.Command));
            var probe = commandResult is { Success: true }
                ? RemoteWorkingDirectoryProbe.Parse(commandResult.Output)
                : new RemoteWorkingDirectoryProbeResult(RemoteWorkingDirectoryProbeStatus.Unavailable);
            if (probe.Status == RemoteWorkingDirectoryProbeStatus.Path)
            {
                path = probe.Path;
            }
            else if (probe.Status == RemoteWorkingDirectoryProbeStatus.NotAtShell)
            {
                canUsePromptFallback = false;
                failure = probe.Process is { } process
                    ? $"the terminal is running {process}, not waiting at a shell prompt"
                    : "the terminal is not waiting at a shell prompt";
            }
        }

        if (path is null && canUsePromptFallback && _tab.RunningCommand is null &&
            string.IsNullOrEmpty(_tab.PromptContextPlatform) &&
            _tab.PromptContext is { } promptPath &&
            (promptPath == "~" || promptPath.StartsWith("~/", StringComparison.Ordinal) ||
             promptPath.StartsWith("/", StringComparison.Ordinal)))
        {
            path = promptPath;
        }

        if (path is null)
            failure ??= "the shell did not report a current folder";

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
        var session = _resolvedSshSession ?? Session;
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
        var resolved = _resolvedSshSession is { } active
            ? new ResolvedSshCredential(active, _secret ?? "")
            : await ResolveSshCredentialAsync();
        if (resolved is null)
            throw new SshSessionException(SshFailureKind.AuthenticationFailed, "No credential provided.");
        var (session, secret) = resolved;
        _secret = secret;
        _resolvedSshSession = session;
        var sftp = new SftpSession(_knownHosts);
        try
        {
            await Task.Run(() => sftp.Connect(session, secret, PromptKeyboardInteractiveBlocking));
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
        _agentPoll?.Stop();
        _agentPoll = null;
        if (IsFilePaneOpen)
            SaveFilePaneWidth();
        _filePane?.Dispose();
        _filePane = null;
        _sshfsMount?.Dispose(); // killing the sshfs process unmounts the drive
        _sshfsMount = null;
        _rewindPlayer?.Dispose();
        _rewindPlayer = null;
        // Plan-mandated order: reader â†’ shell â†’ client (inside Stop) â†’ WebView2.
        // For local tabs, Stop kills the process tree via the job object — no orphans.
        _backend?.Stop();
        _backend = null;
        _ssh = null;
        _terminal.Dispose();
        _capture?.Dispose();
        _capture = null;
    }
}
