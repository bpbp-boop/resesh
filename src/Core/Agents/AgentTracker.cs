namespace Sessions.Core.Agents;

/// <summary>What a tab currently shows for its agent: identity, attention, and the
/// evidence behind it. Immutable so views can diff two snapshots cheaply.</summary>
public sealed record AgentSnapshot(string? Key, AgentAttention Attention, string? Label, AgentSource Source)
{
    public static readonly AgentSnapshot Empty = new(null, AgentAttention.None, null, AgentSource.Unknown);

    /// <summary>True when an actual agent identity is showing (not a plain shell).</summary>
    public bool IsAgent => AgentIdentities.IsAgentKey(Key);

    public string Name => AgentIdentities.DisplayName(Key);
}

/// <summary>
/// One tab's agent state machine. Every source of evidence — adapters, prompts, titles,
/// local process membership, the user — feeds in here, and the resolved snapshot comes
/// out. Deliberately UI-free and synchronous so the precedence rules can be tested.
///
/// Precedence, strongest first: the user's manual override, an adapter's structured
/// events, live detection (local process membership, prompt commands, titles), then the
/// session's default identity as the "before we know anything" fallback.
///
/// Attention never comes from detection — a heuristic may say WHICH agent is running,
/// never that it is waiting for you.
/// </summary>
public sealed class AgentTracker
{
    private string? _sessionDefault;
    private string? _manual;
    private string? _structured;      // identity asserted by an adapter, sticky until it ends
    private bool _structuredSeen;     // an adapter spoke once: stop listening to bells
    private string? _detected;        // identity from heuristics ("shell" is a real answer)
    private AgentSource _detectedSource = AgentSource.Unknown;
    private AgentAttention _attention = AgentAttention.None;
    private string? _label;

    public AgentTracker(string? sessionDefault = null)
    {
        _sessionDefault = Normalize(sessionDefault);
    }

    /// <summary>The session's saved default identity: null = auto-detect,
    /// <see cref="AgentIdentities.None"/> = never show an agent here, otherwise a key.</summary>
    public string? SessionDefault
    {
        get => _sessionDefault;
        set => _sessionDefault = Normalize(value);
    }

    /// <summary>The tab-menu override; null = follow detection again.</summary>
    public string? ManualOverride => _manual;

    /// <summary>The user picked an identity (or "auto", or "none") from the tab menu.</summary>
    public bool SetManualOverride(string? key) => Change(() => _manual = Normalize(key));

    /// <summary>Whether agent icons are suppressed here (either level said "none").</summary>
    public bool Suppressed =>
        _manual == AgentIdentities.None || (_manual is null && _sessionDefault == AgentIdentities.None);

    public AgentSnapshot Current
    {
        get
        {
            var key = ResolveKey();
            var isAgent = AgentIdentities.IsAgentKey(key);
            return new AgentSnapshot(
                key,
                isAgent ? _attention : AgentAttention.None,
                isAgent ? _label : null,
                ResolveSource());
        }
    }

    private string? ResolveKey()
    {
        if (Suppressed)
            return null;
        if (_manual is not null)
            return _manual;
        return _structured ?? _detected ?? _sessionDefault;
    }

    private AgentSource ResolveSource()
    {
        if (Suppressed)
            return AgentSource.Unknown;
        if (_manual is not null)
            return AgentSource.Manual;
        if (_structured is not null || _structuredSeen)
            return AgentSource.Structured;
        return _detected is not null ? _detectedSource : AgentSource.Unknown;
    }

    // ---- evidence ----

    /// <summary>
    /// A command was run at a shell prompt (OSC 133, or Enter-gated discovery). This is the
    /// one signal that can also retire a stale agent: reaching a shell prompt means whatever
    /// owned the terminal has exited, even if it never said so.
    /// </summary>
    public bool ObserveCommand(string? commandLine) => Change(() =>
    {
        if (Suppressed)
            return;
        var key = AgentDetection.FromCommand(commandLine);
        _structured = null; // back at a prompt: any adapter-reported agent is gone
        _detected = key ?? AgentIdentities.Shell;
        _detectedSource = AgentSource.Command;
        _label = null;
        _attention = key is not null ? AgentAttention.Working : AgentAttention.None;
    });

