using System.Text.RegularExpressions;

namespace Resesh.Terminal.Ghostty;

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
internal sealed class GhosttyCommandTracker
{
    private const int EchoSettleMs = 300;
    private const int EchoRetryMs = 900;
    private const int HistoryMaxOutput = 65536;
    private const int HistoryMaxCommand = 4096;
    private const int HistoryIdleMs = 600;

    // ---- prompt shapes (terminal.html / addon-ruler.js) ----------------------------------
    private const string UnixSpacedPromptBody =
        @"[^@\s$#%>]{1,100}@[^\s$#%>]{1,100}\s+[^\r\n$#%>]{1,160}?\s*[$#%>]";
    private const string CmdPromptBody =
        @"(?:PS [^\n]{0,200}>|" + UnixSpacedPromptBody + @"|(?:\[[^\]]{1,100}\]|[^\s$#%>]{0,100})[$#%>])";
    internal static readonly Regex CmdPromptRe = new("^" + CmdPromptBody + @"\s?\S", RegexOptions.CultureInvariant);
    internal static readonly Regex CmdSplitRe = new("^(" + CmdPromptBody + @")\s?(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex HistoryCompactPromptRe = new(@"^[^@\s:]{1,100}@[^\s:]{1,100}:([^\s$#%>]{1,512})[$#%]$", RegexOptions.CultureInvariant);
    internal static readonly Regex WindowsIdlePromptRe = new(@"^(?:PS )?((?:[A-Za-z]:[\\/]|\\\\)[^\r\n>]*)>\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex UnixSpacedIdlePromptRe = new(@"^[^@\s$#%>]{1,100}@[^\s$#%>]{1,100}\s+([^\r\n$#%>]{1,160}?)\s*[$#%>]\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex UnixBracketedIdlePromptRe = new(@"^\[[^@\]\s]{1,100}@[^\]\s]{1,100}\s+([^\]\r\n]{1,160}?)\]\s*[$#%>]\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex NokiaMdCliPromptRe = new(@"^[A-Z]:[^@\s]+@[^\s#]+#\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex NokiaMdCliContextRe = new(@"^(\*)?(?:\((gl|ex|pr|ro)\)\s*)?\[((?:(gl|ex|pr|ro):)?[^\]\r\n]{0,500})\]$", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> NokiaMdCliModes = new()
    {
        ["gl"] = "global", ["ex"] = "exclusive", ["pr"] = "private", ["ro"] = "read-only",
    };
    private static readonly Regex JunosPromptRe = new(@"^[^@\s]+@[^\s#>]+([#>])\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex JunosEditContextRe = new(@"^(?:\{([^}\r\n]{1,100})\}\s*)?\[edit(?:\s+([^\]\r\n]{1,400}))?\]\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex JunosRoleRe = new(@"^\{((?:master|backup|primary|secondary)(?::[^}\s]+)?)\}\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex JunosBannerRe = new(@"^---\s*JUNOS\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CiscoIosPromptRe = new(@"^([A-Za-z0-9][A-Za-z0-9._-]{0,100})(?:\(([^()\r\n]{1,100})\))?([#>])\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex CiscoXrPromptRe = new(@"^(RP/\d+/(?:RP|RSP)\d+/CPU\d+):([^()\s#>]+)(?:\(([^()\r\n]{1,100})\))?([#>])\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CiscoBannerRe = new(@"\b(?:Cisco IOS(?: XE| XR)? Software|Cisco Internetwork Operating System Software)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string> CiscoSubmodeNames = new()
    {
        ["if"] = "interface", ["if-pre"] = "preconfigured interface", ["subif"] = "subinterface",
        ["router"] = "routing", ["line"] = "line", ["vlan"] = "VLAN", ["vrf"] = "VRF",
        ["std-nacl"] = "standard ACL", ["ext-nacl"] = "extended ACL", ["bgp"] = "BGP",
        ["bgp-af"] = "BGP address family", ["bgp-nbr"] = "BGP neighbor",
        ["bgp-nbr-af"] = "BGP neighbor address family",
    };
    private static readonly Regex Osc3008TypeRe = new(@"^(service|session|shell|command|vm|container|elevate|chpriv|subcontext|remote|boot|app)$", RegexOptions.CultureInvariant);
    private static readonly Regex ControlCharsRe = new(@"[\x00-\x1f\x7f-\x9f]", RegexOptions.CultureInvariant);

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
    private readonly Func<long> _now;
    private readonly List<Mark> _marks = [];
    private readonly List<ICommandMarker> _bookmarks = [];
    private readonly List<Probe> _probes = [];
    private readonly Dictionary<string, Osc3008Record> _osc3008 = new(StringComparer.Ordinal);
    private readonly List<string> _osc3008Order = [];

    private long _nextMarkId;
    private bool _oscSeen;
    private bool _executing;
    private ICommandMarker? _promptMarker;
    private int _promptCol = -1;
    private Mark? _pending;

    private bool _executionReporting;
    private long _executionNextId;
    private TerminalCommandExecution? _executionPending;

    private bool _historyEnabled;
    private Mark? _historyOpen;
    private int _historyIdleGeneration;
    private long _lastOutputMs;

    private string? _promptPlatform;
    private string? _lastPromptSignature;
    private int _titlesSeen;

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

    public GhosttyCommandTracker(ICommandBuffer buffer, Func<long>? now = null)
    {
        _buffer = buffer;
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
            _oscSeen = true;
            if (kind is 1 or 2)
            {
                // Continuation, secondary and right prompts belong to the same command.
                if (kind == 1 && promptKind != 0)
                    return;
                if (kind == 1)
                    FinishExecution(null);
                if (kind == 1 && _executing)
                {
                    _executing = false;
                    FireCommand("", null, exact: true);
                }
                _promptMarker?.Dispose();
                _promptMarker = cursorMarker;
                used = cursorMarker is not null;
                // B arrives with the prompt drawn and the cursor at the input start; A is
                // the prompt's own start, useless for slicing the command text out.
                _promptCol = kind == 2 ? cursorX : -1;
            }
            else if (kind == 3)
            {
                _executing = true;
                if (_promptMarker is { IsDisposed: false, Line: >= 0 } prompt)
                {
                    var line = prompt.Line;
                    var text = command.Length > 0 ? Truncate(command.Trim(), 256) : CmdText(line, _promptCol);
                    if (text.Length > 0)
                        FireCommand(text, null, exact: true);
                    _pending = Commit(line, null, exact: true, null, text);
                    if (_pending is not null)
                        _pending.FullText = command.Length > 0 ? Truncate(command.Trim(), HistoryMaxCommand) : CmdText(line, _promptCol, HistoryMaxCommand);
                    if (_executionReporting && text.Length > 0 && !ControlCharsRe.IsMatch(text) && _executionNextId < long.MaxValue)
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
                _executing = false;
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

    /// <summary>Transport cancellation and playback are not shell completion evidence.</summary>
    public void SetExecutionReporting(bool enabled)
    {
        _executionReporting = enabled;
        _executionPending = null;
        _pending = null;
        if (!enabled)
            ClearPrompt();
    }

    private void FinishExecution(int? exitCode)
    {
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

    internal static Osc3008? ParseOsc3008(string data)
    {
        if (string.IsNullOrEmpty(data) || data.Length > 4096 || data.Any(c => c < 32 || c == 127))
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
        string? type = null, exit = null;
        int? status = null;
        foreach (var field in fields.Skip(1))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = field[..separator];
            var value = field[(separator + 1)..];
            if (key == "type" && Osc3008TypeRe.IsMatch(value))
                type = value;
            else if (key == "exit" && value is "success" or "failure" or "crash" or "interrupt")
                exit = value;
            else if (key == "status" && value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit)
                && int.TryParse(value, out var s) && s <= 255)
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
            var m = CmdSplitRe.Match(full);
            if (!m.Success || m.Groups[2].Length == 0)
                return "";
            col = full.Length - m.Groups[2].Length;
        }
        else if (col == 0)
        {
            // tmux can pass B through before flushing its buffered prompt, leaving B at
            // column zero. Recover only strong user@host or PowerShell prompt shapes.
            var prompt = CmdSplitRe.Match(full);
            if (prompt.Success && (Regex.IsMatch(prompt.Groups[1].Value, @"^[^\s@]+@[^\s:]+(?:\s|:)")
                || prompt.Groups[1].Value.StartsWith("PS ", StringComparison.Ordinal)))
                col = full.Length - prompt.Groups[2].Length;
        }
        if (col > full.Length)
            return "";
        return Truncate(full[col..].Trim(), maxLength);
    }

    private void FireCommand(string text, int? epoch, bool exact)
    {
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
        _lastPromptSignature = null;
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
            var match = CmdPromptRe.IsMatch(lineText) ? CmdSplitRe.Match(lineText) : null;
            if (match is not { Success: true })
                return null;
            var col = lineText.Length - match.Groups[2].Length;
            return (row, CmdText(row, col), CmdText(row, col, HistoryMaxCommand));
        }

        // Most input was echoed before Enter: capture it now, because a program such as
        // nano can switch buffers before the settle timer runs.
        if (ReadCommand() is { } visible)
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
        _probes.Remove(probe);
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
        var match = CmdSplitRe.Match(text);
        var command = match.Success ? match.Groups[2].Value : text;
        if (command.Length > 0)
            CommandMarked?.Invoke(command);
    }

    /// <summary>Marks whose lines were trimmed out of the scrollback: keep the command for
    /// history, say its output is gone.</summary>
    private void PruneDisposed()
    {
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
            var m = CmdSplitRe.Match(text);
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
        if (!_historyEnabled)
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
        var split = CmdSplitRe.Match(text);
        return (split.Success && split.Groups[2].Length == 0)
            || WindowsIdlePromptRe.IsMatch(text) || UnixSpacedIdlePromptRe.IsMatch(text)
            || UnixBracketedIdlePromptRe.IsMatch(text) || CiscoIosPromptRe.IsMatch(text)
            || CiscoXrPromptRe.IsMatch(text) || JunosPromptRe.IsMatch(text) || NokiaMdCliPromptRe.IsMatch(text);
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
        var split = CmdSplitRe.Match(text);
        if (!split.Success)
            return null;
        var prompt = split.Groups[1].Value;
        foreach (var re in new[] { WindowsIdlePromptRe, UnixSpacedIdlePromptRe, UnixBracketedIdlePromptRe, HistoryCompactPromptRe })
        {
            var m = re.Match(prompt);
            if (m.Success)
                return Truncate(m.Groups[1].Value.Trim(), 1024);
        }
        return null;
    }

    // ---- prompt context -------------------------------------------------------------------

    /// <summary>Native hint from a saved icon or SSH banner; it only permits prompt
    /// interpretation, screen evidence still owns the displayed context.</summary>
    public void SetPromptPlatform(string? platform)
    {
        if (platform is "cisco" or "juniper" or "nokia")
        {
            _promptPlatform = platform;
            ReportPromptContext(force: false);
        }
    }

    /// <summary>Reports the current location label from a completed known prompt. The cursor
    /// line is part of the signature, so returning to the same context after a command still
    /// reports that the command ended. Returns the label (or null).</summary>
    public (string Context, string? Platform)? ReportPromptContext(bool force)
    {
        if (_executing || _buffer.IsAlternate)
            return null;
        var row = _buffer.CursorLine;
        var start = row;
        while (start > 0 && _buffer.IsWrapped(start))
            start--;
        var promptText = _buffer.LineText(start);
        if (promptText is null)
            return null;
        for (var next = start + 1; next <= row && promptText.Length < 512; next++)
        {
            var text = _buffer.LineText(next);
            if (text is null || !_buffer.IsWrapped(next))
                break;
            promptText += text;
        }
        string? label = null, platform = null;
        Match match;
        if ((match = WindowsIdlePromptRe.Match(promptText)).Success)
        {
            label = match.Groups[1].Value;
        }
        else if ((match = UnixBracketedIdlePromptRe.Match(promptText)).Success)
        {
            label = match.Groups[1].Value.Trim();
        }
        else if ((match = UnixSpacedIdlePromptRe.Match(promptText)).Success)
        {
            label = match.Groups[1].Value.Trim();
        }
        else if (NokiaMdCliPromptRe.IsMatch(promptText) && start > 0)
        {
            var contextText = !_buffer.IsWrapped(start - 1) ? _buffer.LineText(start - 1) ?? "" : "";
            var context = NokiaMdCliContextRe.Match(contextText);
            if (context.Success)
            {
                var modeKey = context.Groups[4].Success ? context.Groups[4].Value : context.Groups[2].Value;
                var path = context.Groups[3].Value;
                if (context.Groups[4].Success)
                    path = path[(context.Groups[4].Value.Length + 1)..];
                label = path.Length > 0 ? path : "MD-CLI";
                if (modeKey.Length > 0)
                    label = NokiaMdCliModes[modeKey] + (context.Groups[1].Success ? "*" : "") + " · " + label;
                platform = "nokia";
                _promptPlatform = platform;
            }
        }
        else
        {
            var junos = JunosPromptRe.Match(promptText);
            if (junos.Success)
            {
                var previousText = start > 0 && !_buffer.IsWrapped(start - 1) ? _buffer.LineText(start - 1) ?? "" : "";
                var edit = JunosEditContextRe.Match(previousText);
                if (junos.Groups[1].Value == "#" && edit.Success)
                {
                    var hierarchy = edit.Groups[2].Success && edit.Groups[2].Length > 0 ? "/" + edit.Groups[2].Value : "/";
                    label = (edit.Groups[1].Success && edit.Groups[1].Length > 0 ? edit.Groups[1].Value + " · " : "")
                        + "configure · " + hierarchy;
                    platform = "juniper";
                    _promptPlatform = platform;
                }
                else if (junos.Groups[1].Value == ">")
                {
                    var role = JunosRoleRe.Match(previousText);
                    var known = _promptPlatform == "juniper" || role.Success || RecentLineMatches(start, JunosBannerRe, 40);
                    if (known)
                    {
                        label = (role.Success ? role.Groups[1].Value + " · " : "") + "operational";
                        platform = "juniper";
                        _promptPlatform = platform;
                    }
                }
            }
            if (label is null)
            {
                var xr = CiscoXrPromptRe.Match(promptText);
                var ios = xr.Success ? Match.Empty : CiscoIosPromptRe.Match(promptText);
                if (xr.Success || ios.Success)
                {
                    var isXr = xr.Success;
                    var location = isXr ? xr.Groups[1].Value : "";
                    var mode = isXr ? xr.Groups[3].Value : ios.Groups[2].Value;
                    var terminator = isXr ? xr.Groups[4].Value : ios.Groups[3].Value;
                    var known = isXr || _promptPlatform == "cisco" || RecentLineMatches(start, CiscoBannerRe, 60);
                    // A mode suffix is safe network-CLI context; a bare EXEC prompt needs Cisco
                    // evidence so "server#" stays an ordinary Unix root prompt.
                    if (mode.Length > 0 || known)
                    {
                        var parts = CiscoModeParts(mode, terminator, isXr);
                        if (location.Length > 0)
                            parts.Insert(0, location);
                        label = string.Join(" · ", parts);
                        if (known)
                        {
                            platform = "cisco";
                            _promptPlatform = platform;
                        }
                    }
                }
            }
        }
        if (label is null)
            return null;
        var signature = row + ":" + platform + ":" + label;
        if (!force && signature == _lastPromptSignature)
            return (label, platform);
        _lastPromptSignature = signature;
        PromptContext?.Invoke(label, platform);
        return (label, platform);
    }

    private bool RecentLineMatches(int start, Regex pattern, int limit)
    {
        for (var scan = start - 1; scan >= Math.Max(0, start - limit); scan--)
        {
            if (_buffer.LineText(scan) is { } text && pattern.IsMatch(text))
                return true;
        }
        return false;
    }

    private static List<string> CiscoModeParts(string mode, string terminator, bool isXr)
    {
        if (mode.Length == 0)
            return [terminator == ">" ? "user EXEC" : isXr ? "EXEC" : "privileged EXEC"];
        if (mode == "admin")
            return ["administration"];
        if (mode == "admin-config")
            return ["administration", "configure"];
        if (mode.StartsWith("admin-config-", StringComparison.Ordinal))
        {
            var sub = mode[13..];
            return ["administration", "configure", CiscoSubmodeNames.GetValueOrDefault(sub) ?? sub.Replace('-', ' ')];
        }
        if (mode == "config")
            return ["configure"];
        if (mode.StartsWith("config-", StringComparison.Ordinal))
        {
            var sub = mode[7..];
            return ["configure", CiscoSubmodeNames.GetValueOrDefault(sub) ?? sub.Replace('-', ' ')];
        }
        return [mode.Replace('-', ' ')];
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
