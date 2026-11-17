using System.Text;
using System.Text.RegularExpressions;

namespace Resesh.Core.NetworkDevices;

/// <summary>Where a session's confirmed commit stands.</summary>
public enum CommitTimerStatus
{
    /// <summary>No confirmed commit is waiting.</summary>
    None,

    /// <summary>A confirmed commit rolls back at <see cref="CommitTimer.Deadline"/> unless confirmed.</summary>
    Armed,

    /// <summary>The device said it rolled back, or a rollback was asked for.</summary>
    RolledBack,
}

/// <summary>A session's confirmed-commit timer. <see cref="Deadline"/> is when it rolls back
/// while armed, and when it rolled back once it has; null when nothing is waiting.</summary>
public sealed record CommitTimer(CommitTimerStatus Status, DateTimeOffset? Deadline = null)
{
    public static readonly CommitTimer None = new(CommitTimerStatus.None);
}

/// <summary>
/// Follows a terminal's output for confirmed commits on network devices, so the tab can count
/// down to the rollback. Junos, IOS XR and Nokia SR OS MD-CLI are recognised.
/// The typed command is read from the device's echo of it after its prompt, which
/// works the same on every platform and needs nothing typed through the app. A commit takes
/// effect only once the device prompts again with no error in between, so a commit the
/// device refused neither arms nor confirms a timer. Junos also says how long is left, and
/// that it rolled back; those messages correct the timer whenever they appear.
/// Fed from the backend's reader thread only; not thread-safe.
/// </summary>
public sealed partial class CommitConfirmedWatcher(TimeProvider? timeProvider = null)
{
    // Long enough for any prompt and command; anything longer is not one and is dropped.
    private const int MaxLine = 512;

    private static readonly TimeSpan JunosDefault = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NokiaDefault = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan XrDefault = TimeSpan.FromSeconds(600);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly StringBuilder _line = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private char[] _chars = new char[1024];
    private EscapeState _escape;
    private bool _afterCarriageReturn;
    private bool _overflowed;

    // A commit command seen but not yet answered by a prompt.
    private PendingCommit? _pending;

    public CommitTimer Timer { get; private set; } = CommitTimer.None;

    /// <summary>Reads more output. True when <see cref="Timer"/> changed.</summary>
    public bool Feed(ReadOnlySpan<byte> data)
    {
        var before = Timer;
        if (_chars.Length < data.Length + 1)
            _chars = new char[data.Length + 1];
        var count = _utf8.GetChars(data, _chars, flush: false);
        foreach (var ch in _chars.AsSpan(0, count))
            Read(ch);
        // A prompt waits for typing without a line ending, so the unfinished line is checked too.
        if (_pending is not null && !_overflowed && IsPrompt(_line.ToString()))
            Settle();
        return Timer != before;
    }

    private void Read(char ch)
    {
        switch (_escape)
        {
            case EscapeState.Escape:
                _escape = ch switch { '[' => EscapeState.Csi, ']' or 'P' or '_' or '^' => EscapeState.String, _ => EscapeState.None };
                return;
            case EscapeState.Csi:
                if (ch is >= '@' and <= '~')
                    _escape = EscapeState.None;
                return;
            case EscapeState.String:
                if (ch == '\a')
                    _escape = EscapeState.None;
                else if (ch == '\x1b')
                    _escape = EscapeState.StringEscape;
                return;
            case EscapeState.StringEscape:
                _escape = ch == '\x1b' ? EscapeState.StringEscape : EscapeState.String;
                if (ch == '\\')
                    _escape = EscapeState.None;
                return;
        }

        if (ch == '\x1b')
        {
            _escape = EscapeState.Escape;
            return;
        }
        if (ch == '\n')
        {
            _afterCarriageReturn = false;
            EndLine();
            return;
        }
        if (_afterCarriageReturn)
        {
            // A bare carriage return redraws the line from its start.
            _afterCarriageReturn = false;
            ClearLine();
        }
        if (ch == '\r')
        {
            _afterCarriageReturn = true;
            return;
        }
        if (ch == '\b')
        {
            if (_line.Length > 0)
                _line.Length--;
            return;
        }
        if (char.IsControl(ch))
            return;
        if (_line.Length >= MaxLine)
        {
            _overflowed = true;
            return;
        }
        _line.Append(ch);
    }

    private void ClearLine()
    {
        _line.Clear();
        _overflowed = false;
    }

    private void EndLine()
    {
        var line = _overflowed ? "" : _line.ToString().Trim();
        ClearLine();
        if (line.Length > 0)
            OnLine(line);
    }

    private void OnLine(string line)
    {
        if (CommandLine().Match(line) is { Success: true } command)
        {
            // A prompt that ends a pending commit's output, and starts the next command.
            if (_pending is not null)
                Settle();
            OnCommand(command.Groups["prompt"].Value, command.Groups["command"].Value.Trim());
            return;
        }
        // Any other prompt, with or without a command after it: "commit and-quit" ends at the
        // operational prompt (user@host>), which settles the commit but starts none.
        if (IsPrompt(line) || OperationalCommandLine().IsMatch(line))
        {
            if (_pending is not null)
                Settle();
            return;
        }

        // Junos reports every confirmed commit, and reminds at each prompt while one is pending.
        if (JunosArmed().Match(line) is { Success: true } armed)
        {
            // Junos rounds to whole minutes; a reminder within a minute of the timer keeps it steady.
            var deadline = _time.GetUtcNow() + TimeSpan.FromMinutes(int.Parse(armed.Groups["minutes"].Value));
            if (Timer.Deadline is not { } current || (deadline - current).Duration() >= TimeSpan.FromMinutes(1))
                ArmAt(deadline);
            if (_pending is { Kind: PendingKind.Arm })
                _pending = null;
            return;
        }
        if (JunosRolledBack().IsMatch(line))
        {
            Timer = new CommitTimer(CommitTimerStatus.RolledBack, _time.GetUtcNow());
            return;
        }
        if (_pending is not null && CommitFailed().IsMatch(line))
            _pending = null;
    }

