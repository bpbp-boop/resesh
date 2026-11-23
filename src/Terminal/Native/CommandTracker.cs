using System.Globalization;
using System.Text.RegularExpressions;

namespace Resesh.Terminal.Native;

/// <summary>A position in the normal screen that follows its line through scrolling, reflow and
/// scrollback trimming. <see cref="Line"/> is -1 once the line is gone.</summary>
internal interface ICommandMarker : IDisposable
{
    int Line { get; }
    bool IsDisposed { get; }
}

/// <summary>What the command tracker needs from a terminal: xterm's buffer vocabulary
/// (absolute lines, soft-wrap continuation, markers), so terminal.html's ruler logic ports
/// over without reinterpretation.</summary>
internal interface ICommandBuffer
{
    bool IsAlternate { get; }
    int CursorLine { get; }
    int CursorX { get; }
    int ViewportTop { get; }
    int Rows { get; }
    int Length { get; }
    /// <summary>Text of an absolute line with trailing blanks trimmed; null when it does not exist.</summary>
    string? LineText(int line);
    /// <summary>The line continues the one above it (a soft wrap).</summary>
    bool IsWrapped(int line);
    ICommandMarker? CreateMarker(int line);
    /// <summary>Reads a run of lines in one go ahead of <see cref="LineText"/> calls for them.</summary>
    void Prefetch(int first, int count) { }
}

/// <summary>One command mark as the ruler and the commands panel show it.</summary>
internal sealed record CommandMarkInfo(long Id, int Line, int? Exit, bool Exact, string Text, long UnixMs, long? ExecutionId);

/// <summary>
/// Command marks, running-command reports, command history and prompt context for the
/// ghostty surface. A port of addon-ruler.js (Phase 9.4 marks, 9.6 panel data, command
/// history) with the same three mark sources: exact OSC 133 shell integration, OSC 3008
/// results attached to discovered marks, and Enter-gated prompt discovery for hosts with no
/// shell integration. UI thread only; timers go through <see cref="Schedule"/>.
/// </summary>
internal sealed class CommandTracker
{
    private const int EchoSettleMs = 300;
    private const int EchoRetryMs = 900;
    private const int HistoryMaxOutput = 65536;
    private const int HistoryMaxCommand = 4096;
    private const int HistoryIdleMs = 600;

