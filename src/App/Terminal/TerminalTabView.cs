using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Agents;
using Resesh.Core.Backend;
using Resesh.Core.Credentials;
using Resesh.Core.History;
using Resesh.Core.Identities;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Recording;
using Resesh.Core.Sftp;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Resesh.Core.Telnet;
using Resesh.Terminal;

namespace Resesh.App.Terminal;

/// <summary>
/// The content of one tab: a terminal surface plus the shell lifecycle for each target
/// kind — SSH (credential prompt, host key confirmation, connect/reconnect, teardown),
/// telnet (plain TCP connect/reconnect), serial (a COM port, in TerminalTabView.Serial.cs), or a local ConPTY process (launch, exit code,
/// restart). The live shell is an
/// <see cref="ITerminalBackend"/>; SSH-only surfaces keep a typed reference beside it.
/// </summary>
public sealed partial class TerminalTabView : Grid, IDisposable
{
    private readonly TabViewModel _tab;
    private readonly ICredentialService _credentials;
    private readonly KnownHostsStore _knownHosts;
    private readonly SshKeyStore _sshKeys;
    private readonly TmuxSlotReservations.Owner _tmuxReservation;
    private int? _resumeTmuxSlot;
    // The persistent shell a reconnect goes back to: chosen by a persistent connect (on its
    // worker thread), dropped by a connect without persistence. What the live connection is
    // attached to is the tab's RemoteSessionSlot, set when the connect succeeds.
    private bool _hasTmuxTarget;
    private int _tmuxSlot;
    private bool _connectingPersistent;
    /// <summary>What keeps this tab's persistent shell alive, chosen at each connect.</summary>
    private PersistenceBackend _persistence = PersistenceBackend.Tmux;
    private readonly TerminalSurface _terminal = TerminalSurfaceFactory.CreateLive();
    private readonly ProgressRing _spinner = new() { IsActive = false, Width = 48, Height = 48 };

    private TerminalConnectionLifetime _connection = new();
    private ITerminalBackend? _backend;
    private SshTerminalSession? _ssh; // set when _backend is the SSH implementation
    private bool _connecting;
    private bool _disposed;
    private bool _initialLaunchStarted;
    private int _captureColumns;
    private int _captureRows;
    private (int Columns, int Rows, int PixelWidth, int PixelHeight) _backendSize;
    private TerminalCapture? _capture;
    private TerminalPlayerView? _rewindPlayer;
    private bool _rewindAvailable;
    private readonly WorkingDirectoryTracker _workingDirectory = new();
    private string? _bannerPlatform; // the OS the SSH banner named, for this connection only

    // Agent awareness (Phase 6.2). One tracker per tab, fed only by this tab's own page
    // and backend — a session cannot describe another tab's agent, whatever it writes.
    private readonly AgentTracker _agent;
    private DispatcherQueueTimer? _agentPoll;
    private bool _agentPollBusy;

    // Progress from OSC 9;4 and OSC 7501 in the terminal and from file-pane transfers,
    // combined on the tab.
    private TerminalProgress _terminalProgress;
    private TerminalProgress _statusProgress;
    private string? _tmuxStatusReport;   // last report a persistent session's title carried
    private TerminalProgress _transferProgress;

    // File pane: local sessions use the Windows filesystem; SSH sessions keep the
    // resolved secret for a separate SFTP channel.
    private FilePaneView? _filePane;
    private CommunityToolkit.WinUI.Controls.GridSplitter? _paneSplitter;
    private Border? _paneSplitterLine;
    private ThemeVisualPalette _chromePalette = ThemeVisualPalette.For(App.ResolveTheme(App.Settings.Current.Theme));
    private bool _paneSplitterActive;
    private LiveLogin? _liveLogin;
    private IReadOnlyList<Session> _routeSessions = [];   // this connect's jump hosts, in dialling order
    private const double DefaultFilePaneWidth = 340;
    private HighlightsStore? _highlightPreview;
    private string? _historyDirectory;
    private string? _enterDirectory; // the folder when Enter was last pressed
    private bool _historyWriteFailed;

    private Session Session => _tab.Session;

    /// <summary>A window shortcut pressed inside this tab's terminal or rewind player:
    /// the binding id and matched chord index. The window runs it for this tab.</summary>
    public event Action<string, int>? ShortcutRequested;

    /// <summary>Raised when the user clicks the lock overlay wanting to unlock.</summary>
    public event Action? UnlockRequested;

    /// <summary>Raised (UI thread) when a connect to a session with no icon set identified
    /// the OS/vendor from the server banner. The window decides whether to persist it.</summary>
    public event Action<string>? IconSuggested;

    /// <summary>Raised (UI thread) when the login prompt changed where this session's login
    /// comes from, with the session's own password to store (or null). The window saves it.</summary>
    public event Action<Session, string?>? LoginChanged;

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

    /// <summary>True while the user is typing somewhere the window should not take focus
    /// from (the command palette or session search). A connection can finish seconds after
    /// the tab opened, and focusing the terminal then would swallow what they are typing.</summary>
    public Func<bool>? IsFocusHeldElsewhere { get; set; }

    public bool IsRecording => _capture?.IsRecording == true;
    public bool CanRecord => _capture is not null && !_disposed;
    public bool IsRewinding => _rewindPlayer is not null;
    public bool CanRewind => _rewindAvailable;
    public string? RecordingPath => _capture?.RecordingPath;
    public int? ReservedTmuxSlot => _tmuxReservation.Slot;