    private void OnCommand(string prompt, string command)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words[0].Equals("commit", StringComparison.OrdinalIgnoreCase))
            return;
        var xr = XrPrompt().IsMatch(prompt);
        var nokia = NokiaPrompt().IsMatch(prompt);
        var confirmed = words.Length > 1 && words[1].Equals("confirmed", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
        {
            // Any other commit (commit, commit check, commit comment …, commit and-quit)
            // confirms the pending one on Junos and IOS XR. MD-CLI confirms only with
            // "commit confirmed accept", so a plain commit there leaves the timer running.
            if (Timer.Status == CommitTimerStatus.Armed && !nokia)
                _pending = new PendingCommit(PendingKind.Confirm, TimeSpan.Zero, _time.GetUtcNow());
            return;
        }

        var rest = words.Skip(2).Select(word => word.ToLowerInvariant()).ToList();
        // MD-CLI ends its confirmed commit with "accept" or "cancel".
        if (rest.FirstOrDefault() is "accept")
        {
            _pending = new PendingCommit(PendingKind.Confirm, TimeSpan.Zero, _time.GetUtcNow());
            return;
        }
        if (rest.FirstOrDefault() is "cancel")
        {
            _pending = new PendingCommit(PendingKind.Cancel, TimeSpan.Zero, _time.GetUtcNow());
            return;
        }
        _pending = new PendingCommit(PendingKind.Arm, Duration(rest, xr), _time.GetUtcNow());
    }

    /// <summary>How long a "commit confirmed" waits: minutes on Junos and MD-CLI, seconds on
    /// IOS XR unless "minutes" says otherwise. Each platform's default when no time is given.</summary>
    private static TimeSpan Duration(List<string> words, bool xr)
    {
        for (var index = 0; index < words.Count; index++)
        {
            if (words[index] == "minutes" && index + 1 < words.Count && int.TryParse(words[index + 1], out var minutes))
                return TimeSpan.FromMinutes(minutes);
            if (int.TryParse(words[index], out var value))
                return xr ? TimeSpan.FromSeconds(value) : TimeSpan.FromMinutes(value);
        }
        return xr ? XrDefault : JunosDefault;
    }

    /// <summary>The device prompted again with no error since the commit: it took effect.</summary>
    private void Settle()
    {
        var pending = _pending!;
        _pending = null;
        switch (pending.Kind)
        {
            case PendingKind.Arm:
                ArmAt(pending.At + pending.Duration);
                break;
            case PendingKind.Confirm:
                Timer = CommitTimer.None;
                break;
            case PendingKind.Cancel:
                Timer = new CommitTimer(CommitTimerStatus.RolledBack, _time.GetUtcNow());
                break;
        }
    }

    private void ArmAt(DateTimeOffset deadline) => Timer = new CommitTimer(CommitTimerStatus.Armed, deadline);

    private static bool IsPrompt(string line) => Prompt().IsMatch(line.Trim());

    /// <summary>A prompt: user@host# or user@host> (Junos), A:user@host# with *, ! or (ex)
    /// marks (MD-CLI), RP/0/RP0/CPU0:host(config)# (IOS XR). Any prompt settles a commit;
    /// only a # prompt's command is read as one (<see cref="CommandLine"/>).</summary>
    [GeneratedRegex(@"^[!*]*(?:\(\w+\))?[^\s#>]+[#>]$")]
    private static partial Regex Prompt();

    [GeneratedRegex(@"^[!*]*(?:\(\w+\))?[^\s#>]+>\s*\S")]
    private static partial Regex OperationalCommandLine();

    [GeneratedRegex(@"^(?<prompt>[!*]*(?:\(\w+\))?[^\s#>]+#)\s*(?<command>\S.*)$")]
    private static partial Regex CommandLine();

    [GeneratedRegex(@"^(?:RP|LC)/\d+/")]
    private static partial Regex XrPrompt();

    [GeneratedRegex(@"^[!*]*(?:\(\w+\))?[AB]:[^\s#>]+@")]
    private static partial Regex NokiaPrompt();

    // "commit confirmed will be automatically rolled back in 5 minutes unless confirmed",
    // and at later prompts "# commit confirmed will be rolled back in 4 minutes".
    [GeneratedRegex(@"commit confirmed will be (?:automatically )?rolled back in (?<minutes>\d+) minutes?", RegexOptions.IgnoreCase)]
    private static partial Regex JunosArmed();

    [GeneratedRegex(@"Commit was not confirmed; automatic rollback complete", RegexOptions.IgnoreCase)]
    private static partial Regex JunosRolledBack();

    // Junos "error: commit failed", IOS XR "% Failed to commit", MD-CLI "MINOR: MGMT_CORE #...: ... commit failed".
    [GeneratedRegex(@"commit failed|failed to commit|no configuration changes|^error:|^MINOR:|^MAJOR:|^CRITICAL:", RegexOptions.IgnoreCase)]
    private static partial Regex CommitFailed();

    private enum EscapeState { None, Escape, Csi, String, StringEscape }

    private enum PendingKind { Arm, Confirm, Cancel }

    private sealed record PendingCommit(PendingKind Kind, TimeSpan Duration, DateTimeOffset At);
}