    // ---- prompt shapes (terminal.html / addon-ruler.js) ----------------------------------
    private static readonly TimeSpan RegexTimeout = PromptContextDetector.RegexTimeout;
    private const string UnixSpacedPromptBody =
        @"[^@\s$#%>]{1,100}@[^\s$#%>]{1,100}\s+[^\r\n$#%>]{1,160}?\s*[$#%>]";
    private const string CmdPromptBody =
        @"(?:PS [^\n]{0,200}>|" + UnixSpacedPromptBody + @"|(?:\[[^\]]{1,100}\]|[^\s$#%>]{0,100})[$#%>])";
    internal static readonly Regex CmdPromptRe = new("^" + CmdPromptBody + @"\s?\S", RegexOptions.CultureInvariant, RegexTimeout);
    internal static readonly Regex CmdSplitRe = new("^(" + CmdPromptBody + @")\s?(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout);
    internal static Regex WindowsIdlePromptRe => PromptContextDetector.WindowsIdlePromptRe;
    private static readonly Regex Osc3008TypeRe = new(@"^(service|session|shell|command|vm|container|elevate|chpriv|subcontext|remote|boot|app)$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ControlCharsRe = new(@"[\x00-\x1f\x7f-\x9f]", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex UserHostPromptRe = new(@"^[^\s@]+@[^\s:]+(?:\s|:)", RegexOptions.CultureInvariant, RegexTimeout);

    private sealed class Mark
    {
        public required ICommandMarker Marker;
        public long Id;
        public int? Exit;
        public required bool Exact;
        public string Text = "";
        public string? FullText;
        public long CreatedMs;
        public long? ExecutionId;
        public bool History;
        public bool HistoryDone;
        public long HistoryCommitMs;
    }

    private sealed class Probe
    {
        public required ICommandMarker Marker;
        public string? Osc3008Id;
    }

    private sealed class Osc3008Record
    {
        public Probe? Probe;
        public Mark? Entry;
        public bool Ended;
        public int? Exit;
    }

    private readonly ICommandBuffer _buffer;
    private readonly PromptContextDetector _prompts;
    private readonly Func<long> _now;
    private readonly List<Mark> _marks = [];
    private readonly List<ICommandMarker> _bookmarks = [];
    private readonly List<Probe> _probes = [];
    private readonly Dictionary<string, Osc3008Record> _osc3008 = new(StringComparer.Ordinal);
    private readonly List<string> _osc3008Order = [];

    /// <summary>Where the shell is in OSC 133's A/B → C → D cycle.</summary>
    internal enum CommandPhase { Idle, Prompt, Executing }

    private long _nextMarkId;
    // Per connection: a reconnect can land in a shell without integration, whose commands only
    // Enter-gated discovery and OSC 3008 can see. Cleared by ResetConnection.
    private bool _oscSeen;
    private CommandPhase _phase;
    private ICommandMarker? _promptMarker;
    private int _promptCol = -1;
    // The current command's three faces: its exact mark awaiting D (_pending), its running
    // report (_executionPending) and its history entry (_historyOpen, which discovered marks use
    // too). Each ends on its own signal; ResetConnection ends all three together.
    private Mark? _pending;
    // The live command's mark while a replay runs: the replay's C and D work on its own marks
    // (in _pending), and must neither finish this one nor lose it.
    private Mark? _livePending;

    private bool _executionReporting;
    private long _executionNextId;
    private TerminalCommandExecution? _executionPending;

    private bool _replaying;
    private string? _replayRunning;

    private bool _historyEnabled;
    private Mark? _historyOpen;
    private int _historyIdleGeneration;
    private long _lastOutputMs;

    private int _titlesSeen;

    // An agent owns the screen (see SetAgentRunning). _agentCommandAt is where its last
    // ignored OSC 133 C left the cursor, so the D that closes it can be told from the shell's.
    private bool _agentRunning;
    private (int Line, int X)? _agentCommandAt;

    /// <summary>Runs an action on the UI thread after a delay.</summary>
    public required Action<TimeSpan, Action> Schedule { get; init; }

    /// <summary>The tab subtitle's running command: (text, exact); "" means it ended.</summary>
    public event Action<string, bool>? RunningCommand;
    public event Action<TerminalCommandExecution>? CommandExecution;
    public event Action<TerminalCommandRecord>? CommandRecorded;
    public event Action<string, string?>? PromptContext;
    /// <summary>Command text of each new mark (agent detection; never reports an end).</summary>
    public event Action<string>? CommandMarked;
    /// <summary>Marks, bookmarks or exits changed: repaint the ruler and panel.</summary>
    public event Action? Changed;

    public CommandTracker(ICommandBuffer buffer, Func<long>? now = null)
    {
        _buffer = buffer;
        _prompts = new PromptContextDetector(buffer);
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool ShellIntegrationSeen => _oscSeen;

    // ---- inputs from the surface ----------------------------------------------------------

    public void NoteTitleChanged() => _titlesSeen++;

    public void NoteOutput(long unixMs) => _lastOutputMs = unixMs;

    /// <summary>After output was parsed (terminal.html's onWriteParsed): drop marks whose
    /// lines left the scrollback, re-read the idle prompt, re-arm history's idle check.</summary>
    public void OnOutputParsed()
    {
        PruneDisposed();
        ReportPromptContext(force: false);
        HistoryScheduleIdle();
    }

    /// <summary>OSC 133 as libghostty-vt reports it: kind 1..4 = A..D, with the cursor
    /// position and a marker on the cursor line captured while the output was parsed.</summary>
    public void OnSemanticPrompt(int kind, int promptKind, int? exit, ICommandMarker? cursorMarker, int cursorX, string command)
    {
        var used = false;
        try
        {
            if (_buffer.IsAlternate)
                return;
            if (_agentRunning && IsAgentSemanticPrompt(kind, cursorMarker, cursorX))
                return;
            _oscSeen = true;
            if (kind is 1 or 2)
            {
                // Continuation, secondary and right prompts belong to the same command.
                if (kind == 1 && promptKind != 0)
                    return;
                if (kind == 1)
                    FinishExecution(null);
                if (kind == 1 && _phase == CommandPhase.Executing)
                    FireCommand("", null, exact: true);
                // A B with no A is still the same command running (unchanged from before).
                if (kind == 1 || _phase == CommandPhase.Idle)
                    _phase = CommandPhase.Prompt;
                _promptMarker?.Dispose();
                _promptMarker = cursorMarker;
                used = cursorMarker is not null;
                // B arrives with the prompt drawn and the cursor at the input start; A is
                // the prompt's own start, useless for slicing the command text out.
                _promptCol = kind == 2 ? cursorX : -1;
            }
            else if (kind == 3)
            {
                _phase = CommandPhase.Executing;
                if (_promptMarker is { IsDisposed: false, Line: >= 0 } prompt)
                {
                    var line = prompt.Line;
                    var text = command.Length > 0 ? Truncate(command.Trim(), 256) : CmdText(line, _promptCol);
                    if (text.Length > 0)
                        FireCommand(text, null, exact: true);
                    _pending = Commit(line, null, exact: true, null, text);
                    if (_pending is not null)
                        _pending.FullText = command.Length > 0 ? Truncate(command.Trim(), HistoryMaxCommand) : CmdText(line, _promptCol, HistoryMaxCommand);
                    if (_executionReporting && !_replaying && text.Length > 0 && !PromptContextDetector.SafeIsMatch(ControlCharsRe, text) && _executionNextId < long.MaxValue)
                    {
                        FinishExecution(null);
                        var execution = new TerminalCommandExecution(++_executionNextId, Truncate(text, 256), false, null);
                        _executionPending = execution;
                        if (_pending is not null)
                            _pending.ExecutionId = execution.Id;
                        CommandExecution?.Invoke(execution);
                    }
                    ClearPrompt();
                }
            }
            else if (kind == 4)
            {
                _phase = CommandPhase.Idle;
                FinishExecution(exit);
                if (_pending is not null)
                {
                    var finished = _pending;
                    finished.Exit = exit;
                    _pending = null;
                    Changed?.Invoke();
                    HistoryFlush(finished, lost: false);
                }
                else if (_promptMarker is { IsDisposed: false, Line: >= 0 } prompt)
                {
                    // A shell that sends A and D but never C: this D still belongs to what was
                    // typed at the last prompt (empty Enters get a mark too, indistinguishably).
                    var line = prompt.Line;
                    var late = Commit(line, exit, exact: true, null, CmdText(line, _promptCol));
                    if (late is not null)
                    {
                        late.FullText = CmdText(line, _promptCol, HistoryMaxCommand);
                        HistoryFlush(late, lost: false);
                    }
                    ClearPrompt();
                }
                FireCommand("", null, exact: true); // the command is over, whatever it was
            }
        }
        finally
        {
            if (!used)
                cursorMarker?.Dispose();
        }
    }

    private void ClearPrompt()
    {
        _promptMarker?.Dispose();
        _promptMarker = null;
        _promptCol = -1;
    }

    /// <summary>
    /// The host's agent detection says an agent is running here. What it draws in the normal
    /// screen is its transcript, not shell commands: oh-my-pi brackets every user message with
    /// OSC 133 (A before it, B C D;0 after it) for prompt navigation, and agents' editors and
    /// transcripts have prompt-shaped lines ("> fix it", "$ make"). Those are not marked,
    /// reported or recorded. The shell's own D when the agent exits still ends the command
    /// that started it, and is what retires the agent on hosts that never retitle.
    /// </summary>
    public void SetAgentRunning(bool running)
    {
        _agentRunning = running;
        _agentCommandAt = null;
    }

    /// <summary>While an agent runs, every A, B and C is its own. A D is its own when it closes
    /// the agent's C at the same cursor position (oh-my-pi writes B C D;0 as one string);
    /// anything else is the shell reporting that the command running the agent finished.</summary>
    private bool IsAgentSemanticPrompt(int kind, ICommandMarker? cursorMarker, int cursorX)
    {
        var at = (cursorMarker is { IsDisposed: false } marker ? marker.Line : -1, cursorX);
        var closesAgentCommand = _agentCommandAt == at;
        _agentCommandAt = kind == 3 ? at : null;
        return kind != 4 || closesAgentCommand;
    }

    /// <summary>Transport cancellation and playback are not shell completion evidence.</summary>
    public void SetExecutionReporting(bool enabled)
    {
        _executionReporting = enabled;
        _executionPending = null;
        _pending = null;
        _livePending = null;
        if (!enabled)
            ClearPrompt();
    }

    /// <summary>
    /// The connection ended. Everything about "the command in progress" belonged to it: the
    /// phase, the pending mark, the running report, the open history entry, a replay the stream
    /// was cut in the middle of, OSC 3008 contexts and whether the shell had integration. The
    /// open history entry is recorded with the output it has (the next shell's output is not
    /// its); the running report and the tab's running command end without events, because a
    /// dropped transport says nothing about how the command finished (see
    /// <see cref="SetExecutionReporting"/>; the host clears its own state on disconnect).
    /// Marks stay on the ruler.
    /// </summary>
    public void ResetConnection()
    {
        FlushHistory();
        _phase = CommandPhase.Idle;
        _pending = null;
        _livePending = null;
        _executionPending = null;
        ClearPrompt();
        _replaying = false;
        _replayRunning = null;
        _oscSeen = false;
        _osc3008.Clear();
        _osc3008Order.Clear();
        foreach (var probe in _probes)
            probe.Osc3008Id = null;
        _prompts.ResetSignature();
        _agentCommandAt = null;
    }

    internal CommandPhase Phase => _phase;

    private void FinishExecution(int? exitCode)
    {
        // A replay's A and D are history: they finish nothing that runs now.
        if (_replaying)
            return;
        var pending = _executionPending;
        _executionPending = null;
        if (pending is null)
            return;
        CommandExecution?.Invoke(pending with { Completed = true, ExitCode = exitCode });
    }

    // ---- OSC 3008 -------------------------------------------------------------------------

    /// <summary>UAPI.15 context signals attach a stable command id and an exact result to a
    /// discovered mark; the stock systemd hook sends no command text, so discovery still
    /// owns the mark.</summary>
    public void OnOsc3008(string data)
    {
        var parsed = ParseOsc3008(data);
        if (parsed is not { } p || _oscSeen)
            return;
        if (p.Action == "start")
        {
            if (p.Type != "command")
                return;
            var record = new Osc3008Record();
            _osc3008[p.Id] = record;
            _osc3008Order.Remove(p.Id);
            _osc3008Order.Add(p.Id);
            while (_osc3008Order.Count > 64)
            {
                _osc3008.Remove(_osc3008Order[0]);
                _osc3008Order.RemoveAt(0);
            }
            foreach (var probe in _probes)
            {
                if (!probe.Marker.IsDisposed && probe.Osc3008Id is null)
                {
                    probe.Osc3008Id = p.Id;
                    record.Probe = probe;
                    break;
                }
            }
            return;
        }
        if (!_osc3008.TryGetValue(p.Id, out var current))
            return;
        current.Ended = true;
        current.Exit = p.Status ?? (p.Exit == "success" ? 0 : null);
        if (current.Entry is { } entry)
        {
            if (current.Exit is { } code)
                entry.Exit = code;
            RemoveOsc3008(p.Id);
            Changed?.Invoke();
            HistoryFlush(entry, lost: false);
        }
        else if (current.Probe is null || current.Probe.Marker.IsDisposed)
        {
            RemoveOsc3008(p.Id);
        }
    }

    private void RemoveOsc3008(string id)
    {
        _osc3008.Remove(id);
        _osc3008Order.Remove(id);
    }

    internal readonly record struct Osc3008(string Action, string Id, string? Type, string? Exit, int? Status);

    /// <summary>Reads the fields the tracker uses, accepting exactly what Core's
    /// Osc3008ContextParser accepts (this library cannot reference Core; a shared test vector
    /// keeps them agreeing): type only on start, exit and status only on end, and no C0,
    /// DEL or C1 control anywhere.</summary>
    internal static Osc3008? ParseOsc3008(string data)
    {
        if (string.IsNullOrEmpty(data) || data.Length > 4096 || data.Any(char.IsControl))
            return null;
        var fields = data.Split(';');
        var first = Regex.Match(fields[0], "^(start|end)=(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!first.Success || first.Groups[2].Length is 0 or > 256)
            return null;
        var raw = first.Groups[2].Value;
        var id = new System.Text.StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\\')
            {
                if (c < 32 || c > 126)
                    return null;
                id.Append(c);
                continue;
            }
            var escape = raw.Substring(i, Math.Min(4, raw.Length - i));
            if (escape == "\\x3b") id.Append(';');
            else if (escape == "\\x5c") id.Append('\\');
            else return null;
            i += 3;
        }
        if (id.Length is 0 or > 64)
            return null;
        var start = first.Groups[1].Value == "start";
        string? type = null, exit = null;
        int? status = null;
        foreach (var field in fields.Skip(1))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = field[..separator];
            var value = field[(separator + 1)..];
            if (start && key == "type" && PromptContextDetector.SafeIsMatch(Osc3008TypeRe, value))
                type = value;
            else if (!start && key == "exit" && value is "success" or "failure" or "crash" or "interrupt")
                exit = value;
            else if (!start && key == "status" && value.Length > 0 && value.All(char.IsAsciiDigit)
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s <= 255)
                status = s;
        }
        return new Osc3008(first.Groups[1].Value, id.ToString(), type, exit, status);
    }

    // ---- command text ---------------------------------------------------------------------

    /// <summary>First logical line starting at (line, col), following soft wraps: the command
    /// text. col -1 means unknown (no 133;B): fall back to the prompt regex.</summary>
    internal string CmdText(int line, int col, int maxLength = 256)
    {
        var full = _buffer.LineText(line);
        if (full is null)
            return "";
        for (var r = line + 1; full.Length < Math.Max(col, 0) + 512 + maxLength; r++)
        {
            var next = _buffer.LineText(r);
            if (next is null || !_buffer.IsWrapped(r))
                break;
            full += next;
        }
        if (col < 0)
        {
            var m = PromptContextDetector.SafeMatch(CmdSplitRe, full);
            if (!m.Success || m.Groups[2].Length == 0)
                return "";
            col = full.Length - m.Groups[2].Length;
        }
        else if (col == 0)
        {
            // tmux can pass B through before flushing its buffered prompt, leaving B at
            // column zero. Recover only strong user@host or PowerShell prompt shapes.
            var prompt = PromptContextDetector.SafeMatch(CmdSplitRe, full);
            if (prompt.Success && IsShellPrompt(prompt.Groups[1].Value))
                col = full.Length - prompt.Groups[2].Length;
        }
        if (col > full.Length)
            return "";
        return Truncate(full[col..].Trim(), maxLength);
    }

    /// <summary>A prompt shape only a shell draws: user@host, or PowerShell's.</summary>
    private static bool IsShellPrompt(string prompt) =>
        PromptContextDetector.SafeIsMatch(UserHostPromptRe, prompt) || prompt.StartsWith("PS ", StringComparison.Ordinal);

    /// <summary>
    /// A persistent session is sending its screen again after a reattach. The marks in it
    /// are old: they are kept for the ruler, but nothing in them is run, finished or
    /// recorded now. When it ends, the running command is reported once, as it stands.
    /// A command still running at that point is not recorded when it finishes. A live
    /// command's mark set aside at the start is not finished by the replay's D; it is
    /// pending again afterwards unless the replay ended inside a command of its own, which
    /// is then the one the screen shows running.
    /// </summary>
    public void SetReplaying(bool replaying)
    {
        if (_replaying == replaying)
            return;
        _replaying = replaying;
        if (replaying)
        {
            _replayRunning = null;
            _livePending = _pending;
            _pending = null;
            return;
        }
        _pending ??= _livePending;
        _livePending = null;
        if (_replayRunning is { } running)
            RunningCommand?.Invoke(running, true);
        _replayRunning = null;
    }

    private void FireCommand(string text, int? epoch, bool exact)
    {
        if (_replaying)
        {
            _replayRunning = text;
            return;
        }
        // A discovered command loses to a title that arrived after its Enter: the command
        // already finished, or the program titled itself.
        if (epoch is { } e && e != _titlesSeen)
            return;
        RunningCommand?.Invoke(text, exact);
    }

    // ---- Enter-gated discovery ------------------------------------------------------------

    /// <summary>The user submitted input containing Enter. The cursor row is anchored and,
    /// once the remote echo settles, tested against the prompt shapes.</summary>
    public void NotifyEnter()
    {
        if (_oscSeen || _buffer.IsAlternate)
            return;
        _prompts.ResetSignature();
        var marker = _buffer.CreateMarker(_buffer.CursorLine);
        if (marker is null)
            return;
        var probe = new Probe { Marker = marker };
        _probes.Add(probe);
        var epoch = _titlesSeen;
        var attempts = 0;
        var reported = false;

        (int Row, string Text, string FullText)? ReadCommand()
        {
            if (_buffer.IsAlternate)
                return null;
            var row = marker.Line;
            if (row < 0)
                return null;
            while (row > 0 && _buffer.IsWrapped(row))
                row--;
            var lineText = _buffer.LineText(row) ?? "";
            var match = PromptContextDetector.SafeIsMatch(CmdPromptRe, lineText)
                ? PromptContextDetector.SafeMatch(CmdSplitRe, lineText)
                : null;
            if (match is not { Success: true })
                return null;
            // An agent's editor line ("> fix it") is not a command line; a shell prompt
            // under it means the agent has exited.
            if (_agentRunning && !IsShellPrompt(match.Groups[1].Value))
                return null;
            var col = lineText.Length - match.Groups[2].Length;
            return (row, CmdText(row, col), CmdText(row, col, HistoryMaxCommand));
        }

        // Most input was echoed before Enter: capture it now, because a program such as
        // nano can switch buffers before the settle timer runs.
        var atEnter = ReadCommand();
        if (atEnter is { } visible)
        {
            reported = true;
            FireCommand(visible.Text, epoch, exact: false);
        }

        void Evaluate()
        {
            if (marker.IsDisposed || marker.Line < 0)
            {
                FinishProbe(probe);
                return;
            }
            if (_oscSeen)
            {
                FinishProbe(probe);
                return;
            }
            attempts++;
            if (ReadCommand() is { } command)
            {
                // An echo only grows after Enter ("cod" -> "codex"). Different text means the
                // anchored line moved under a full-screen program's redraw: committing it would
                // mark (and record) an old command with everything since as its output.
                if (atEnter is { Text.Length: > 0 } before && !command.Text.StartsWith(before.Text, StringComparison.Ordinal))
                {
                    FinishProbe(probe);
                    return;
                }
                if (!reported)
                {
                    reported = true;
                    FireCommand(command.Text, epoch, exact: false);
                }
                var guessed = Commit(command.Row, null, exact: false, probe.Osc3008Id, command.Text);
                if (guessed is not null && guessed.FullText is null)
                    guessed.FullText = command.FullText;
                FinishProbe(probe);
                return;
            }
            if (attempts < 2)
                Schedule(TimeSpan.FromMilliseconds(EchoRetryMs), Evaluate);
            else
                FinishProbe(probe);
        }
        Schedule(TimeSpan.FromMilliseconds(EchoSettleMs), Evaluate);
    }

    private void FinishProbe(Probe probe)
    {
        probe.Marker.Dispose();
        if (!_probes.Remove(probe))
            return; // a settle timer can outlive pruning or tracker disposal
        if (probe.Osc3008Id is not { } id || !_osc3008.TryGetValue(id, out var record))
            return;
        record.Probe = null;
        if (record.Ended && record.Entry is null)
            RemoveOsc3008(id);
    }

    // ---- marks ----------------------------------------------------------------------------

    /// <summary>Adds a command mark at an absolute line (idempotent per line; an exit code
    /// updates an existing mark in place).</summary>
    private Mark? Commit(int line, int? exit, bool exact, string? contextId, string? text)
    {
        foreach (var existing in _marks)
        {
            if (existing.Marker.Line != line)
                continue;
            if (exit is not null)
                existing.Exit = exit;
            if (!string.IsNullOrEmpty(text) && existing.Text.Length == 0)
                existing.Text = text;
            Associate(contextId, existing);
            Changed?.Invoke();
            return existing;
        }
        var marker = _buffer.CreateMarker(line);
        if (marker is null)
            return null;
        var entry = new Mark { Marker = marker, Id = ++_nextMarkId, Exit = exit, Exact = exact, Text = text ?? "", CreatedMs = _now() };
        _marks.Add(entry);
        Associate(contextId, entry);
        HistoryBegin(entry);
        NotifyCommandMarked(line);
        Changed?.Invoke();
        return entry;
    }

    private void Associate(string? contextId, Mark entry)
    {
        if (contextId is null || !_osc3008.TryGetValue(contextId, out var record))
            return;
        record.Entry = entry;
        record.Probe = null;
        if (!record.Ended)
            return;
        if (record.Exit is { } code)
            entry.Exit = code;
        RemoveOsc3008(contextId);
    }

    private void NotifyCommandMarked(int line)
    {
        var text = (_buffer.LineText(line) ?? "").Trim();
        var match = PromptContextDetector.SafeMatch(CmdSplitRe, text);
        var command = match.Success ? match.Groups[2].Value : text;
        if (command.Length > 0 && !_replaying)
            CommandMarked?.Invoke(command);
    }

    /// <summary>Marks whose lines were trimmed out of the scrollback: keep the command for
    /// history, say its output is gone.</summary>
    private void PruneDisposed()
    {
        if (_promptMarker is { Line: < 0 })
            ClearPrompt();
        for (var i = _probes.Count - 1; i >= 0; i--)
        {
            if (_probes[i].Marker.Line < 0)
                FinishProbe(_probes[i]);
        }
        var changed = false;
        for (var i = _marks.Count - 1; i >= 0; i--)
        {
            var entry = _marks[i];
            if (entry.Marker.Line >= 0)
                continue;
            if (entry.History && !entry.HistoryDone)
                HistoryFlush(entry, lost: true);
            entry.Marker.Dispose();
            _marks.RemoveAt(i);
            if (_livePending == entry)
                _livePending = null;
            if (_pending == entry)
                _pending = null;
            changed = true;
        }
        for (var i = _bookmarks.Count - 1; i >= 0; i--)
        {
            if (_bookmarks[i].Line >= 0)
                continue;
            _bookmarks[i].Dispose();
            _bookmarks.RemoveAt(i);
            changed = true;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Toggles a bookmark on the cursor's line. Returns true when one was added.</summary>
    public bool ToggleBookmark()
    {
        if (_buffer.IsAlternate)
            return false;
        var cursorLine = _buffer.CursorLine;
        var existing = _bookmarks.FindIndex(b => b.Line == cursorLine);
        if (existing >= 0)
        {
            _bookmarks[existing].Dispose();
            _bookmarks.RemoveAt(existing);
            Changed?.Invoke();
            return false;
        }
        var marker = _buffer.CreateMarker(cursorLine);
        if (marker is null)
            return false;
        _bookmarks.Add(marker);
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<int> BookmarkLines() =>
        [.. _bookmarks.Select(b => b.Line).Where(l => l >= 0).OrderBy(l => l)];

    /// <summary>All command marks ascending by line.</summary>
    public IReadOnlyList<CommandMarkInfo> Commands()
    {
        var result = new List<CommandMarkInfo>(_marks.Count);
        foreach (var entry in _marks.OrderBy(m => m.Marker.Line))
        {
            var line = entry.Marker.Line;
            if (line < 0)
                continue;
            var text = entry.Text.Length > 0 ? entry.Text : CmdText(line, -1);
            if (text.Length == 0)
                text = Truncate((_buffer.LineText(line) ?? "").Trim(), 256);
            result.Add(new CommandMarkInfo(entry.Id, line, entry.Exit, entry.Exact, text, entry.CreatedMs, entry.ExecutionId));
        }
        return result;
    }

    /// <summary>The nearest mark above (dir &lt; 0) or below (dir &gt; 0) the viewport center.</summary>
    public int? JumpTarget(int direction)
    {
        if (_buffer.IsAlternate || _marks.Count == 0)
            return null;
        var center = _buffer.ViewportTop + _buffer.Rows / 2;
        int? best = null;
        foreach (var entry in _marks)
        {
            var line = entry.Marker.Line;
            if (line < 0 || (direction > 0 ? line <= center : line >= center))
                continue;
            if (best is null || (direction > 0 ? line < best : line > best))
                best = line;
        }
        return best;
    }

    public int? LineForMark(long id) =>
        _marks.FirstOrDefault(m => m.Id == id) is { } mark && mark.Marker.Line >= 0 ? mark.Marker.Line : null;

    public int? LineForExecution(long id) =>
        _marks.FirstOrDefault(m => m.ExecutionId == id) is { } mark && mark.Marker.Line >= 0 ? mark.Marker.Line : null;

    /// <summary>The command marked at <paramref name="line"/> as a transcript: its own prompt
    /// line first, then everything up to the next mark (soft wraps joined). The live idle
    /// prompt and trailing blank or empty-Enter prompt lines are dropped (recognized by
    /// equality with this or the next mark's prompt, never by shape). "" when nothing is left.</summary>
    public string CommandOutput(int line)
    {
        string? PromptAt(int row)
        {
            var text = _buffer.LineText(row);
            if (text is null)
                return null;
            var m = PromptContextDetector.SafeMatch(CmdSplitRe, text);
            return m.Success ? m.Groups[1].Value : null;
        }

        var start = line + 1;
        while (_buffer.LineText(start) is not null && _buffer.IsWrapped(start))
            start++;
        var end = _buffer.Length;
        var nextMarkLine = -1;
        foreach (var entry in _marks)
        {
            var markLine = entry.Marker.Line;
            if (markLine > line && markLine < end)
            {
                end = markLine;
                nextMarkLine = markLine;
            }
        }
        var cursor = _buffer.CursorLine;
        while (cursor > 0 && _buffer.IsWrapped(cursor))
            cursor--;
        if (cursor > line && cursor < end)
            end = cursor;

        var output = new List<string>();
        for (var r = start; r < end; r++)
        {
            var text = _buffer.LineText(r);
            if (text is null)
                continue;
            if (_buffer.IsWrapped(r) && output.Count > 0)
                output[^1] += text;
            else
                output.Add(text);
        }
        var ownPrompt = PromptAt(line);
        var nextPrompt = nextMarkLine >= 0 ? PromptAt(nextMarkLine) : null;
        while (output.Count > 0)
        {
            var last = output[^1];
            if (last.Length != 0 && last != ownPrompt && last != nextPrompt)
                break;
            output.RemoveAt(output.Count - 1);
        }
        if (output.Count == 0)
            return "";
        var head = _buffer.LineText(line) ?? "";
        for (var w = line + 1; w < start; w++)
            head += _buffer.LineText(w) ?? "";
        if (head.Length > 0)
            output.Insert(0, head);
        return string.Join("\n", output);
    }

    // ---- command history ------------------------------------------------------------------

    /// <summary>Host opt-in. Turning it off drops the open command without recording it.</summary>
    public void SetHistoryCapture(bool enabled)
    {
        _historyEnabled = enabled;
        if (!enabled)
        {
            if (_historyOpen is not null)
                _historyOpen.HistoryDone = true;
            _historyOpen = null;
        }
    }

    /// <summary>Records the open command now with the output it has so far.</summary>
    public void FlushHistory()
    {
        if (_historyOpen is not null)
            HistoryFlush(_historyOpen, lost: false);
    }

    private void HistoryBegin(Mark entry)
    {
        if (!_historyEnabled || _replaying)
            return;
        var previous = _historyOpen;
        if (previous is not null && previous != entry && !previous.HistoryDone)
            HistoryFlush(previous, lost: false);
        entry.History = true;
        entry.HistoryCommitMs = _now();
        _historyOpen = entry;
        HistoryScheduleIdle();
    }

    /// <summary>Discovered commands have no end signal: output going quiet at a prompt-shaped
    /// cursor line below the command means the next prompt has arrived.</summary>
    private void HistoryScheduleIdle()
    {
        var open = _historyOpen;
        if (open is null || open.HistoryDone || open.Exact)
            return;
        var generation = ++_historyIdleGeneration;
        Schedule(TimeSpan.FromMilliseconds(HistoryIdleMs), () =>
        {
            if (generation == _historyIdleGeneration && _historyOpen == open && !open.HistoryDone && HistoryAtIdlePrompt(open))
                HistoryFlush(open, lost: false);
        });
    }

    private bool HistoryAtIdlePrompt(Mark entry)
    {
        if (entry.Marker.Line < 0 || _buffer.IsAlternate)
            return false;
        var row = _buffer.CursorLine;
        while (row > 0 && _buffer.IsWrapped(row))
            row--;
        if (row <= entry.Marker.Line)
            return false;
        var text = (_buffer.LineText(row) ?? "").TrimEnd();
        if (text.Length == 0)
            return false;
        var split = PromptContextDetector.SafeMatch(CmdSplitRe, text);
        return (split.Success && split.Groups[2].Length == 0)
            || PromptContextDetector.IsKnownIdlePrompt(text);
    }

    private void HistoryFlush(Mark entry, bool lost)
    {
        if (entry.HistoryDone)
            return;
        entry.HistoryDone = true;
        if (_historyOpen == entry)
        {
            _historyOpen = null;
            _historyIdleGeneration++;
        }
        if (!_historyEnabled || !entry.History || CommandRecorded is null)
            return;

        var line = lost ? -1 : entry.Marker.Line;
        var command = entry.FullText is { Length: > 0 } full ? full : entry.Text;
        if (command.Length == 0 && line >= 0)
            command = CmdText(line, -1);
        command = Truncate(command.Trim(), HistoryMaxCommand);
        if (command.Length == 0)
            return;

        var output = "";
        var truncated = false;
        var startedMs = entry.HistoryCommitMs;
        long? endedMs = null;
        if (line >= 0)
        {
            var transcript = CommandOutput(line);
            var newline = transcript.IndexOf('\n');
            output = newline >= 0 ? transcript[(newline + 1)..] : "";
            if (output.Length > HistoryMaxOutput)
            {
                output = output[..HistoryMaxOutput];
                truncated = true;
            }
            endedMs = entry.Exact || entry.Exit is not null ? _now() : _lastOutputMs > 0 ? _lastOutputMs : null;
            if (endedMs < startedMs)
                endedMs = startedMs;
        }
        CommandRecorded(new TerminalCommandRecord(
            command, entry.Exit, entry.Exact, startedMs, endedMs, output, truncated, lost,
            line >= 0 ? HistoryPromptDirectory(line) : null));
    }

    /// <summary>The folder a command ran in, read from its own prompt line.</summary>
    private string? HistoryPromptDirectory(int line)
    {
        var text = _buffer.LineText(line);
        if (text is null)
            return null;
        var split = PromptContextDetector.SafeMatch(CmdSplitRe, text);
        return split.Success ? PromptContextDetector.ExtractDirectory(split.Groups[1].Value) : null;
    }

    // ---- prompt context -------------------------------------------------------------------

    /// <summary>Native hint from a saved icon or SSH banner; it only permits prompt
    /// interpretation, screen evidence still owns the displayed context. A null or
    /// non-network platform withdraws an earlier hint (the session's icon changed).</summary>
    public void SetPromptPlatform(string? platform)
    {
        if (_prompts.SetPlatformHint(platform))
            ReportPromptContext(force: false);
    }

    /// <summary>Reports the current location label from a completed known prompt. The cursor
    /// line is part of the signature, so returning to the same context after a command still
    /// reports that the command ended. Returns the label (or null).</summary>
    public (string Context, string? Platform)? ReportPromptContext(bool force)
    {
        if (_phase == CommandPhase.Executing || _buffer.IsAlternate)
            return null;
        if (_prompts.Detect(force) is not var (result, changed))
            return null;
        if (changed)
            PromptContext?.Invoke(result.Context, result.Platform);
        return result;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] : text;

    public void Dispose()
    {
        foreach (var mark in _marks)
            mark.Marker.Dispose();
        foreach (var bookmark in _bookmarks)
            bookmark.Dispose();
        foreach (var probe in _probes)
            probe.Marker.Dispose();
        _promptMarker?.Dispose();
        _marks.Clear();
        _bookmarks.Clear();
        _probes.Clear();
    }
}
