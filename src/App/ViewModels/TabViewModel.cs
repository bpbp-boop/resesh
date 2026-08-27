using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Resesh.Core.Agents;
using Resesh.Core.Backend;
using Resesh.Core.Models;

namespace Resesh.App.ViewModels;

public enum TabConnectionState
{
    /// <summary>SSH connecting, or a local process starting.</summary>
    Connecting,

    /// <summary>SSH connected, or a local process running.</summary>
    Connected,

    /// <summary>SSH connection ended/failed, or a local launch failed (red).</summary>
    Disconnected,

    /// <summary>Local process ended normally — neutral, keeps the tab open with the exit code.</summary>
    Exited,

    /// <summary>Read-only playback of a recording; no backend process or connection.</summary>
    Playback,
}

/// <summary>One open tab: a session plus its terminal view and connection state.</summary>
public sealed class TabViewModel : ObservableObject
{
    private readonly Guid _tabInstanceId = Guid.NewGuid();
    private Session _session;
    private string? _titleOverride;
    private TabConnectionState _state = TabConnectionState.Connecting;
    private string _connectionSummary = "";
    private object? _view;
    private string _appTheme;

    public TabViewModel(Session session)
        : this(session, isOnboarding: false)
    {
    }

    private TabViewModel(Session session, bool isOnboarding)
    {
        _session = session;
        _appTheme = App.Settings.Current.Theme;
        _appTheme = App.ResolveTheme(_appTheme);
        IsOnboarding = isOnboarding;
    }

    public static TabViewModel CreateOnboarding() => new(
        new Session
        {
            Id = Guid.Empty,
            Name = "Welcome",
            Kind = SessionKind.Local,
            Local = new LocalTarget(),
        },
        isOnboarding: true)
    {
        State = TabConnectionState.Playback,
    };

    /// <summary>A native app page hosted in the tab strip instead of a terminal session.</summary>
    public bool IsOnboarding { get; }

    public bool CanDrag => !IsOnboarding;

    /// <summary>What this tab's target kind supports; drives menu naming and visibility.</summary>
    public SessionCapabilities Capabilities => SessionCapabilities.For(Session);

    public bool IsLocal => Session.IsLocal;

    private string? _playbackPath;