    public TerminalTabView(TabViewModel tab, ICredentialService credentials, KnownHostsStore knownHosts,
        SshKeyStore sshKeys, int? resumeTmuxSlot = null)
    {
        _tab = tab;
        _credentials = credentials;
        _knownHosts = knownHosts;
        _sshKeys = sshKeys;
        _tmuxReservation = App.TmuxSlots.CreateOwner(tab.Session.Id);
        _resumeTmuxSlot = resumeTmuxSlot;

        // Column 0: terminal; column 1: splitter (collapsed); column 2: file pane (width 0
        // until opened). The lock overlay spans all three.
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        SizeChanged += (_, _) => UpdateFilePaneWidthLimit();

        Children.Add(_terminal);
        Children.Add(_spinner);

        _terminal.PasteInterceptor = InterceptPaste;
        _terminal.TypedInputFilter = StopsPacedPaste;
        _terminal.InputReceived += data =>
        {
            _backend?.Write(data);
            // A command runs in the folder it was typed in. Read it at Enter: a command found
            // without shell integration is only marked once its echo settles, and a quick cd
            // and the next prompt's report may have moved the folder by then.
            if (Array.IndexOf(data, (byte)'\r') >= 0)
                _enterDirectory = CurrentHistoryDirectory();
            // Answering is what unblocks a waiting agent, so input clears a sticky badge.
            ApplyAgent(tracker => tracker.ObserveUserInput());
        };
        _terminal.Resized += (cols, rows) =>
        {
            ResizeBackend(cols, rows);
            CaptureResize(cols, rows);
        };
        _terminal.OutputObserved += (data, unixMs) => _capture?.CaptureOutput(data, unixMs);
        _terminal.KeyframeCaptured += (state, cols, rows, unixMs) =>
            _capture?.CaptureKeyframe(state, cols, rows, unixMs);
        _terminal.ReconnectRequested += () => DispatcherQueue.TryEnqueue(() => _ = ConnectAsync(isReconnect: true));
        // The page's terminal is constructed with these (init handshake), so it opens
        // with the right theme/fonts instead of restyling just after first paint.
        var initial = App.Settings.Current.WithOverrides(Session.Overrides);
        var initialTheme = App.ResolveTheme(initial.Theme);
        _terminal.SetInitialOptions(
            initial.FontSize, initial.FontFamily, initialTheme,
            initial.CopyOnSelect, initial.RightClickPaste, initial.Scrollback,
            BuildHighlightPayload(),
            fixed80Columns: Session.IsTelnet && Session.TelnetFixed80Columns);
        ApplyPromptPlatform();
        _terminal.SetHistoryCapture(initial.KeepCommandHistory);
        _terminal.SetMouseCapture(MouseCapture(initial.MouseCapture));

        // Queued, while Resized is not: a Resized handled before this runs has already sent
        // a newer size, so use the size now rather than the one Ready was raised with.
        _terminal.Ready += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;
            ResizeBackend(_terminal.Columns, _terminal.Rows);
            CaptureResize(_terminal.Columns, _terminal.Rows);
        });
        _terminal.TitleChanged += title => DispatcherQueue.TryEnqueue(() => _tab.ApplyTerminalTitle(SplitTmuxStatus(title, out _)));
        _terminal.CommandChanged += (text, exact) => DispatcherQueue.TryEnqueue(() => _tab.ApplyRunningCommand(text, exact));
        _terminal.CommandExecutionChanged += execution => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _tab.State == TabConnectionState.Connected)
                _tab.ObserveCommandExecution(execution.Id, execution.CommandLine, execution.Completed, execution.ExitCode);
        });
        _terminal.PromptContextChanged += (context, platform) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _tab.ApplyPromptContext(context, platform);
                ObservePrompt(context, platform);
                if (platform is "nokia" or "juniper" or "cisco" && Session.Icon is null)
                    IconSuggested?.Invoke(platform);
            });
        // Every folder report goes through the one tracker, which checks its host and ranks
        // it by recency; the tab's subtitle and every folder reader take it from there.
        _terminal.WorkingDirectoryReported += payload => DispatcherQueue.TryEnqueue(() =>
        {
            if (Osc7WorkingDirectoryParser.TryParse(payload, out var report) && report is not null)
                ObserveWorkingDirectory(report);
        });
        _terminal.WindowsWorkingDirectoryReported += payload => DispatcherQueue.TryEnqueue(() =>
        {
            // OSC 9;9 names a Windows folder: only this machine's shells send it.
            if (Session.IsLocal && Osc9WorkingDirectory.TryParse(payload, out var path))
                ObserveWorkingDirectory(new Osc7WorkingDirectory(Environment.MachineName, path));
        });
        _terminal.ContextReported += payload => DispatcherQueue.TryEnqueue(() =>
        {
            if (Osc3008ContextParser.TryParse(payload, out var context) && context is
                { Action: Osc3008ContextAction.Start, Type: "shell" or "command", WorkingDirectory: not null })
            {
                ObserveWorkingDirectory(new Osc7WorkingDirectory(
                    context.Hostname ?? "", context.WorkingDirectory));
            }
        });
        _terminal.ShortcutRequested += (id, chord) =>
            DispatcherQueue.TryEnqueue(() => ShortcutRequested?.Invoke(id, chord));
        _terminal.CommandsPanelOpenChanged += open => DispatcherQueue.TryEnqueue(() =>
        {
            IsCommandsPanelOpen = open;
            CommandsPanelOpenChanged?.Invoke();
        });
        // A command's folder is read when it starts (at its Enter, when there was one): by
        // the time its record arrives, a cd may already have moved the shell. The page sends
        // a finished command's record before it announces the next command, so this still
        // holds the right folder.
        _terminal.CommandObserved += _ => DispatcherQueue.TryEnqueue(() =>
        {
            _historyDirectory = _enterDirectory ?? CurrentHistoryDirectory();
            _enterDirectory = null;
        });
        _terminal.CommandRecorded += record => DispatcherQueue.TryEnqueue(() => RecordHistory(record));

        _agent = new AgentTracker(Session.Agent);
        WireAgentSignals();
        WireProgress();
        WirePaste();
        WireConnectionFollowers();

        Loaded += async (_, _) =>
        {
            if (_initialLaunchStarted || _disposed)
                return;
            _initialLaunchStarted = true;

            // Capture must exist before the backend can emit. The page reports its measured
            // size later; until then both transports start at the terminal's 80x24 default.
            EnsureCapture(_terminal.Columns, _terminal.Rows);
            if (initial.AlwaysRecord)
                TryStartAutomaticRecording();

            await Task.WhenAll(
                _terminal.InitializeAsync(),
                ConnectAsync(isReconnect: false));
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
            maximumAge: RewindLimits.Age(settings.RewindMinutes),
            maximumBytes: RewindLimits.Bytes(settings.RewindMegabytes),
            retainForRewind: _terminal.SupportsRewindCapture);
        _captureColumns = columns;
        _captureRows = rows;
        _capture.Changed += OnCaptureChanged;
        _capture.RecordingChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            SyncCaptureState();
            App.RefreshRecordingsInAllWindows(); // a recording appeared or was finished
        });
        // A write failure (a full disk, say) stops the recording but not the terminal.
        _capture.RecordingFailed += (_, message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
                _terminal.WriteNotice(message);
        });
        SyncCaptureState();
    }

    private void CaptureResize(int columns, int rows)
    {
        if (_capture is null || (_captureColumns == columns && _captureRows == rows))
            return;
        _capture.CaptureResize(columns, rows, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _captureColumns = columns;
        _captureRows = rows;
    }

    /// <summary>Tells the backend the terminal's size, unless it already has it. The size
    /// is recorded only once it reached the shell, so a resize the transport could not send
    /// (a channel not ready yet) is sent again with the next one.</summary>
    private void ResizeBackend(int columns, int rows)
    {
        var size = BackendSize(columns, rows);
        if (_backend is null || _backendSize == size)
            return;
        if (_backend.Resize(columns, rows, size.PixelWidth, size.PixelHeight))
            _backendSize = size;
    }

    /// <summary>A grid size with its text area in pixels, which SSH passes to the pty so a
    /// program asking it (TIOCGWINSZ) and one asking the terminal (CSI 14 t) agree.</summary>
    private (int Columns, int Rows, int PixelWidth, int PixelHeight) BackendSize(int columns, int rows)
    {
        var (cellWidth, cellHeight) = _terminal.CellPixelSize;
        return (columns, rows, columns * cellWidth, rows * cellHeight);
    }

    private void OnCaptureChanged()
    {
        if (_capture is null)
            return;
        var available = _capture.HasRewindData;
        if (_rewindAvailable == available)
            return;
        _rewindAvailable = available;
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
        var stopping = _capture.IsRecording;
        try
        {
            if (stopping)
            {
                _capture.StopRecording();
                return;
            }

            if (!await Dialogs.ConfirmDialog.ConfirmAsync(
                    XamlRoot,
                    "Start recording?",
                    "The recording captures all output echoed to this terminal. It can include secrets that a server prints.",
                    "Start recording",
                    defaultToPrimary: true))
                return;
            StartRecordingCore();
        }
        catch (Exception exception)
        {
            await Dialogs.MessageDialog.ShowMessageAsync(XamlRoot,
                stopping ? "Recording could not stop" : "Recording could not start", exception.Message);
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

        var player = new TerminalPlayerView(_capture, () => Session.Overrides, _terminal.ZoomedFontSize);
        player.CloseRequested += ReturnToLive;
        player.ShortcutRequested += (id, chord) => ShortcutRequested?.Invoke(id, chord);
        _rewindPlayer = player;
        Grid.SetColumnSpan(player, 3);
        Children.Add(player);
        UpdateInputEnabled();
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
        UpdateInputEnabled();
        if (_terminalInputEnabled)
            _terminal.FocusTerminal();
        SyncCaptureState();
    }

    // ---- agent awareness (Phase 6.2) ----

    /// <summary>The identity the user pinned from the tab menu; null = follow detection.</summary>
    public string? AgentOverride => _agent.ManualOverride;

    /// <summary>What the tab is showing right now (identity, attention, and its source).</summary>
    public AgentSnapshot AgentState => _agent.Current;

    /// <summary>Tab menu: pin an identity, "auto" (null), or <c>AgentIdentities.None</c>.</summary>
    public void SetAgentOverride(string? key)
    {
        ApplyAgent(tracker => tracker.SetManualOverride(key));
        StartAgentPolling(); // polling stops while agents are suppressed; "auto" resumes it
    }

    /// <summary>Re-reads the session's saved default after Session Options changed it.</summary>
    public void RefreshAgentDefault()
    {
        _agent.SessionDefault = Session.Agent;
        PushAgentState();
        StartAgentPolling();
    }

    /// <summary>
    /// Subscribes to every evidence source. The page forwards raw escape sequences, titles
    /// and marked commands; all of the mapping happens in the tracker. Detection can say
    /// WHICH agent is running; only an adapter's structured event may say it is waiting.
    /// </summary>
    private void WireAgentSignals()
    {
        _terminal.AgentOscReceived += (code, data) =>
            ApplyAgent(tracker => code == ProgramStatus.Code
                ? tracker.ObserveProgramStatus(ProgramStatus.Parse(data))
                : tracker.ObserveEvent(AgentOsc.Parse(code, data)));
        _terminal.BellReceived += () =>
            ApplyAgent(tracker => tracker.ObserveEvent(AgentOsc.Bell()));
        _terminal.TitleChanged += title =>
        {
            var clean = SplitTmuxStatus(title, out var report);
            ApplyAgent(tracker => tracker.ObserveTitle(clean));
            ObserveTmuxStatus(report, clean);
        };
        _terminal.CommandChanged += (command, _) => ApplyAgent(tracker => tracker.ObserveCommand(command));
        _terminal.CommandObserved += command => ApplyAgent(tracker => tracker.ObserveCommand(command));
        _terminal.PromptContextChanged += (_, _) => ApplyAgent(tracker => tracker.ObserveCommand(""));

        _tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.IsActive) && _tab.IsActive)
                ApplyAgent(tracker => tracker.ObserveViewed());
            else if (e.PropertyName == nameof(TabViewModel.Session))
                RefreshAgentDefault();
        };
        PushAgentState(); // a session default shows before anything has been observed
    }

    /// <summary>
    /// OSC 9;4 arrives on the same channel as agent notifications (the tracker ignores it).
    /// A program that exits or loses its connection without clearing its progress would
    /// leave the bar stuck, so a finished command, a new prompt, or leaving the connected
    /// state clears it.
    /// </summary>
    private void WireProgress()
    {
        _terminal.AgentOscReceived += (code, data) =>
        {
            if (code == 9 && TerminalProgress.ParseOsc9(data, _terminalProgress) is { } progress)
                SetTerminalProgress(progress);
            // OSC 7501 carries a percentage of its own, whoever the program is.
            else if (code == ProgramStatus.Code && ProgramStatus.Parse(data)?.AsTerminalProgress() is { } reported)
                SetStatusProgress(reported);
        };
        _terminal.CommandChanged += (command, _) =>
        {
            if (command.Length == 0)
                DispatcherQueue.TryEnqueue(ClearReportedProgress);
        };
        _terminal.PromptContextChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(ClearReportedProgress);
        _tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.State) && _tab.State != TabConnectionState.Connected)
            {
                ClearReportedProgress();
                _tmuxStatusReport = null; // a reattach repeats the report; let it count again
            }
        };
    }

    /// <summary>
    /// What follows the live connection rather than the session's settings. When the
    /// connection ends or is replaced, the file pane's SFTP connection (made with its login
    /// and route) goes with it; when one is up again, an open pane lists its folder over the
    /// new one. An edit to the session reaches what describes the session itself: its icon
    /// (the prompt-reading hint) and telnet's fixed 80-column grid.
    /// </summary>
    private void WireConnectionFollowers()
    {
        var wasConnected = false;
        _tab.PropertyChanged += (_, e) =>
        {
            if (_disposed)
                return;
            if (e.PropertyName == nameof(TabViewModel.State))
            {
                var connected = _tab.State == TabConnectionState.Connected;
                if (wasConnected && !connected)
                    _filePane?.DropConnection(_tab.State == TabConnectionState.Connecting
                        ? null
                        : "Disconnected. Reconnect the session to browse its files.");
                else if (connected && !wasConnected && IsFilePaneOpen && _filePane!.IsLoaded)
                    _ = _filePane.NavigateAsync(_filePane.CurrentPath);
                wasConnected = connected;
            }
            else if (e.PropertyName == nameof(TabViewModel.Session))
            {
                ApplyPromptPlatform();
                _terminal.SetFixed80Columns(Session.IsTelnet && Session.TelnetFixed80Columns);
            }
        };
    }

    /// <summary>How prompts are read: as the device the SSH banner named on this connection,
    /// else as the session's icon says (no hint when it names no network OS).</summary>
    private void ApplyPromptPlatform() => _terminal.SetPromptPlatform(_bannerPlatform ?? Session.Icon);

    private void ObserveWorkingDirectory(Osc7WorkingDirectory report)
    {
        _workingDirectory.ObserveReport(report);
        _tab.ApplyReportedWorkingDirectory(_workingDirectory.ReportedPath);
    }

    private void ObservePrompt(string? context, string? platform)
    {
        if (_workingDirectory.ObservePrompt(context, platform))
            _tab.ApplyReportedWorkingDirectory(_workingDirectory.ReportedPath);
    }

    private void SetTerminalProgress(TerminalProgress progress)
    {
        _terminalProgress = progress;
        PushProgress();
    }

    private void SetStatusProgress(TerminalProgress progress)
    {
        _statusProgress = progress;
        PushProgress();
    }

    private void ClearReportedProgress()
    {
        _terminalProgress = TerminalProgress.None;
        _statusProgress = TerminalProgress.None;
        PushProgress();
    }

    private void SetTransferProgress(TerminalProgress progress)
    {
        _transferProgress = progress;
        PushProgress();
    }

    private void PushProgress()
    {
        if (!_disposed)
            _tab.Progress = TerminalProgress.Combine(_terminalProgress, _statusProgress, _transferProgress);
    }

    /// <summary>A persistent session's title may carry an adapter's status report behind it
    /// (<see cref="TmuxStatus"/>). Any other session's title is passed through untouched.</summary>
    private string SplitTmuxStatus(string title, out string? report)
    {
        report = null;
        // A title can arrive while the persistent connect that produced it is still finishing.
        var persistent = _tab.IsAttachedToRemoteSession || _connectingPersistent;
        return persistent && _persistence.StatusRidesOnTitle ? TmuxStatus.SplitTitle(title, out report) : title;
    }

    /// <summary>
    /// tmux repeats the report with every title change, so only a changed one is news:
    /// observing the same "blocked" again would undo the user having answered it. A report
    /// that disappears while its program still runs was withdrawn; one that disappears
    /// because the shell is back simply went with the program, and the shell title has
    /// already retired it.
    /// </summary>
    private void ObserveTmuxStatus(string? report, string title)
    {
        if (report == _tmuxStatusReport)
            return;
        var withdrawn = report is null && !AgentDetection.IsShellTitle(title);
        _tmuxStatusReport = report;
        var status = report is not null ? ProgramStatus.ParseReport(report) : withdrawn ? ProgramStatus.Cleared : null;
        if (status is null)
            return;
        ApplyAgent(tracker => tracker.ObserveProgramStatus(status));
        if (status.AsTerminalProgress() is { } progress)
            SetStatusProgress(progress);
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
        if (_disposed || _agent.Suppressed || _backend is not LocalTerminalSession { IsRunning: true })
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
        timer.Tick += (_, _) => _ = PollLocalAgentProcessesAsync();
        return timer;
    }

    private async Task PollLocalAgentProcessesAsync()
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
            TerminalSurface.TraceHook?.Invoke($"agent poll: {ex.Message}");
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

    /// <summary>A finished connection focuses its terminal, unless the user has moved on
    /// to typing in the palette or session search meanwhile.</summary>
    private void FocusTerminalOnConnect()
    {
        if (IsFocusHeldElsewhere?.Invoke() != true)
            _terminal.FocusTerminal();
    }

    /// <summary>Moves keyboard focus to this tab's terminal when the tab is selected.</summary>
    public void FocusTerminal()
    {
        if (!_disposed && !_tab.IsLocked)
            _terminal.FocusTerminal();
    }

    /// <summary>Types text at the prompt as a paste, without pressing Enter, so the user
    /// reviews a command from history before it runs. It goes through safe paste, so a
    /// multi-line command that would run line by line (no bracketed paste) always asks
    /// first, and a slow-paste session gets it slowly.</summary>
    public bool InsertText(string text)
    {
        if (_disposed || _tab.IsLocked || _tab.State != TabConnectionState.Connected)
            return false;
        if (!PasteForReview(text))
            return false;
        _terminal.FocusTerminal();
        return true;
    }

    /// <summary>Notification activation returns to the tracked command without sending input.</summary>
    public void ScrollToCommand(long id)
    {
        if (_disposed || _tab.IsLocked)
            return;
        if (_rewindPlayer is not null)
            ReturnToLive();
        _terminal.ScrollToCommand(id);
    }

    /// <summary>Stops the terminal drawing while this tab is in the background or its
    /// window is minimized.</summary>
    public void SetRenderingPaused(bool paused)
    {
        if (_disposed)
            return;
        _terminal.SetRenderingPaused(paused);
        _rewindPlayer?.SetRenderingPaused(paused);
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

    /// <summary>Runs a terminal-scope shortcut (zoom, clear, find, ...) from the command palette.</summary>
    public void InvokeTerminalShortcut(string id)
    {
        if (!_disposed && !_tab.IsLocked)
            _terminal.InvokeShortcut(id);
    }

    /// <summary>
    /// The view's one notion of connection phase: a connect is in flight (<c>_connecting</c>),
    /// or the tab's state says the backend is live. The tab state is moved only by this view,
    /// so the two never disagree, and an inert tab (playback, app page) never connects.
    /// </summary>
    private bool CanStartConnect =>
        !_connecting && !_disposed && !_tab.IsInert && _tab.State != TabConnectionState.Connected;

    /// <summary>Kicks off a fresh connection/launch using the terminal's current size.</summary>
    public async Task ConnectAsync(bool isReconnect)
    {
        if (!CanStartConnect || !_tab.BeginConnecting())
            return;
        _connecting = true;
        _spinner.IsActive = true;
        // Keep the composition terminal visible while progress or credential UI owns input.
        UpdateInputEnabled();
        // The session's own host wins the OSC 7 host check, so a nested host that reports
        // first doesn't get latched as this tab's machine.
        _workingDirectory.SetKnownHosts([Session.Host, Session.Local is null ? null : Environment.MachineName]);
        _workingDirectory.Reset();
        _tab.ApplyReportedWorkingDirectory(null);
        // What the last connection's banner said about the host does not carry over.
        _bannerPlatform = null;
        ApplyPromptPlatform();
        _terminal.SetFixed80Columns(Session.IsTelnet && Session.TelnetFixed80Columns);

        // Tear down the previous (dead) backend so its blocked reader thread is released.
        _connection.Dispose();
        _connection = new TerminalConnectionLifetime();
        _backend = null;
        _ssh = null;
        _liveLogin = null;

        // Work that must wait until the connect has fully ended (spinner off, Reconnect
        // available again), such as asking for a replacement password.
        Func<Task>? afterConnect = null;
        try
        {
            if (Session.IsLocal)
                await LaunchLocalAsync(isReconnect);
            else if (Session.IsTelnet)
                await ConnectTelnetAsync(isReconnect);
            else if (Session.IsSerial)
                await ConnectSerialAsync(isReconnect);
            else
                afterConnect = await ConnectSshAsync(isReconnect);
        }
        finally
        {
            _connecting = false;
            if (!_disposed) _spinner.IsActive = false;
            UpdateInputEnabled();
            if (!_disposed && !_connection.Token.IsCancellationRequested && _terminalInputEnabled
                && _tab.State == TabConnectionState.Connected)
                FocusTerminalOnConnect();
        }
        if (afterConnect is not null)
            await afterConnect();
    }

    private bool _terminalInputEnabled = true;

    /// <summary>
    /// The single gate for terminal input. Input is off while a connect is in flight
    /// (credential and progress UI own the keyboard), while the tab is locked, while the
    /// rewind player covers the terminal, and once the view is disposed. A stopped tab
    /// keeps input on so Enter can reconnect or restart it.
    /// </summary>
    private void UpdateInputEnabled()
    {
        var enabled = !_disposed && !_connecting && !_tab.IsLocked && _rewindPlayer is null;
        _terminalInputEnabled = enabled;
        if (!_disposed)
            _terminal.SetInputEnabled(enabled);
    }

    /// <summary>Local lifecycle: start the ConPTY process; a later natural exit keeps the
    /// tab open, reports the exit code neutrally, and Enter/Restart relaunches.</summary>
    private async Task LaunchLocalAsync(bool isReconnect)
    {
        var connection = _connection;
        try
        {
            if (isReconnect)
                _terminal.WriteDivider(); // restart-in-place: scrollback preserved

            var local = new LocalTerminalSession();
            local.OutputReceived += data =>
            {
                if (connection.Token.IsCancellationRequested) return;
                _terminal.WriteOutput(data);
                if (!_tab.IsActive && !_tab.HasUnseenOutput)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!connection.Token.IsCancellationRequested) _tab.NotifyOutputActivity();
                    });
            };
            local.Exited += code => DispatcherQueue.TryEnqueue(() =>
            {
                if (connection.Token.IsCancellationRequested) return;
                _tab.MarkExited(code);
                _tab.ConnectionSummary = "";
                EndAgentTracking();
                _terminal.NotifyDisconnected($"Process exited with code {code}.", action: "restart", neutral: true);
            });

            var cols = _terminal.Columns;
            var rows = _terminal.Rows;
            _backendSize = BackendSize(cols, rows); // ConPTY has no pixel size to send
            await connection.StartAsync(local, () => local.Start(Session, cols, rows));
            connection.Token.ThrowIfCancellationRequested();
            if (local.ShellIntegrationMessage is { } integrationMessage)
                _terminal.WriteNotice(integrationMessage);

            _backend = local;
            ResizeBackend(_terminal.Columns, _terminal.Rows);
            _tab.MarkConnected();
            _tab.ConnectionSummary = $"pid {local.ProcessId}";
            _terminal.NotifyConnected();
            FocusTerminalOnConnect();
            StartAgentPolling();
        }
        catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
        catch (LocalSessionException ex)
        {
            if (connection.Token.IsCancellationRequested) return;
            // Launch failure (unlike a normal exit) is an error state — red dot, warning text.
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected(ex.Message, action: "restart");
        }
        catch (Exception ex)
        {
            if (connection.Token.IsCancellationRequested) return;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected($"Unexpected error: {ex.Message}", action: "restart");
        }
    }

    /// <summary>Telnet lifecycle: no credentials or host keys — the server's own login
    /// prompt (if any) arrives as terminal output.</summary>
    private async Task ConnectTelnetAsync(bool isReconnect)
    {
        var connection = _connection;
        var credentialDeclined = false;
        try
        {
            if (isReconnect)
                _terminal.WriteDivider();
            // A jump host carries the telnet connection inside SSH as far as the jump.
            // Its login is asked for when the connect reaches it, as for an SSH session.
            var jumpSessions = JumpRoute.Resolve(Session, App.Store.Find);
            _routeSessions = jumpSessions;
            var jumps = new List<SshHop>();
            _terminal.WriteNotice($"Connecting to {Session.Host}:{SessionEndpoint.EffectivePort(Session)} over telnet (unencrypted){RouteDescription(Session with { Proxy = null })} …");

            var telnet = new TelnetTerminalSession();
            telnet.OutputReceived += data =>
            {
                if (connection.Token.IsCancellationRequested) return;
                _terminal.WriteOutput(data);
                if (!_tab.IsActive && !_tab.HasUnseenOutput)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!connection.Token.IsCancellationRequested) _tab.NotifyOutputActivity();
                    });
            };
            // The server closing and a jump host dying can both be reported; the first says it.
            var ended = false;
            string? jumpLost = null;
            void EndTelnet(string message)
            {
                if (ended || connection.Token.IsCancellationRequested) return;
                ended = true;
                _tab.MarkDisconnected();
                _tab.ConnectionSummary = "";
                EndAgentTracking();
                _terminal.NotifyDisconnected(message);
            }
            telnet.Closed += ex => DispatcherQueue.TryEnqueue(() =>
                EndTelnet(ex is null ? "Connection closed by the remote host." : $"Connection lost: {ex.Message}"));

            var cols = _terminal.Columns;
            var rows = _terminal.Rows;
            _backendSize = BackendSize(cols, rows); // telnet's window size has no pixels
            var dialling = new Dialling(jumpSessions);
            await connection.StartAsync(telnet, () =>
            {
                var via = jumpSessions.Count == 0 ? null : SshJumpChain.Open(
                    jumpSessions.Select(jump => new SshHop(jump, null)).ToList(), Session.Host, SessionEndpoint.EffectivePort(Session), _knownHosts,
                    info => ConfirmHostKeyBlocking(info, dialling, connection.Token),
                    prompts => PromptKeyboardInteractiveBlocking(prompts, dialling, connection.Token),
                    connection.Token, host => dialling.Host = host,
                    hop =>
                    {
                        var login = ResolveJumpLoginBlocking(hop, jumps, connection.Token);
                        credentialDeclined = login is null;
                        return login;
                    });
                // TCP keepalive on telnet's socket only reaches the loopback forward, so a
                // jump that dies is watched for on the jump itself.
                if (via is not null)
                    via.Lost += reason => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (connection.Token.IsCancellationRequested) return;
                        if (!ReferenceEquals(_backend, telnet))
                        {
                            jumpLost = reason; // still connecting: reported once that ends
                            return;
                        }
                        telnet.Dispose(); // raises no Closed; this reports the end instead
                        EndTelnet($"Connection lost: {reason}");
                    });
                telnet.Connect(via?.Host ?? Session.Host, via?.Port ?? SessionEndpoint.EffectivePort(Session), Session.TerminalType, cols, rows,
                    connection.Token, Session.TelnetEncoding, Session.TelnetReport80x25, via);
            });
            connection.Token.ThrowIfCancellationRequested();

            if (jumpLost is not null)
            {
                telnet.Dispose();
                _tab.MarkDisconnected();
                _terminal.NotifyDisconnected($"Connection lost: {jumpLost}");
                return;
            }
            _backend = telnet;
            ResizeBackend(_terminal.Columns, _terminal.Rows);
            _tab.MarkConnected();
            _tab.ConnectionSummary = jumpSessions.Count == 0
                ? $"telnet • {telnet.RemoteEndPoint} • unencrypted"
                : $"telnet • {Session.Host}:{SessionEndpoint.EffectivePort(Session)} via {jumpSessions[^1].Name} • unencrypted past the jump host";
            _terminal.NotifyConnected();
            FocusTerminalOnConnect();
        }
        catch (Exception ex) when (ex is TelnetSessionException or SshSessionException)
        {
            if (connection.Token.IsCancellationRequested) return;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (connection.Token.IsCancellationRequested) return;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected(credentialDeclined ? "No credential provided." : "Connection cancelled.");
        }
        catch (Exception ex)
        {
            if (connection.Token.IsCancellationRequested) return;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<Func<Task>?> ConnectSshAsync(bool isReconnect)
    {
        var connection = _connection;
        Identity? authenticatingWith = null;
        var credentialDeclined = false;
        var attached = false;
        // The session as this connect found it. An edit made while it is under way (Persistent
        // turned on, say) applies to the next connect, not to half of this one.
        var target = Session;
        // Resuming a chosen persistent shell (Manage Remote Sessions) attaches to it even when
        // the session no longer starts persistent shells by itself.
        if (_resumeTmuxSlot is not null && !target.Persistent)
            target = target with { Persistent = true };
        var persistent = target.Persistent;
        if (!persistent)
        {
            // Not going back to a persistent shell: let another tab have it.
            _hasTmuxTarget = false;
            _tmuxReservation.Release();
        }
        try
        {
            // Before any jump host is dialled; the login resolved for each host is checked again.
            if (MissingUsername(target, App.Logins.Resolve(target)) is { } noUsername)
            {
                _tab.MarkDisconnected();
                _terminal.NotifyDisconnected(noUsername);
                return null;
            }

            // Logins are asked for hop by hop as the connect reaches each host, jump hosts
            // first, so a jump that fails stops before anything is typed for the next one.
            var jumpSessions = JumpRoute.Resolve(target, App.Store.Find);
            _routeSessions = jumpSessions;
            var jumps = new List<SshHop>();
            string? proxyPassword = null;
            ResolvedSshCredential? targetLogin = null;
            if (isReconnect)
                _terminal.WriteDivider();
            SshHop? ResolveLogin(SshHop hop)
            {
                var login = hop.Session.Id != target.Id
                    ? ResolveJumpLoginBlocking(hop, jumps, connection.Token)
                    : RunOnUiBlocking(async () =>
                    {
                        var resolved = await ResolveSshCredentialAsync(target, connection.Token);
                        connection.Token.ThrowIfCancellationRequested();
                        if (resolved is null)
                            return null;
                        targetLogin = resolved;
                        proxyPassword = ProxyPassword(target);
                        authenticatingWith = resolved.IdentitySecret;
                        _terminal.WriteNotice(
                            $"Connecting to {resolved.Session.Username}@{target.Host}:{SessionEndpoint.EffectivePort(target)}{RouteDescription(target)} …");
                        return new SshHop(resolved.Session, resolved.Secret, proxyPassword);
                    }, connection.Token);
                credentialDeclined = login is null;
                return login;
            }

            // Connect and its integration diagnostics run on the backend worker.
            // Terminal notices must be delivered on this view's UI thread.
            var uiDispatcher = DispatcherQueue;
            var dialling = new Dialling(jumpSessions);
            var session = new SshTerminalSession(_knownHosts)
            {
                HostKeyDecision = info => ConfirmHostKeyBlocking(info, dialling, connection.Token),
                Connecting = host => dialling.Host = host,
                ShellIntegrationNotice = message => uiDispatcher.TryEnqueue(() =>
                {
                    if (!_disposed && !connection.Token.IsCancellationRequested)
                        _terminal.WriteNotice(message);
                }),
            };
            session.TunnelChanged += status => uiDispatcher.TryEnqueue(() =>
            {
                if (_disposed || connection.Token.IsCancellationRequested) return;
                // Tunnels saved with the session start before its shell; say how each went.
                if (!ReferenceEquals(_ssh, session))
                    _terminal.WriteNotice(TunnelNotice(status));
                else
                    UpdateSshSummary();
                TunnelsChanged?.Invoke();
            });
            session.OutputReceived += data =>
            {
                if (connection.Token.IsCancellationRequested) return;
                _terminal.WriteOutput(data);
                // Benign cross-thread reads: worst case is one redundant enqueue.
                if (!_tab.IsActive && !_tab.HasUnseenOutput)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!connection.Token.IsCancellationRequested) _tab.NotifyOutputActivity();
                    });
            };
            session.Closed += ex => DispatcherQueue.TryEnqueue(() =>
            {
                if (connection.Token.IsCancellationRequested) return;
                _tab.MarkDisconnected();
                _tab.ConnectionSummary = "";
                EndAgentTracking();
                _terminal.NotifyDisconnected(ex is null ? "Connection closed." : $"Connection lost: {ex.Message}");
            });

            var cols = _terminal.Columns;
            var rows = _terminal.Rows;
            var size = _backendSize = BackendSize(cols, rows);
            Func<SshTerminalSession, string?>? bootstrapFactory = persistent
                ? connected => SelectTmuxBootstrapBlocking(connected, isReconnect, connection.Token)
                : null;
            _connectingPersistent = persistent;
            await connection.StartAsync(session, () => session.Connect(
                target, null, target.TerminalType, cols, rows,
                bootstrapCommandFactory: bootstrapFactory,
                interactiveResponder: prompts => PromptKeyboardInteractiveBlocking(prompts, dialling, connection.Token),
                cancellationToken: connection.Token,
                jumps: jumpSessions.Select(jump => new SshHop(jump, null)).ToList(),
                tunnels: target.Tunnels,
                resolveLogin: ResolveLogin,
                pixelWidth: size.PixelWidth,
                pixelHeight: size.PixelHeight));
            connection.Token.ThrowIfCancellationRequested();
            attached = true;
            _resumeTmuxSlot = null;
            // Published with the Connected state below, all at once: the side connections
            // that reuse it never see a new login with the old route.
            var live = _liveLogin = new LiveLogin(
                targetLogin?.Session ?? target, targetLogin?.Secret ?? "", new ResolvedRoute(jumps, proxyPassword));

            _backend = session;
            _ssh = session;
            ResizeBackend(_terminal.Columns, _terminal.Rows);
            // The one record of what this connection reached and which persistent shell it
            // holds; leaving the connected state clears both.
            _tab.MarkConnected(live.Session, persistent && _hasTmuxTarget ? _tmuxSlot : null);
            UpdateSshSummary();
            TunnelsChanged?.Invoke();
            _terminal.NotifyConnected();
            FocusTerminalOnConnect();

            // Icon auto-suggest: only for sessions where the user never chose anything
            // (null; an explicit "none" also blocks this).
            if (SessionIcons.SuggestFromBanner(session.ServerBanner) is { } suggestedIcon)
            {
                _bannerPlatform = suggestedIcon;
                ApplyPromptPlatform();
                if (Session.Icon is null)
                    IconSuggested?.Invoke(suggestedIcon);
            }
        }
        catch (SshSessionException ex)
        {
            if (connection.Token.IsCancellationRequested) return null;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected(ex.Message);
            // A jump host that refused its login says nothing about this session's identity.
            // The offer waits until ConnectAsync has ended, so Reconnect works while it shows;
            // a reconnect cancels this connection's token, which takes the offer down.
            if (ex.Kind == SshFailureKind.AuthenticationFailed && ex.JumpSessionId is null && authenticatingWith is { } identity)
                return async () =>
                {
                    try { await OfferIdentityPasswordUpdateAsync(identity, connection.Token); }
                    catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        if (!connection.Token.IsCancellationRequested)
                            App.ReportRecoverableError(exception);
                    }
                };
        }
        catch (OperationCanceledException)
        {
            if (connection.Token.IsCancellationRequested) return null;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected(credentialDeclined ? "No credential provided." : "Connection cancelled.");
        }
        catch (Exception ex)
        {
            if (connection.Token.IsCancellationRequested) return null;
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected($"Unexpected error: {ex.Message}");
        }
        finally
        {
            _connectingPersistent = false;
            if (!attached)
                _tmuxReservation.Release();
        }
        return null;
    }

    /// <summary>A stored or remembered identity password was rejected: offer to replace it
    /// for every session using the identity, then reconnect with the new one.</summary>
    private async Task OfferIdentityPasswordUpdateAsync(Identity identity, CancellationToken token)
    {
        if (_disposed || XamlRoot is null)
            return;
        var users = App.Logins.UsersOf(identity.Id, App.Store.Sessions).Count;
        var usedBy = users == 1 ? "used by 1 session" : $"used by {users} sessions";
        var remembered = identity.Proof == IdentityProof.Ask;
        var result = await ConnectDialogs.PromptCredentialAsync(
            XamlRoot,
            $"Update the password for {identity.Name}",
            $"The server rejected the {(remembered ? "remembered" : "saved")} password. "
                + $"New password for {identity.Username} ({usedBy})",
            saveLabel: remembered ? null : "Save for every session using this identity",
            primaryText: "Update and reconnect",
            cancellationToken: token);
        if (result is not { } update || _disposed || token.IsCancellationRequested)
            return;
        if (remembered)
            App.AskedSecrets.Set(identity.Id, update.Secret);
        else if (update.Save && update.Secret.Length > 0)
            _credentials.WriteIdentity(identity.Id, update.Secret);
        else
            _pendingIdentitySecret = (identity.Id, update.Secret);
        DispatcherQueue.TryEnqueue(() => _ = ConnectAsync(isReconnect: true));
    }

    /// <summary>Runs after SSH authentication and before the interactive shell opens.</summary>
    private string SelectTmuxBootstrapBlocking(SshTerminalSession connected, bool isReconnect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _persistence = ResolvePersistenceBlocking(connected, mayUpload: true, token);
        if (!isReconnect && !_persistence.StatusRidesOnTitle)
            NoteLeftoverTmuxShells(connected, token);
        var result = connected.RunCommand(_persistence.ManagementCommand());
        token.ThrowIfCancellationRequested();
        var remoteSessions = result is { Success: true }
            ? _persistence.ParseManagedSessions(result.Output, Session.Id)
            : [];

        if (_resumeTmuxSlot is { } requestedSlot)
        {
            if (!remoteSessions.Any(remote => remote.Slot == requestedSlot))
                throw new InvalidOperationException("That remote session is no longer available. Refresh the remote sessions list.");
            ClaimSlot(requestedSlot);
            return _persistence.ResumeCommand(Session.Id, requestedSlot);
        }

        // A reconnect may retain its target only while no other tab has claimed it.
        if (isReconnect && _hasTmuxTarget)
        {
            ClaimSlot(_tmuxSlot);
            return BuildTmuxBootstrap(_tmuxSlot);
        }

        var plan = _tmuxReservation.PlanConnect(remoteSessions, Session.DetachedSessions, token);
        if (plan is TmuxConnectPlan.Choose choose)
        {
            var choice = SelectTmuxSessionBlocking(choose, token);
            // The picker can outlive another window's connect. Never trust its old slot list.
            if (choice is TmuxChoice.Resume resume)
            {
                ClaimSlot(resume.Slot);
                plan = new TmuxConnectPlan.Resume(resume.Slot);
            }
            else
            {
                var action = choice is TmuxChoice.EndDetachedAndStartNew
                    ? DetachedSessionAction.EndDetachedAndStartNew
                    : DetachedSessionAction.StartNew;
                plan = _tmuxReservation.PlanConnect(remoteSessions, action, token);
            }
        }

        if (plan is TmuxConnectPlan.Resume resumePlan)
        {
            _tmuxSlot = resumePlan.Slot;
            _hasTmuxTarget = true;
            return _persistence.ResumeCommand(Session.Id, resumePlan.Slot);
        }

        var start = (TmuxConnectPlan.StartNew)plan;
        var selectedSlot = start.Slot;
        if (start.EndFirst.Count > 0)
        {
            foreach (var slot in start.EndFirst)
            {
                token.ThrowIfCancellationRequested();
                // A deletion is itself a temporary claim, held until its exec completes.
                // Another tab may have claimed this shell since the plan/picker was built.
                using var ending = App.TmuxSlots.CreateOwner(Session.Id);
                if (ending.TryReserve(slot, token))
                    connected.TryRunCommand(_persistence.KillCommand(Session.Id, slot));
            }
            token.ThrowIfCancellationRequested();
            // Trust the server, not the kill's exit status. When the listing fails for
            // another reason, keep avoiding every earlier slot so the new shell can never
            // attach to a survivor.
            var after = connected.RunCommand(_persistence.ManagementCommand());
            token.ThrowIfCancellationRequested();
            if (after is { Success: true })
                remoteSessions = _persistence.ParseManagedSessions(after.Output, Session.Id);
            else if (after is not null && _persistence.IsAbsent(after))
                remoteSessions = [];
            selectedSlot = ((TmuxConnectPlan.StartNew)_tmuxReservation.PlanConnect(
                remoteSessions, DetachedSessionAction.StartNew, token)).Slot;
        }
        _tmuxSlot = selectedSlot;
        _hasTmuxTarget = true;
        return BuildTmuxBootstrap(selectedSlot);

        void ClaimSlot(int slot)
        {
            if (!_tmuxReservation.TryReserve(slot, token))
                throw new InvalidOperationException("That remote session was claimed by another tab. Refresh the remote sessions list.");
            _tmuxSlot = slot;
            _hasTmuxTarget = true;
        }

        string BuildTmuxBootstrap(int slot)
        {
            if (remoteSessions.Any(remote => remote.Slot == slot))
                return _persistence.ResumeCommand(Session.Id, slot);
            return connected.CreatePersistentBootstrap(Session.Id, slot, Session.ShellIntegration, token, _persistence);
        }
    }

    /// <summary>
    /// Picks what keeps this session's shells alive on the host behind
    /// <paramref name="connected"/>: the session agent when the saved session asks for it
    /// and it is there, or can be put there; tmux otherwise. Says why in the terminal when
    /// the agent was asked for and is not used. Runs on an SSH worker.
    /// </summary>
    internal PersistenceBackend ResolvePersistenceBlocking(SshTerminalSession connected, bool mayUpload, CancellationToken token)
    {
        if (!Session.SessionAgent)
            return PersistenceBackend.Tmux;
        if (App.AgentPackage is not { } package)
            return Fallback("this copy of Resesh was built without it");

        var timeout = TimeSpan.FromSeconds(60);
        var probed = connected.RunCommand("sh -s", System.Text.Encoding.UTF8.GetBytes(AgentInstall.ProbeScript(package.Version)), timeout);
        token.ThrowIfCancellationRequested();
        if (AgentInstall.ParseProbe(probed?.Output ?? "") is not { } probe)
            return Fallback("the host did not answer the check for it");

        switch (AgentInstall.Plan(probe, package, mayUpload))
        {
            case AgentInstallPlan.Ready ready:
                return PersistenceBackend.Agent(ready.RemotePath);
            case AgentInstallPlan.Upload upload:
                if (AgentPackage.ReadVerified(upload.Binary) is not { } bytes)
                    return Fallback("its file in the Resesh folder is missing or damaged");
                Notice($"Uploading the Resesh session agent ({upload.Architecture}, {bytes.Length / 1024} KB) …");
                var outcome = AgentInstall.ParseInstall(
                    connected.RunCommand(AgentInstall.InstallCommand(package.Version, upload.Binary), bytes, timeout));
                token.ThrowIfCancellationRequested();
                return outcome switch
                {
                    AgentInstallOutcome.Installed => PersistenceBackend.Agent(AgentInstall.RemotePath(package.Version)),
                    AgentInstallOutcome.CannotRun => Fallback("the host would not run it; the home directory may be mounted noexec"),
                    AgentInstallOutcome.Mismatch => Fallback("the upload did not arrive intact"),
                    _ => Fallback("the upload failed"),
                };
            case AgentInstallPlan.Unavailable unavailable:
                return Fallback(unavailable.Reason);
            default:
                return PersistenceBackend.Tmux;
        }

        PersistenceBackend Fallback(string reason)
        {
            // In the window, not the terminal: the shell start that follows wipes the screen.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && !token.IsCancellationRequested)
                    App.WindowFor(_tab)?.ShowOperationNotice(
                        $"{Session.Name}: using tmux, not the Resesh agent",
                        char.ToUpperInvariant(reason[0]) + reason[1..] + ".");
            });
            return PersistenceBackend.Tmux;
        }

        void Notice(string message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && !token.IsCancellationRequested)
                _terminal.WriteNotice(message);
        });
    }

    /// <summary>
    /// A session moved from tmux to the agent leaves its tmux shells running, and the
    /// agent's lists do not show them. Say so, and how to reach them. Costs one command on
    /// a first connect; a host with no tmux server answers at once.
    /// </summary>
    private void NoteLeftoverTmuxShells(SshTerminalSession connected, CancellationToken token)
    {
        var result = connected.RunCommand(TmuxPersistence.ManagementCommand());
        token.ThrowIfCancellationRequested();
        if (result is not { Success: true })
            return;
        var count = TmuxPersistence.ParseManagedSessions(result.Output, Session.Id).Count;
        if (count == 0)
            return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && !token.IsCancellationRequested)
                App.WindowFor(_tab)?.ShowOperationNotice(
                    count == 1 ? $"{Session.Name}: 1 tmux shell is still running" : $"{Session.Name}: {count} tmux shells are still running",
                    "Started before this session moved to the Resesh agent. Set \"Kept alive by\" to tmux to reach " + (count == 1 ? "it." : "them."));
        });
    }

    /// <summary>Marshals tmux selection onto the UI thread while the SSH worker waits.</summary>
    private TmuxChoice SelectTmuxSessionBlocking(TmuxConnectPlan.Choose choose, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<TmuxChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                tcs.TrySetResult(await ConnectDialogs.SelectTmuxSessionAsync(XamlRoot, Session.Name, choose.Sessions, token));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            throw new OperationCanceledException("The tmux session selector could not open.");
        }
        return tcs.Task.WaitAsync(token).GetAwaiter().GetResult()
            ?? throw new OperationCanceledException("tmux session selection was cancelled.");
    }

    /// <summary>The session to connect as (login applied), its secret, and the identity
    /// whose stored or remembered password supplied it (so a rejection can offer an update).</summary>
    private sealed record ResolvedSshCredential(Session Session, string Secret, Identity? IdentitySecret = null);

    // ---- tunnels ----

    /// <summary>Raised (UI thread) when a tunnel on this tab's connection starts, fails or stops.</summary>
    public event Action? TunnelsChanged;

    /// <summary>The tunnels on the live SSH connection; empty when there is none.</summary>
    public IReadOnlyList<TunnelStatus> Tunnels => _ssh?.Tunnels ?? [];

    /// <summary>True while this tab has an SSH connection that can carry tunnels.</summary>
    public bool CanTunnel => _ssh is { IsConnected: true };

    /// <summary>Starts a tunnel on the live connection. It lasts until the connection ends
    /// unless the caller also saves it with the session.</summary>
    public async Task<TunnelStatus> StartTunnelAsync(Tunnel tunnel)
    {
        var ssh = _ssh;
        return ssh is null
            ? new TunnelStatus(tunnel, TunnelState.Failed, "The session is not connected.")
            : await Task.Run(() => ssh.StartTunnel(tunnel));
    }

    public async Task StopTunnelAsync(Guid tunnelId)
    {
        if (_ssh is { } ssh)
            await Task.Run(() => ssh.StopTunnel(tunnelId));
    }

    /// <summary>Stops a tunnel and drops it from <see cref="Tunnels"/>.</summary>
    public async Task RemoveTunnelAsync(Guid tunnelId)
    {
        if (_ssh is { } ssh)
            await Task.Run(() => ssh.RemoveTunnel(tunnelId));
        TunnelsChanged?.Invoke();
    }

    private static string TunnelNotice(TunnelStatus status) => status.State switch
    {
        TunnelState.Failed => $"Tunnel failed: {status.Tunnel.Describe()}. {status.Error}",
        TunnelState.Stopped => $"Tunnel closed: {status.Tunnel.Describe()}",
        _ => $"Tunnel open: {status.Tunnel.Describe()}",
    };

    /// <summary>Cipher, host key, and how the tunnels are doing, for the status bar; every
    /// negotiated algorithm for its tooltip.</summary>
    private void UpdateSshSummary()
    {
        if (_ssh is not { } session)
            return;
        var tunnels = session.Tunnels;
        var open = tunnels.Count(tunnel => tunnel.State == TunnelState.Listening);
        var failed = tunnels.Count(tunnel => tunnel.State == TunnelState.Failed);
        var tunnelSummary = (open, failed) switch
        {
            (0, 0) => null,
            (_, 0) => open == 1 ? "1 tunnel" : $"{open} tunnels",
            (0, _) => failed == 1 ? "1 tunnel failed" : $"{failed} tunnels failed",
            _ => $"{open} of {open + failed} tunnels open",
        };
        var negotiated = session.Negotiated;
        _tab.ConnectionDetails = negotiated?.Details(session.HostKeyFingerprint) ?? "";
        var legacy = negotiated?.UsesLegacy == true ? "legacy algorithms" : null;
        _tab.ConnectionSummary = string.Join(" • ",
            new[] { session.Encryption, session.HostKeyFingerprint, legacy, tunnelSummary }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>How a connect reaches its host: the jump hosts in the order they are
    /// dialled (logins applied), and the password for the session's own proxy.</summary>
    private sealed record ResolvedRoute(IReadOnlyList<SshHop> Jumps, string? ProxyPassword)
    {
        public IReadOnlyList<Session> Sessions => Jumps.Select(jump => jump.Session).ToList();
    }

    /// <summary>The login (applied to the session, as <see cref="TabViewModel.ConnectedSession"/>
    /// holds it), secret and route the live SSH connection was made with.</summary>
    private sealed record LiveLogin(Session Session, string Secret, ResolvedRoute Route);

    /// <summary>What the file pane, Manage Remote Sessions and the Explorer mount connect
    /// with: the live connection's login and route, and only while the tab is Connected.
    /// During a reconnect the previous one no longer describes anything that is up.</summary>
    private LiveLogin? CurrentLogin => _tab.State == TabConnectionState.Connected ? _liveLogin : null;

    /// <summary>The host one connection is dialling, for its prompts' titles, and the jump
    /// hosts on its route. Each connection (terminal, file pane, management) has its own, so
    /// a prompt never names the host another connection last dialled.</summary>
    private sealed class Dialling(IReadOnlyList<Session> route)
    {
        private volatile Session? _host;

        public IReadOnlyList<Session> Route { get; } = route;

        public Session? Host
        {
            get => _host;
            set => _host = value;
        }
    }

    /// <summary>Resolves every jump host's login the way the session's own is resolved.
    /// Null when a prompt was cancelled; throws when the chain itself is broken.</summary>
    private async Task<ResolvedRoute?> ResolveRouteAsync(CancellationToken token)
    {
        var jumps = new List<SshHop>();
        foreach (var jump in JumpRoute.Resolve(Session, App.Store.Find))
        {
            var resolved = await ResolveSshCredentialAsync(jump, token);
            token.ThrowIfCancellationRequested();
            if (resolved is null)
                return null;
            jumps.Add(new SshHop(resolved.Session, resolved.Secret, ProxyPassword(jump)));
        }
        return new ResolvedRoute(jumps, ProxyPassword(Session));
    }

    /// <summary>The saved password for the proxy the connection really uses, when it sends a login.</summary>
    private string? ProxyPassword(Session session) =>
        JumpRoute.ProxyUsername(session) is not null ? _credentials.ReadProxy(session.Id) : null;

    /// <summary>" via bastion → core", or the proxy, for the connecting notice; empty when direct.</summary>
    private static string RouteDescription(Session session)
    {
        try
        {
            var jumps = JumpRoute.Resolve(session, App.Store.Find);
            if (jumps.Count > 0)
                return " via " + string.Join(" → ", jumps.Select(jump => jump.Name));
        }
        catch (SshSessionException)
        {
            return "";
        }
        return JumpRoute.ActiveProxy(session) is { } proxy ? $" via {proxy.Label}" : "";
    }

    /// <summary>A password typed after the session's own host rejected an identity's password,
    /// but not saved: used once, by the next login to that host (never a jump host's, which
    /// resolves first and may use the same identity).</summary>
    private (Guid IdentityId, string Secret)? _pendingIdentitySecret;

    /// <summary>Why <paramref name="host"/> cannot log in for want of a username, or null when
    /// <paramref name="login"/> has one or will ask for one. Every connection (terminal, file
    /// pane, management, jump hosts) checks each host through this.</summary>
    private string? MissingUsername(Session host, ResolvedLogin login)
    {
        if (login.Source == LoginSource.None || !string.IsNullOrWhiteSpace(login.Username))
            return null;
        var jump = host.Id != Session.Id;
        if (login.Source == LoginSource.Session)
            return jump
                ? $"The jump host {host.Name} has no username. Edit it to add one."
                : "No username set for this session. Right-click it in the tree and choose Edit… to add one.";
        var usedBy = jump ? $" (used by the jump host {host.Name})" : "";
        return $"The identity '{login.Identity!.Name}'{usedBy} has no username. "
            + $"Edit it in {SettingsCatalog.PathTo(SettingsSection.Identities)}.";
    }

    /// <summary>Resolves the login (own, chosen identity, folder identity, or a prompt) and its secret.</summary>
    private async Task<ResolvedSshCredential?> ResolveSshCredentialAsync(Session target, CancellationToken token)
    {
        var login = App.Logins.Resolve(target);
        if (MissingUsername(target, login) is { } noUsername)
            throw new SshSessionException(SshFailureKind.Other, noUsername)
            {
                JumpSessionId = target.Id != Session.Id ? target.Id : null,
            };
        if (login.Source == LoginSource.None)
            return await PromptLoginAsync(target, login, token);
        if (login.Source == LoginSource.Session)
            return await ResolveOwnCredentialAsync(target, token);

        var identity = login.Identity!;
        var session = login.ApplyTo(target);
        if (identity.Proof == IdentityProof.Key)
            return await ResolvePrivateKeyAsync(session, sessionLogin: false, token);

        if (target.Id == Session.Id && _pendingIdentitySecret is { } pending && pending.IdentityId == identity.Id)
        {
            _pendingIdentitySecret = null;
            return new ResolvedSshCredential(session, pending.Secret);
        }
        var ask = identity.Proof == IdentityProof.Ask;
        var remembered = ask ? App.AskedSecrets.Get(identity.Id) : _credentials.ReadIdentity(identity.Id);
        if (!string.IsNullOrEmpty(remembered))
            return new ResolvedSshCredential(session, remembered, identity);

        var result = await ConnectDialogs.PromptCredentialAsync(
            XamlRoot,
            PromptTitle(target, "Connect to"),
            $"Password for {session.Username}@{target.Host} ({identity.Name}){PromptPurpose(target)}",
            saveLabel: ask ? null : "Save for every session using this identity",
            cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (result is not { } cred)
            return null;
        if (ask)
            App.AskedSecrets.Set(identity.Id, cred.Secret);
        else if (cred.Save && cred.Secret.Length > 0)
            _credentials.WriteIdentity(identity.Id, cred.Secret);
        return new ResolvedSshCredential(session, cred.Secret);
    }

    /// <summary>The session's own credential: a session password or a registered key.</summary>
    private async Task<ResolvedSshCredential?> ResolveOwnCredentialAsync(Session target, CancellationToken token)
    {
        if (target.AuthMethod == AuthMethod.None)
            return new ResolvedSshCredential(target, "");

        if (target.AuthMethod == AuthMethod.PrivateKey)
            return await ResolvePrivateKeyAsync(target, sessionLogin: true, token);

        var stored = _credentials.Read(target.Id);
        if (!string.IsNullOrEmpty(stored))
            return new ResolvedSshCredential(target, stored);

        var result = await ConnectDialogs.PromptCredentialAsync(
            XamlRoot,
            PromptTitle(target, "Connect to"),
            $"Password for {target.Username}@{target.Host}{PromptPurpose(target)}",
            cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (result is not { } cred)
            return null;

        if (cred.Save && cred.Secret.Length > 0)
            _credentials.Write(target.Id, cred.Secret);
        return new ResolvedSshCredential(target, cred.Secret);
    }

    /// <summary>No login applies: ask for a username and password and where to remember
    /// them — every session in the folder (a new folder identity), this session, or nowhere.</summary>
    private async Task<ResolvedSshCredential?> PromptLoginAsync(Session target, ResolvedLogin login, CancellationToken token)
    {
        var saved = App.Store.Find(target.Id) is not null;
        var answer = await ConnectDialogs.PromptLoginAsync(
            XamlRoot,
            target,
            login.Username,
            canRemember: saved,
            identityMissing: login.IdentityMissing,
            cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (answer is null)
            return null;

        var session = target with
        {
            Username = answer.Username,
            AuthMethod = AuthMethod.Password,
            PrivateKeyId = null,
            PrivateKeyPath = null,
        };
        switch (answer.Remember)
        {
            case LoginRemember.Folder:
            {
                var identity = App.Identities.Add(new Identity
                {
                    Name = LoginDialog.SuggestedIdentityName(answer.Username, target.FolderPath),
                    Username = answer.Username,
                    Proof = answer.SavePassword ? IdentityProof.Password : IdentityProof.Ask,
                });
                if (answer.SavePassword)
                    _credentials.WriteIdentity(identity.Id, answer.Password);
                else
                    App.AskedSecrets.Set(identity.Id, answer.Password);
                App.Store.SetFolderIdentity(target.FolderPath, identity.Id);
                // A session pointing at a deleted identity goes back to inheriting the new one.
                if (target.Login != SessionLogin.Inherit)
                    LoginChanged?.Invoke(target with { IdentityId = null, InheritLogin = true }, null);
                break;
            }
            case LoginRemember.Session:
                LoginChanged?.Invoke(session with { IdentityId = null, InheritLogin = false },
                    answer.SavePassword ? answer.Password : null);
                break;
        }
        return new ResolvedSshCredential(session, answer.Password);
    }

    /// <summary>A registered key's login. <paramref name="sessionLogin"/>: the session's own
    /// login, whose passphrase a migrated session may still keep under its own credential
    /// (DECISIONS 2026-08-18); an identity's key never falls back to the session password.</summary>
    private async Task<ResolvedSshCredential?> ResolvePrivateKeyAsync(Session session, bool sessionLogin, CancellationToken token)
    {
        if (session.PrivateKeyId is not { } keyId || _sshKeys.Find(keyId) is not { } key)
            throw new InvalidOperationException("The session does not have a registered SSH key.");
        if (!key.IsAvailable)
            throw new FileNotFoundException(
                $"The SSH key '{key.Name}' is unavailable. Locate it in {SettingsCatalog.PathTo(SettingsSection.Identities)}.", key.Path);

        var secret = key.IsEncrypted == true
            ? _credentials.ReadKey(keyId) ?? (sessionLogin ? _credentials.Read(session.Id) : null)
            : null;

        SshKeyReference validated;
        if (key.IsEncrypted == true && string.IsNullOrEmpty(secret))
        {
            var prompted = await PromptAndValidateKeyAsync(key, token);
            if (prompted is null)
                return null;
            (validated, secret) = prompted.Value;
        }
        else
        {
            try
            {
                validated = await ValidateKeyAsync(keyId, secret, token);
                if (validated.IsEncrypted == true && string.IsNullOrEmpty(secret))
                {
                    var prompted = await PromptAndValidateKeyAsync(validated, token);
                    if (prompted is null)
                        return null;
                    (validated, secret) = prompted.Value;
                }
            }
            catch (SshKeyPassphraseException)
            {
                var prompted = await PromptAndValidateKeyAsync(
                    key, token, "The stored passphrase was not accepted.");
                if (prompted is null)
                    return null;
                (validated, secret) = prompted.Value;
            }
        }

        return new ResolvedSshCredential(
            session with { PrivateKeyPath = validated.Path, PassphraseRequired = validated.IsEncrypted == true },
            secret ?? "");
    }

    private async Task<(SshKeyReference Key, string Secret)?> PromptAndValidateKeyAsync(
        SshKeyReference key, CancellationToken token, string? notice = null)
    {
        while (true)
        {
            var prompted = await ConnectDialogs.PromptCredentialAsync(
                XamlRoot,
                $"Unlock {key.Name}",
                notice is null ? $"Passphrase for {key.Name}" : $"{notice} Passphrase for {key.Name}",
                cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (prompted is null)
                return null;
            try
            {
                var validated = await ValidateKeyAsync(key.Id, prompted.Value.Secret, token);
                token.ThrowIfCancellationRequested();
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

    private async Task<SshKeyReference> ValidateKeyAsync(Guid keyId, string? passphrase, CancellationToken token)
    {
        try
        {
            return await Task.Run(() => _sshKeys.Validate(keyId, passphrase), token).WaitAsync(token);
        }
        catch (SshKeyChangedException change)
        {
            if (!await ConnectDialogs.ConfirmChangedPrivateKeyAsync(XamlRoot, change, token))
                throw new OperationCanceledException("SSH key replacement was not accepted.");
            return await Task.Run(() => _sshKeys.Validate(keyId, passphrase, acceptChanged: true), token).WaitAsync(token);
        }
    }

    /// <summary>Where <paramref name="host"/> sits on a route (this tab's connect's unless
    /// <paramref name="route"/> is given) when it is a jump host ("Jump host 1 of 2: bastion"),
    /// or null for the session itself.</summary>
    private string? RouteStep(Session? host, IReadOnlyList<Session>? route = null)
    {
        var jumps = route ?? _routeSessions;
        for (var index = 0; index < jumps.Count; index++)
        {
            if (jumps[index].Id == host?.Id)
                return jumps.Count == 1 ? $"Jump host: {host.Name}" : $"Jump host {index + 1} of {jumps.Count}: {host.Name}";
        }
        return null;
    }

    /// <summary>A prompt title that says which host is asking: the jump host's place on
    /// the route, or "<paramref name="verb"/> name" for the session itself.</summary>
    private string PromptTitle(Session host, string verb, IReadOnlyList<Session>? route = null) =>
        RouteStep(host, route) ?? $"{verb} {host.Name}";

    /// <summary>" (on the way to target)" for a jump host's prompt; empty for the session's own.</summary>
    private string PromptPurpose(Session host) =>
        RouteStep(host) is null ? "" : $" (on the way to {Session.Name})";

    /// <summary>Runs UI work for the connect thread and waits for it: a login prompt in the
    /// middle of a connect, like the host-key decision.</summary>
    private T RunOnUiBlocking<T>(Func<Task<T>> work, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(token);
                    return;
                }
                tcs.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })) throw new OperationCanceledException("The login prompt could not open.");
        return tcs.Task.WaitAsync(token).GetAwaiter().GetResult();
    }

    /// <summary>Asks for one jump host's login when the connect reaches it, and says in the
    /// terminal that it is being dialled. Null when the prompt was cancelled.</summary>
    private SshHop? ResolveJumpLoginBlocking(SshHop hop, List<SshHop> resolvedJumps, CancellationToken token) =>
        RunOnUiBlocking(async () =>
        {
            var jump = hop.Session;
            var resolved = await ResolveSshCredentialAsync(jump, token);
            token.ThrowIfCancellationRequested();
            if (resolved is null)
                return null;
            var login = new SshHop(resolved.Session, resolved.Secret, ProxyPassword(jump));
            resolvedJumps.Add(login);
            _terminal.WriteNotice(
                $"Connecting to jump host {jump.Name} ({resolved.Session.Username}@{jump.Host}:{SessionEndpoint.EffectivePort(jump)}){RouteDescription(jump)} …");
            return login;
        }, token);

    /// <summary>Marshals the host-key decision onto the UI thread; called from the connect thread.</summary>
    private bool ConfirmHostKeyBlocking(HostKeyInfo info, Dialling dialling, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                tcs.TrySetResult(await ConnectDialogs.ConfirmHostKeyAsync(XamlRoot, info, RouteStep(dialling.Host, dialling.Route), token));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })) throw new OperationCanceledException("The host-key dialog could not open.");
        return tcs.Task.WaitAsync(token).GetAwaiter().GetResult();
    }

    /// <summary>Marshals server keyboard-interactive challenges onto the UI thread.</summary>
    private IReadOnlyList<string>? PromptKeyboardInteractiveBlocking(
        IReadOnlyList<KeyboardInteractivePrompt> prompts, Dialling dialling, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                tcs.TrySetResult(await ConnectDialogs.PromptKeyboardInteractiveAsync(
                    XamlRoot, PromptTitle(dialling.Host ?? Session, "Authenticate to", dialling.Route), prompts, token));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            return null;
        }
        return tcs.Task.WaitAsync(token).GetAwaiter().GetResult();
    }

    /// <summary>Applies the app chrome theme and this session's effective terminal theme
    /// without changing terminal geometry or rescanning highlights.</summary>
    public void ApplyTheme(string theme)
    {
        theme = App.ResolveTheme(theme);
        _chromePalette = ThemeVisualPalette.For(theme);
        ApplyPaneSplitterTheme();
        _terminal.ApplyOptions(theme: App.ResolveTheme(Session.Overrides.EffectiveTheme(theme)));
        _rewindPlayer?.ApplyTheme(theme);
    }

    /// <summary>Pushes layout and interaction settings into the terminal, with this
    /// session's overrides layered on top.</summary>
    public void ApplyNonThemeSettings(Core.Storage.AppSettings settings)
    {
        var effective = settings.WithOverrides(Session.Overrides);
        _terminal.ApplyOptions(
            fontSize: effective.FontSize,
            fontFamily: effective.FontFamily,
            copyOnSelect: effective.CopyOnSelect,
            rightClickPaste: effective.RightClickPaste,
            scrollback: effective.Scrollback);
        _terminal.ApplyHighlights(BuildHighlightPayload());
        _terminal.SetHistoryCapture(effective.KeepCommandHistory);
        _terminal.SetMouseCapture(MouseCapture(effective.MouseCapture));
        // Rewind caps are app-wide (no session override) and apply to the running capture.
        _capture?.SetLimits(RewindLimits.Age(settings.RewindMinutes), RewindLimits.Bytes(settings.RewindMegabytes));
        _rewindPlayer?.ApplySettings(settings);
    }

    private static Resesh.Terminal.TerminalMouseCapture MouseCapture(string? mode) =>
        Core.Storage.MouseCaptureModes.Normalize(mode) switch
        {
            Core.Storage.MouseCaptureModes.Always => Resesh.Terminal.TerminalMouseCapture.Always,
            Core.Storage.MouseCaptureModes.Never => Resesh.Terminal.TerminalMouseCapture.Never,
            _ => Resesh.Terminal.TerminalMouseCapture.Scroll,
        };

    // ---- command history ----

    /// <summary>The folder for a history entry: the working-directory policy without the
    /// backend queries, which cannot run at the moment a command starts.</summary>
    private string? CurrentHistoryDirectory() => _workingDirectory.Known();

    private void RecordHistory(TerminalCommandRecord record)
    {
        if (_disposed || !App.Settings.Current.WithOverrides(Session.Overrides).KeepCommandHistory)
            return;
        // Where the command ran: the connection's own target, which an edit to the session
        // since it connected does not move.
        var ranOn = _tab.ConnectedSession ?? Session;
        var entry = new CommandHistoryEntry
        {
            SessionId = Session.Id,
            SessionName = ranOn.Name,
            Kind = ranOn.Kind,
            Target = CommandHistoryEntry.TargetOf(ranOn),
            // The command's own prompt beats the tab's folder, which may have moved on.
            WorkingDirectory = record.Directory ?? _historyDirectory ?? CurrentHistoryDirectory(),
            Command = record.CommandLine,
            ExitCode = record.ExitCode,
            Exact = record.Exact,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(record.StartedUnixMs).ToLocalTime(),
            EndedAt = record.EndedUnixMs is { } ended
                ? DateTimeOffset.FromUnixTimeMilliseconds(ended).ToLocalTime()
                : null,
            Output = record.Output,
            OutputTruncated = record.OutputTruncated,
            OutputLost = record.OutputLost,
        };
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            try { App.History.Append(entry); }
            catch (Exception exception) when (CommandHistoryStore.IsStorageFailure(exception))
            {
                // Report once per tab; a full disk would otherwise raise a notice per command.
                dispatcher.TryEnqueue(() =>
                {
                    if (_historyWriteFailed)
                        return;
                    _historyWriteFailed = true;
                    App.ReportRecoverableError(exception);
                });
            }
        });
    }

    /// <summary>Enabled highlight rules for this session (global state + session deltas),
    /// shaped for the page's addon.</summary>
    public void PreviewHighlights(HighlightsStore? draft)
    {
        _highlightPreview = draft;
        _terminal.ApplyHighlights(BuildHighlightPayload());
    }

    private IReadOnlyList<Resesh.Terminal.TerminalHighlight> BuildHighlightPayload() =>
        HighlightPayload(_highlightPreview ?? App.Highlights, Session.Overrides);

    /// <summary>The enabled highlight rules for a session, as the terminal applies them.</summary>
    internal static IReadOnlyList<Resesh.Terminal.TerminalHighlight> HighlightPayload(
        HighlightsStore store, TerminalOverrides? overrides) =>
        store.ResolveForSession(overrides)
            .Select(r => new Resesh.Terminal.TerminalHighlight(
                r.Id, r.Pattern, r.Color, r.Bold, r.Underline, r.MatchCase, r.ShowInOverview))
            .ToList();

    /// <summary>Reconnects a stopped tab to an explicitly selected existing shell.</summary>
    public Task ResumeRemoteSessionAsync(int slot)
    {
        // Only a connect that will really start may carry the slot; a request refused here
        // would otherwise leave it behind for the next, unrelated reconnect.
        if (!CanStartConnect)
            return Task.CompletedTask;
        _resumeTmuxSlot = slot;
        return ConnectAsync(isReconnect: false);
    }

    /// <summary>Creates an exec-only connection owned and disposed by the manager. Null when
    /// the login was cancelled, or the tab closed or <paramref name="cancellationToken"/> was
    /// cancelled before the connection was up; the connect is abandoned then, not left
    /// running. A connected tab's manager reaches the host the terminal did, with its login
    /// and route; a stopped tab's resolves them from the saved session.</summary>
    public async Task<SshTerminalSession?> CreateRemoteManagementConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, cancellationToken);
        var token = linked.Token;
        SshTerminalSession? connection = null;
        try
        {
            ResolvedRoute? route;
            ResolvedSshCredential? resolved;
            if (CurrentLogin is { } live)
                (route, resolved) = (live.Route, new ResolvedSshCredential(live.Session, live.Secret));
            else
            {
                if (MissingUsername(Session, App.Logins.Resolve(Session)) is { } noUsername)
                    throw new InvalidOperationException(noUsername);
                route = await ResolveRouteAsync(token);
                resolved = route is null ? null : await ResolveSshCredentialAsync(Session, token);
            }
            if (route is null || resolved is null || token.IsCancellationRequested) return null;
            var (profile, secret, _) = resolved;
            var dialling = new Dialling(route.Sessions);
            var session = connection = new SshTerminalSession(_knownHosts)
            {
                HostKeyDecision = info => ConfirmHostKeyBlocking(info, dialling, token),
                Connecting = host => dialling.Host = host,
            };
            // The token stops the connect only until versions are exchanged; disconnecting
            // also cuts a stalled key exchange or login short (safe mid-connect).
            using (token.Register(session.Disconnect))
            {
                await Task.Run(() => session.Connect(profile, secret, Session.TerminalType, 80, 24,
                    interactiveResponder: prompts => PromptKeyboardInteractiveBlocking(prompts, dialling, token),
                    cancellationToken: token,
                    openShell: false,
                    jumps: route.Jumps,
                    proxyPassword: route.ProxyPassword));
            }
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Cut short by the tab closing; the disconnect fails the connect with whatever
            // SSH.NET throws, which is not worth reporting.
            connection?.Dispose();
            return null;
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    /// <summary>The backend keeping the persistent shell the live connection is attached to,
    /// as that connect chose it; null when the tab is not attached to one, and the session's
    /// settings decide (<see cref="ResolvePersistenceBlocking"/>).</summary>
    public PersistenceBackend? AttachedPersistence => _tab.RemoteSessionSlot is not null ? _persistence : null;

    /// <summary>
    /// Kills the persistent shell this tab's live connection is attached to, and only that
    /// one: never a slot chosen by settings the connection was not made with. Waiting for
    /// the command lets a caller close the tab only after the server accepted the request.
    /// </summary>
    public async Task<bool> TryEndRemoteSessionAsync()
    {
        var session = _ssh;
        return _tab.RemoteSessionSlot is { } slot
            && session is not null
            && await Task.Run(() => session.TryRunCommand(
                _persistence.KillCommand(Session.Id, slot)));
    }

    /// <summary>Sends a break signal when the live backend has one (telnet, serial). Returns false
    /// when there is nothing to send it on, so a shortcut can fall through.</summary>
    public bool SendBreak()
    {
        if (_disposed || _tab.IsLocked || _tab.State != TabConnectionState.Connected
            || _backend is not IBreakSender sender)
            return false;
        sender.SendBreak();
        return true;
    }

    /// <summary>User-initiated Disconnect (SSH) / Stop (local): tab stays open showing the
    /// notice. Stopping a local shell kills its whole process tree via the job object.</summary>
    public void DisconnectLocal()
    {
        _connection.Dispose();
        _backend = null;
        _ssh = null;
        _tab.ConnectionSummary = "";
        EndAgentTracking();
        if (Session.IsLocal)
        {
            _tab.MarkExited(null);
            _terminal.NotifyDisconnected("Stopped.", action: "restart", neutral: true);
        }
        else
        {
            _tab.MarkDisconnected();
            _terminal.NotifyDisconnected("Disconnected.");
        }
        UpdateInputEnabled(); // a stopped tab takes Enter to reconnect or restart
    }

    /// <summary>Opens the local profile's starting directory in Explorer.</summary>
    public void OpenWorkingFolder()
    {
        var directory = new LocalFileSystem(Session.Local?.StartingDirectory).HomeDirectory;
        OpenLocalDirectoryInExplorer(directory);
    }

    private static void OpenLocalDirectoryInExplorer(string directory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(directory);
        _ = System.Diagnostics.Process.Start(startInfo);
    }

    // ---- file pane (Phase 3) ----

    public bool IsFilePaneOpen => _filePane is not null && ColumnDefinitions[2].Width.Value > 0;

    public void ToggleFilePane()
    {
        if (!_tab.Capabilities.FilePane)
            return;
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
        if (_disposed || !_tab.Capabilities.FilePane)
            return;
        var wasOpen = IsFilePaneOpen;
        if (_filePane is null)
        {
            _filePane = Session.IsLocal
                ? new FilePaneView(() => Session, OpenInExplorerAsync)
                : new FilePaneView(() => Session, CreateSftpSessionAsync, OpenInExplorerAsync);
            _filePane.CloseRequested += HideFilePane;
            _filePane.TerminalFolderRequested += () => _ = OpenFilePaneAtCurrentFolderAsync();
            _filePane.TransferProgressChanged += SetTransferProgress;
            Grid.SetColumn(_filePane, 2);
            Children.Add(_filePane);

            _paneSplitter = new CommunityToolkit.WinUI.Controls.GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                ResizeBehavior = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeBehavior.PreviousAndNext,
                ResizeDirection = CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns,
            };
            SplitterHitArea.Apply(_paneSplitter, columns: true);
            _paneSplitterLine = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_chromePalette.Divider),
                IsHitTestVisible = false,
            };
            SplitterHighlight.Attach(_paneSplitter, SetPaneSplitterActive);
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
        ColumnDefinitions[1].Width = new GridLength(1);
        UpdateFilePaneWidthLimit();
        _filePane.Visibility = Visibility.Visible;
        if (!wasOpen)
            FilePaneOpenChanged?.Invoke();
        if (initialPath is not null || notice is not null)
            _ = _filePane.NavigateAsync(initialPath, notice);
        else if (_filePane.IsLoaded)
            _ = _filePane.NavigateAsync(_filePane.CurrentPath);
    }

    private void SetPaneSplitterActive(bool active)
    {
        if (_paneSplitterLine is null)
            return;
        _paneSplitterActive = active;
        ApplyPaneSplitterTheme();
        SplitterHighlight.SetLineThickness(_paneSplitterLine, columns: true, active);
    }

    private void UpdateFilePaneWidthLimit()
    {
        // Keep at least 65% of each terminal group for terminal output. MaxWidth clamps
        // the layout without overwriting the user's preferred width when the window shrinks.
        if (ActualWidth > 0)
            ColumnDefinitions[2].MaxWidth = Math.Min(480, ActualWidth * 0.35);
    }

    private void ApplyPaneSplitterTheme()
    {
        if (_paneSplitterLine is null)
            return;
        _paneSplitterLine.Background = _paneSplitterActive
            ? ThemeResources.Brush(_paneSplitterLine, "SessionSplitterHoverBrush")
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(_chromePalette.Divider);
    }

    public void HideFilePane()
    {
        if (!IsFilePaneOpen)
            return;
        SaveFilePaneWidth();
        ColumnDefinitions[2].Width = new GridLength(0);
        ColumnDefinitions[1].Width = new GridLength(0);
        _filePane!.Visibility = Visibility.Collapsed;
        _paneSplitter!.Visibility = Visibility.Collapsed;
        _paneSplitterLine!.Visibility = Visibility.Collapsed;
        FilePaneOpenChanged?.Invoke();
        _terminal.FocusTerminal();
    }

    /// <summary>
    /// Opens the file pane at the terminal folder. Local sessions use OSC 7 or a known
    /// cmd/PowerShell prompt. SSH sessions use the remote query and prompt fallbacks.
    /// </summary>
    public async Task OpenFilePaneAtCurrentFolderAsync()
    {
        if (!_tab.Capabilities.FilePane)
            return;
        var (path, failure) = await ResolveTerminalDirectoryAsync();
        var fallback = Session.IsLocal ? "the profile folder" : "home";
        ShowFilePane(path, failure is null ? null : $"Couldn't read the current folder ({failure}) — opened {fallback} instead.");
    }

    /// <summary>
    /// The terminal's folder for the file pane and File Explorer, in the order
    /// <see cref="WorkingDirectoryTracker"/> documents: the shell's own report, then a query
    /// of the backend (a persistent shell's pane folder; Linux /proc for a plain one, which
    /// can also say the shell is not at a prompt), then the prompt's folder.
    /// </summary>
    private async Task<(string? Path, string? Failure)> ResolveTerminalDirectoryAsync()
    {
        if (Session.IsLocal)
        {
            // Re-read the prompt now: the last report may be older than the screen.
            if (await _terminal.RequestPromptContextAsync() is { } prompt)
                ObservePrompt(prompt.Context, prompt.Platform);
            var localPath = ResolveLocalTerminalDirectory(out var localFailure);
            return (localPath, localFailure);
        }

        if (_workingDirectory.ReportedPath is { } reportedPath)
            return (reportedPath, null);

        string? path = null;
        string? failure = null;
        var canUsePromptFallback = !_workingDirectory.HostMismatch;
        if (!canUsePromptFallback)
            failure = "the shell reported a folder from another host";
        var session = _ssh;
        if (_tab.RemoteSessionSlot is { } slot && session is not null && session.IsConnected)
        {
            var result = await Task.Run(() => session.RunCommand(_persistence.CurrentPathCommand()));
            if (result is null)
                failure ??= "the connection did not accept the query";
            else if (!result.Success)
                failure ??= string.IsNullOrWhiteSpace(result.Error)
                    ? "the persistent session query failed"
                    : $"persistent session: {result.Error.Trim()}";
            else
            {
                path = _persistence.ParseCurrentPath(result.Output, Session.Id, slot);
                if (path is null)
                    failure ??= "no matching persistent session in the reply";
            }
        }
        // A plain Linux session can report its foreground shell's cwd through /proc on
        // a separate SSH channel. This sends no terminal input and changes no remote file.
        else if (canUsePromptFallback && session is not null && session.IsConnected)
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
            _workingDirectory.PromptPath is { } promptPath &&
            (promptPath == "~" || promptPath.StartsWith("~/", StringComparison.Ordinal) ||
             promptPath.StartsWith("/", StringComparison.Ordinal)))
        {
            path = promptPath;
        }

        if (path is null)
            failure ??= "the shell did not report a current folder";

        return (path, failure);
    }

    public bool HasFileExplorer => _tab.Capabilities.FilePane &&
        (Session.IsLocal || Interop.SshfsIntegration.IsInstalled);

    /// <summary>Opens the file pane's displayed folder, or the terminal folder when
    /// the pane is closed. Remote folders use the existing SSHFS mount.</summary>
    public async Task OpenFileExplorerAsync()
    {
        if (!HasFileExplorer || _disposed || _tab.IsLocked || _tab.State != TabConnectionState.Connected)
            return;
        try
        {
            var path = IsFilePaneOpen
                ? _filePane!.CurrentPath
                : (await ResolveTerminalDirectoryAsync()).Path;
            if (_disposed || _tab.IsLocked)
                return;
            path ??= Session.IsLocal
                ? new LocalFileSystem(Session.Local?.StartingDirectory).HomeDirectory
                : "/";
            await OpenInExplorerAsync(path);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (_disposed)
                return;
            await Dialogs.MessageDialog.ShowMessageAsync(XamlRoot, "File Explorer could not open", exception.Message);
        }
    }

    /// <summary>The local half of <see cref="ResolveTerminalDirectoryAsync"/>: the shell's
    /// report, then the prompt's folder, each only when it names a folder that exists; the
    /// process's own folder only when the shell has said nothing at all.</summary>
    private string? ResolveLocalTerminalDirectory(out string? failure)
    {
        failure = null;
        if (_workingDirectory.HostMismatch)
        {
            failure = "the shell reported a folder from another host";
            return null;
        }
        var files = new LocalFileSystem(Session.Local?.StartingDirectory);
        var candidates = new List<string>();
        if (_workingDirectory.ReportedPath is { } reportedPath)
            candidates.Add(reportedPath);
        if (_tab.RunningCommand is null && _workingDirectory.PromptPath is { } promptPath)
            candidates.Add(promptPath);

        foreach (var candidate in candidates)
        {
            try
            {
                return files.ResolveDirectory(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                failure = ex.Message;
            }
        }

        // cmd updates the process directory, but PowerShell keeps its location apart from
        // it, so the process folder is only a guess for a shell that has said nothing.
        if (!_workingDirectory.HasEvidence && _backend is LocalTerminalSession local &&
            local.TryGetCurrentDirectory() is { } processDirectory)
            return processDirectory;

        failure ??= candidates.Count > 0
            ? "the shell is not in a folder"
            : "the shell did not report a current folder";
        return null;
    }

    private Interop.SshfsMount? _sshfsMount;
    private string? _sshfsTarget;
    private Task<Interop.SshfsMount?>? _sshfsMounting;
    private readonly CancellationTokenSource _closing = new(); // cancelled when the tab closes (SSHFS mounts, management connections)

    /// <summary>Opens a local directory directly. Remote sessions mount the SFTP path
    /// through SSHFS first. A second request while the mount comes up waits for the same
    /// mount and then opens its own folder.</summary>
    private async Task OpenInExplorerAsync(string path)
    {
        if (Session.IsLocal)
        {
            OpenLocalDirectoryInExplorer(path);
            return;
        }
        // The connection the terminal made: its login and route, not the session's settings
        // as edited since. Like the file pane, the mount follows the terminal.
        var live = CurrentLogin
            ?? throw new InvalidOperationException("The session is disconnected. Reconnect it to open its files in File Explorer.");
        var session = live.Session;
        // SSHFS-Win dials the host itself, so it cannot follow a jump host or proxy.
        if (live.Route.Jumps.Count > 0 || session.JumpHostId is not null || JumpRoute.ActiveProxy(session) is not null)
            throw new IOException("File Explorer connects straight to the host, so it cannot open a session "
                + "reached through a jump host or proxy. Use the file pane instead.");
        var mount = _sshfsMount;
        // A mount made for another target (the session was edited and reconnected) is replaced.
        if (mount is not { IsAlive: true } || _sshfsTarget != SshfsTarget(session))
        {
            var mounting = _sshfsMounting ??= MountSshfsAsync(session, live.Secret);
            try
            {
                mount = await mounting;
            }
            finally
            {
                if (ReferenceEquals(_sshfsMounting, mounting))
                    _sshfsMounting = null;
            }
            if (mount is null || _disposed)
                return; // the tab closed during the mount
        }
        Interop.SshfsIntegration.OpenInExplorer(mount.Root, path);
    }

    private async Task<Interop.SshfsMount?> MountSshfsAsync(Session session, string secret)
    {
        var token = _closing.Token;
        var stale = _sshfsMount;
        _sshfsMount = null;
        var password = session.AuthMethod == AuthMethod.Password ? secret : null;
        Interop.SshfsMount mount;
        try
        {
            // The key file is checked against its registered fingerprint, as every
            // connection's is; the terminal's check was made when it connected.
            var keyPath = session.AuthMethod == AuthMethod.PrivateKey ? await ValidateSshfsKeyAsync(session, token) : null;
            // Killing a dead mount's process can wait, so it happens off the UI thread too.
            mount = await Task.Run(() =>
            {
                stale?.Dispose();
                return Interop.SshfsIntegration.Mount(session, _knownHosts, password, keyPath, token);
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw new IOException("The changed SSH key was not accepted.");
        }
        if (_disposed)
        {
            // The tab closed during the mount; its Dispose could not see this process.
            _ = Task.Run(mount.Dispose);
            return null;
        }
        _sshfsMount = mount;
        _sshfsTarget = SshfsTarget(session);
        return mount;
    }

    /// <summary>The key file an Explorer mount logs in with, once its fingerprint is checked.</summary>
    private async Task<string> ValidateSshfsKeyAsync(Session session, CancellationToken token)
    {
        const string encrypted = "The key is passphrase-protected, and sshfs cannot prompt for it. "
            + "Use a passphrase-free key for the Explorer mount.";
        if (session.PrivateKeyId is not { } keyId || _sshKeys.Find(keyId) is not { } registered)
            throw new IOException("The session does not have a registered SSH key.");
        if (registered.IsEncrypted == true)
            throw new IOException(encrypted);
        SshKeyReference key;
        try
        {
            key = await ValidateKeyAsync(keyId, null, token);
        }
        catch (SshKeyPassphraseException)
        {
            throw new IOException(encrypted);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        if (key.IsEncrypted == true)
            throw new IOException(encrypted);
        return key.Path;
    }

    /// <summary>What an SSHFS mount was made for: host, port and login (the registered key,
    /// whose current file the mount reads, not a path the session once had).</summary>
    private static string SshfsTarget(Session session) =>
        $"{session.Username}@{session.Host}:{SessionEndpoint.EffectivePort(session)}|{session.AuthMethod}|"
        + (session.AuthMethod == AuthMethod.PrivateKey ? session.PrivateKeyId?.ToString() : null);

    /// <summary>Connect factory handed to the pane: reuses the live connection's login, secret
    /// and route and the shared host-key trust. Stops when the pane gives up (a newer
    /// navigation, Cancel, the pane closing) or the tab reconnects.</summary>
    private async Task<SftpSession> CreateSftpSessionAsync(CancellationToken paneToken)
    {
        // The pane follows the terminal: a tab whose connection ended browses nothing, and
        // one still connecting has no login and route yet (a reconnect's are not published
        // until it is up). The pane lists its folder again once the tab is Connected.
        if (_tab.State == TabConnectionState.Connecting)
            throw new InvalidOperationException("The session is still connecting. Its files appear once it is connected.");
        var live = CurrentLogin
            ?? throw new InvalidOperationException("The session is disconnected. Reconnect it to browse its files.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token, paneToken);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var (session, secret, route) = live;
        var dialling = new Dialling(route.Sessions);
        var sftp = new SftpSession(_knownHosts);
        try
        {
            await Task.Run(() => sftp.Connect(session, secret, prompts => PromptKeyboardInteractiveBlocking(prompts, dialling, token),
                route.Jumps, route.ProxyPassword, token, host => dialling.Host = host));
            token.ThrowIfCancellationRequested();
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
            App.SaveSettings(App.Settings.Current with { FilePaneWidth = width });
    }

    // ---- lock overlay (per plan: obscure output, block input, buffer continues) ----

    private Button? _lockOverlay;

    public void ShowLockOverlay()
    {
        UpdateInputEnabled(); // IsLocked is set: blocks keyboard and pointer input into the terminal
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
        UpdateInputEnabled();
        if (_terminalInputEnabled)
            _terminal.FocusTerminal();
    }

    /// <summary>Clean disconnect keeping the tab and scrollback (used by tab close and app teardown).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tab.Progress = TerminalProgress.None;
        CancelPacedPaste();
        StopSerialPortWatch();
        _agentPoll?.Stop();
        _agentPoll = null;
        CleanupActions.Run(App.ReportRecoverableError,
            () => _connection.Dispose(),
            () => _closing.Cancel(), // SSHFS mounts and management connections still coming up stop
            () => _tmuxReservation.Dispose(),
            () => { if (IsFilePaneOpen) SaveFilePaneWidth(); },
            () => _filePane?.Dispose(),
            () => _sshfsMount?.Dispose(),
            () => _rewindPlayer?.Dispose(),
            () => _terminal.Dispose(),
            () => _capture?.Dispose());
        _filePane = null;
        _sshfsMount = null;
        _rewindPlayer = null;
        _backend = null;
        _ssh = null;
        _capture = null;
        _liveLogin = null;
    }
}