    /// <summary>The terminal title changed. Identity only, and only when it names an agent —
    /// a title that mentions no agent is not evidence that one exited.</summary>
    public bool ObserveTitle(string? title) => Change(() =>
    {
        if (Suppressed)
            return;

        // The tmux title bridge reports the foreground shell instead of a stale pane title
        // when an agent exits. This is definite negative evidence and retires even a
        // structured identity whose SessionEnd hook did not run yet.
        if (AgentDetection.IsShellTitle(title))
        {
            _structured = null;
            _detected = AgentIdentities.Shell;
            _detectedSource = AgentSource.Title;
            _label = null;
            _attention = AgentAttention.None;
            return;
        }

        if (_structured is not null)
            return;
        var key = AgentDetection.FromTitle(title);
        if (key is null || key == _detected)
            return;
        _detected = key;
        _detectedSource = AgentSource.Title;
        if (_attention == AgentAttention.None)
            _attention = AgentAttention.Working;
    });

    /// <summary>
    /// The processes currently inside this tab's job object (local tabs only). Membership is
    /// definitive both ways: an agent process appearing starts the identity, and its absence
    /// ends one — including an adapter-reported agent that died without an exit event.
    /// </summary>
    public bool ObserveProcesses(IEnumerable<string>? processNames) => Change(() =>
    {
        if (Suppressed)
            return;
        var key = AgentDetection.FromProcessNames(processNames);
        if (key is not null)
        {
            if (_detected != key)
            {
                _detected = key;
                _detectedSource = AgentSource.Process;
                if (_structured is null)
                    _attention = AgentAttention.Working;
            }
            return;
        }

        // No agent process in the job: nothing is running here but a shell.
        if (_structured is null && _detected is null or AgentIdentities.Shell)
            return;
        _structured = null;
        _detected = AgentIdentities.Shell;
        _detectedSource = AgentSource.Process;
        _label = null;
        _attention = AgentAttention.None;
    });

    /// <summary>An escape sequence arrived (structured adapter event, or a generic
    /// notification we treat as low-confidence).</summary>
    public bool ObserveEvent(AgentEvent? agentEvent) => Change(() =>
    {
        if (Suppressed || agentEvent is null)
            return;

        if (agentEvent.Source == AgentSource.Structured)
        {
            _structuredSeen = true;
            if (agentEvent.Ended || agentEvent.Key == AgentIdentities.Shell)
            {
                _structured = null;
                _detected = AgentIdentities.Shell;
                _detectedSource = AgentSource.Structured;
                _label = null;
                _attention = AgentAttention.None;
                return;
            }
            _structured = agentEvent.Key
                ?? _structured
                ?? (AgentIdentities.IsAgentKey(_detected) ? _detected : AgentIdentities.Generic);
            _attention = agentEvent.Attention;
            _label = agentEvent.Label;
            return;
        }

        // Bell / OSC 9 / OSC 777: only meaningful if an agent is already showing, never
        // once an adapter has proven it reports properly, and never a downgrade of a
        // state that says the agent is actually blocked.
        if (_structuredSeen || !AgentIdentities.IsAgentKey(ResolveKey()) || _attention.RequiresUser())
            return;
        _attention = AgentAttention.Signal;
        _label = agentEvent.Label;
    });

    /// <summary>The user typed into this tab. Answering is what unblocks a waiting agent,
    /// so this is the signal that clears a sticky badge.</summary>
    public bool ObserveUserInput() => Change(() =>
    {
        if (_attention is AgentAttention.None or AgentAttention.Working)
            return;
        _attention = AgentIdentities.IsAgentKey(ResolveKey()) ? AgentAttention.Working : AgentAttention.None;
        _label = null;
    });

    /// <summary>The tab was selected. Clears the badges that only meant "look at me";
    /// a genuinely blocked agent keeps its badge until input arrives.</summary>
    public bool ObserveViewed() => Change(() =>
    {
        if (!_attention.ClearsOnView())
            return;
        _attention = AgentIdentities.IsAgentKey(ResolveKey()) ? AgentAttention.Idle : AgentAttention.None;
        _label = null;
    });

    /// <summary>The shell or connection ended; everything detected is stale. The user's own
    /// override survives, since the tab can be restarted in place.</summary>
    public bool ObserveEnded() => Change(() =>
    {
        _structured = null;
        _structuredSeen = false;
        _detected = null;
        _detectedSource = AgentSource.Unknown;
        _attention = AgentAttention.None;
        _label = null;
    });

    private static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var value = key.Trim().ToLowerInvariant();
        if (value == "auto")
            return null;
        if (value == AgentIdentities.None || AgentIdentities.Find(value) is not null)
            return value;
        return AgentIdentities.Generic; // an unknown key still means "an agent"
    }

    private bool Change(Action mutate)
    {
        var before = Current;
        mutate();
        return Current != before;
    }
}