    /// <summary>Non-null for an ephemeral, read-only asciicast playback tab.</summary>
    public string? PlaybackPath
    {
        get => _playbackPath;
        set
        {
            if (SetProperty(ref _playbackPath, value))
            {
                OnPropertyChanged(nameof(IsPlayback));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(Endpoint));
            }
        }
    }

    public bool IsPlayback => PlaybackPath is not null;

    /// <summary>Exit code of the last local process run, shown while <see cref="State"/> is Exited.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Distinguishes this tab's tmux session when several tabs of the same saved session are
    /// open (Clone): slot 0 is the primary; clones get their own remote session. Fixed at
    /// tab creation so Reconnect re-attaches to the same remote session.
    /// </summary>
    public int TmuxSlot { get; set; }

    public Session Session
    {
        get => _session;
        set
        {
            if (SetProperty(ref _session, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(CloseAutomationName));
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(ColorTag));
            }
        }
    }

    /// <summary>The tab's content control (TerminalTabView); set by the window after creation.</summary>
    public object? View
    {
        get => _view;
        set => SetProperty(ref _view, value);
    }

    /// <summary>Display-only tab title override (context-menu Rename, M4). Null = session name.</summary>
    public string? TitleOverride
    {
        get => _titleOverride;
        set
        {
            if (SetProperty(ref _titleOverride, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(CloseAutomationName));
            }
        }
    }

    public TabConnectionState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                // A reconnecting or dead tab must not keep advertising the last program it
                // ran; the next prompt or full-screen app sets a fresh title.
                if (value != TabConnectionState.Connected)
                {
                    TerminalTitle = null;
                    RunningCommand = null;
                    PromptContext = null;
                }
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    private bool _isRecording;
    private bool _hasRewind;

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(RecordingTooltip));
            }
        }
    }

    public bool HasRewind
    {
        get => _hasRewind;
        set => SetProperty(ref _hasRewind, value);
    }

    public string RecordingTooltip => IsRecording ? "Recording terminal output" : "";

    // ---- VS Code-style tab visuals (the header content paints the whole tab) ----

    private bool _isActive;
    private bool _isPointerOver;
    private bool _isGroupFocused = true;

    /// <summary>Whether this tab is its group's selected tab; drives the tab visuals.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                if (value)
                    HasUnseenOutput = false; // selecting the tab means the output has been seen
                NotifyTabVisuals();
            }
        }
    }

    // ---- activity indicator (output arrived while the tab wasn't visible) ----

    private bool _hasUnseenOutput;

    /// <summary>Output arrived while this tab wasn't its group's selected tab; cleared on selection.
    /// Shown SecureCRT-style: the status dot turns accent blue and the title goes semibold.</summary>
    public bool HasUnseenOutput
    {
        get => _hasUnseenOutput;
        private set
        {
            if (SetProperty(ref _hasUnseenOutput, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>Called (UI thread) when session output arrives; marks the tab unless it's visible.
    /// A tab selected in an unfocused split group is still on screen, so IsActive is the gate.</summary>
    public void NotifyOutputActivity()
    {
        if (!IsActive)
            HasUnseenOutput = true;
    }

    // ---- agent awareness (Phase 6.2): replace the session icon while an agent is active ----

    private AgentSnapshot _agent = AgentSnapshot.Empty;

    /// <summary>What is running in this tab right now, as resolved by the tab's
    /// <c>AgentTracker</c>. Its icon replaces the session icon while the agent is active.</summary>
    public AgentSnapshot Agent
    {
        get => _agent;
        set
        {
            if (SetProperty(ref _agent, value))
                NotifyAgentVisuals();
        }
    }

    /// <summary>Re-reads the agent visuals; also used when the app-wide "show agent icons"
    /// setting changes, since the displayed icon and badge are derived from it.</summary>
    public void NotifyAgentVisuals()
    {
        OnPropertyChanged(nameof(AgentIconKey));
        OnPropertyChanged(nameof(AgentBadgeGlyph));
        OnPropertyChanged(nameof(AgentBadgeSize));
        OnPropertyChanged(nameof(AgentTooltip));
    }

    private static bool AgentIconsEnabled => App.Settings.Current.ShowAgentIcons;

    public string? AgentIconKey => AgentIconsEnabled && Agent.IsAgent ? Agent.Key : null;

    /// <summary>Meaningful states get a symbol as well as a colour, so the badge remains
    /// readable for users who cannot distinguish the colours.</summary>
    public string AgentBadgeGlyph => Agent.Attention switch
    {
        AgentAttention.NeedsApproval => "!",
        AgentAttention.NeedsAnswer => "?",
        AgentAttention.Complete => "✓",
        AgentAttention.Failed => "×",
        _ => "",
    };

    public double AgentBadgeSize => AgentBadgeGlyph.Length == 0 ? 7 : 11;

    /// <summary>Names the agent, its state, and how we know — so a guess never reads like
    /// a report. Any label came off the wire and is already sanitized and truncated.</summary>
    public string AgentTooltip
    {
        get
        {
            if (!Agent.IsAgent)
                return "";
            var text = Agent.Name;
            var state = AgentAttentionExtensions.Describe(Agent.Attention);
            if (state.Length > 0)
                text += " — " + state;
            if (!string.IsNullOrEmpty(Agent.Label))
                text += ": " + Agent.Label;
            return text + Agent.Source switch
            {
                AgentSource.Structured => " (reported by the agent)",
                AgentSource.Manual => " (set by you)",
                AgentSource.Unknown => "",
                _ => " (detected)",
            };
        }
    }

    /// <summary>
    /// Whether this tab's group is the focused one. In split view each group has a
    /// selected tab. The unfocused group's selected tab keeps the active surface so it
    /// still reads as the open tab in that pane, with an underline distinguishing it.
    /// </summary>
    public bool IsGroupFocused
    {
        get => _isGroupFocused;
        set
        {
            if (SetProperty(ref _isGroupFocused, value))
                NotifyTabVisuals();
        }
    }

    /// <summary>Pointer hover, set by the view; reveals the close × and hover tint (VS Code style).</summary>
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (SetProperty(ref _isPointerOver, value))
                NotifyTabVisuals();
        }
    }

    private void NotifyTabVisuals()
    {
        OnPropertyChanged(nameof(CloseOpacity));
        OnPropertyChanged(nameof(CloseInteractive));
    }

    /// <summary>The × shows on the active tab and on hover; hidden (but space kept) otherwise. Never on pinned tabs.</summary>
    public double CloseOpacity => !IsPinned && (IsActive || IsPointerOver) ? 1.0 : 0.0;

    public bool CloseInteractive => !IsPinned && (IsActive || IsPointerOver);

    public string CloseAutomationName => $"Close {Header}";

    public string CloseAutomationId => $"TabClose_{_tabInstanceId:N}";

    public string AppTheme => _appTheme;

    /// <summary>Applies a saved or previewed app theme to this tab's chrome.</summary>
    public void ApplyAppTheme(string theme)
    {
        _appTheme = theme;
        _appTheme = App.ResolveTheme(_appTheme);
        OnPropertyChanged(nameof(AppTheme));
    }

    /// <summary>e.g. "aes256-gcm@openssh.com • SHA256:…" once connected.</summary>
    public string ConnectionSummary
    {
        get => _connectionSummary;
        set => SetProperty(ref _connectionSummary, value);
    }

    public string Header => IsOnboarding ? "Welcome" : TitleOverride ?? Session.Name;

    // ---- second tab line (tells tabs of the same session apart) ----

    private string? _terminalTitle;

    /// <summary>Stock .bashrc's per-prompt title: "user@host: cwd". The shape means "sitting at
    /// a prompt", so the cwd is the useful half; any other title was set by what's running.</summary>
    private static readonly Regex PromptTitle =
        new(@"^[^@\s]+@[^:\s]+:\s*(?<cwd>\S.*)$", RegexOptions.Compiled);

    /// <summary>
    /// OSC 0/2 title reported by the terminal page — the only process hint a host without
    /// tmux gives us for free. Null until something sets one.
    /// </summary>
    public string? TerminalTitle
    {
        get => _terminalTitle;
        private set
        {
            if (SetProperty(ref _terminalTitle, value))
                OnPropertyChanged(nameof(Subtitle));
        }
    }

    /// <summary>Called (UI thread) when the terminal page reports a new window title.</summary>
    public void ApplyTerminalTitle(string? title)
    {
        var trimmed = title?.Trim();
        // A tmux older than 2.6 has no #{==:} and sends our set-titles-string back verbatim;
        // that is noise, not a title, and the endpoint fallback beats showing it.
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Contains("#{", StringComparison.Ordinal)
            || (IsLocal && CommandTitle.IsLocalExecutableTitle(trimmed, Session.Local?.Executable)))
            TerminalTitle = null;
        else
            TerminalTitle = trimmed;
        // A prompt-shaped title means the shell is drawing a prompt again: whatever
        // command was running is over. This is the only end signal hosts without
        // OSC 133 ever send.
        if (TerminalTitle is { } t && PromptTitle.IsMatch(t))
            RunningCommand = null;
    }

    private string? _runningCommand;

    /// <summary>
    /// Program name of the command the terminal page saw start (Enter-gated discovery or
    /// OSC 133;C), shown while no fresher title exists: stock PS1s refresh the title only
    /// when the NEXT prompt is drawn, so mid-command the title still says the old cwd.
    /// </summary>
    public string? RunningCommand
    {
        get => _runningCommand;
        private set
        {
            if (SetProperty(ref _runningCommand, value))
                OnPropertyChanged(nameof(Subtitle));
        }
    }

    /// <summary>Called (UI thread) when the page reports a command starting ("" = ended).</summary>
    public void ApplyRunningCommand(string? commandLine) =>
        RunningCommand = CommandTitle.ProgramName(commandLine);

    private string? _promptContext;

    /// <summary>Current location read from a known idle prompt: a local directory or a
    /// network CLI context.</summary>
    public string? PromptContext
    {
        get => _promptContext;
        private set
        {
            if (SetProperty(ref _promptContext, value))
                OnPropertyChanged(nameof(Subtitle));
        }
    }

    /// <summary>The prompt parser's platform key. A network CLI context is a label, not
    /// an SFTP path, so current-folder fallback uses this value to exclude it.</summary>
    public string? PromptContextPlatform { get; private set; }

    /// <summary>A recognized prompt means the shell is idle in this location.</summary>
    public void ApplyPromptContext(string? context, string? platform = null)
    {
        var trimmed = context?.Trim();
        PromptContext = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        PromptContextPlatform = string.IsNullOrWhiteSpace(platform) ? null : platform;
        RunningCommand = null;
    }

    /// <summary>
    /// The tab's second line, best available: what a program set as the title, else the
    /// running command the page saw start, else the cwd from a prompt-shaped title, else
    /// the endpoint. The endpoint is always known, so this never returns empty — a blank
    /// second line would leave a visible gap in the strip.
    /// </summary>
    public string Subtitle
    {
        get
        {
            if (IsOnboarding)
                return "Setup Resesh";
            if (TerminalTitle is not { } title)
                return RunningCommand ?? PromptContext ?? FallbackSubtitle;
            var match = PromptTitle.Match(title);
            if (!match.Success)
                return title; // a program's own title beats a guessed command name
            return RunningCommand ?? PromptContext ?? match.Groups["cwd"].Value;
        }
    }

    /// <summary>Shown until a title arrives, and on hosts that never send one (network gear).</summary>
    private string FallbackSubtitle
    {
        get
        {
            if (PlaybackPath is not null)
                return Path.GetFileName(PlaybackPath);
            if (IsLocal)
            {
                var directory = Environment.ExpandEnvironmentVariables(
                    Session.Local?.StartingDirectory?.Trim() ?? "");
                return string.IsNullOrEmpty(directory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : directory;
            }
            return string.IsNullOrEmpty(Session.Username)
                ? Session.Host
                : $"{Session.Username}@{Session.Host}";
        }
    }

    public string Endpoint => IsOnboarding
        ? ""
        : IsLocal
            ? Session.Local?.Executable ?? ""
            : $"{Session.Username}@{Session.Host}:{Session.Port}";

    public string StateText => IsOnboarding
        ? "setup"
        : State switch
        {
            TabConnectionState.Connecting => IsLocal ? "starting…" : "connecting…",
            TabConnectionState.Connected when IsLocal => HasUnseenOutput ? "running — new output" : "running",
            TabConnectionState.Connected => HasUnseenOutput ? "connected — new output" : "connected",
            TabConnectionState.Playback => "recording playback",
            TabConnectionState.Exited => ExitCode is { } code ? $"exited (code {code})" : "exited",
            _ => IsLocal ? "failed" : "disconnected",
        };

    // ---- Pin state (browser-style: pinned tabs can't be closed without unpinning) ----

    private bool _isPinned;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                NotifyTabVisuals(); // the × hides while pinned
            }
        }
    }

    // ---- Lock state (per plan: password held in memory only; never persisted) ----

    private bool _isLocked;
    private string? _lockPassword;

    public bool IsLocked
    {
        get => _isLocked;
        private set
            => SetProperty(ref _isLocked, value);
    }

    public string? ColorTag => Session.ColorTag;

    /// <summary>Set while locked-out after repeated failed unlock attempts.</summary>
    public DateTimeOffset LockoutUntil { get; private set; } = DateTimeOffset.MinValue;

    private int _failedUnlockAttempts;

    public void Lock(string password)
    {
        _lockPassword = password;
        _failedUnlockAttempts = 0;
        IsLocked = true;
    }

    /// <summary>False on wrong password; three misses start a 30-second lockout.</summary>
    public bool TryUnlock(string password)
    {
        if (DateTimeOffset.Now < LockoutUntil)
            return false;

        if (password == _lockPassword)
        {
            _lockPassword = null;
            _failedUnlockAttempts = 0;
            IsLocked = false;
            return true;
        }

        if (++_failedUnlockAttempts >= 3)
        {
            LockoutUntil = DateTimeOffset.Now.AddSeconds(30);
            _failedUnlockAttempts = 0;
        }
        return false;
    }
}
