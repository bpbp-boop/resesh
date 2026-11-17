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

    /// <summary>A new connection starts: what was half-read from the old one is dropped,
    /// including a commit it echoed but never answered, which proves nothing. The timer
    /// stays, because it runs on the device.</summary>
    public void ResetConnection()
    {
        _utf8.Reset();
        ClearLine();
        _escape = EscapeState.None;
        _afterCarriageReturn = false;
        _pending = null;
    }

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
            // "0 minutes" is the last minute, taken as no time left: a countdown that ends early
            // is safe, one that ends late is not. A number no device would print is not a reminder.
            if (Timeout(armed.Groups["minutes"].Value, TimeSpan.FromMinutes(1), minimum: 0) is { } left)
            {
                var deadline = _time.GetUtcNow() + left;
                if (Timer is not { Status: CommitTimerStatus.Armed, Deadline: { } current } || (deadline - current).Duration() >= TimeSpan.FromMinutes(1))
                    ArmAt(deadline);
            }
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
        var xr = XrPrompt().IsMatch(prompt);
        var nokia = NokiaPrompt().IsMatch(prompt);
        if (ParseCommit(command, xr, nokia) is not { } commit)
            return;
        var now = _time.GetUtcNow();
        _pending = commit switch
        {
            // MD-CLI ends its confirmed commit with "accept" or "cancel".
            { Confirmed: true, Action: "accept" } => new PendingCommit(PendingKind.Confirm, TimeSpan.Zero, now),
            { Confirmed: true, Action: "cancel" } => new PendingCommit(PendingKind.Cancel, TimeSpan.Zero, now),
            { Confirmed: true } => new PendingCommit(PendingKind.Arm, commit.Timeout ?? (xr ? XrDefault : JunosDefault), now),
            // Any other commit (commit, commit check, commit comment …, commit and-quit)
            // confirms the pending one on Junos and IOS XR. MD-CLI confirms only with
            // "commit confirmed accept", so a plain commit there leaves the timer running.
            _ when Timer.Status == CommitTimerStatus.Armed && !nokia => new PendingCommit(PendingKind.Confirm, TimeSpan.Zero, now),
            _ => _pending,
        };
    }

    // Options that take a value, which is never read as a keyword or a timeout: a label of
    // "confirmed" or a persist-id of 123 is not a timer.
    private static readonly HashSet<string> ValueOptions = ["comment", "label", "description", "persist-id", "at"];

    /// <summary>A commit command's options, in any order ("commit best-effort confirmed 60",
    /// "commit confirmed 5 comment \"x\"", "commit confirmed persist-id 123"); null when the
    /// command is not a commit. The timeout is minutes on Junos and MD-CLI, seconds on IOS XR
    /// unless "minutes" says otherwise, and null when none is given. MD-CLI ignores everything
    /// after an unquoted #, so a comment there is no part of the command.</summary>
    private static ParsedCommit? ParseCommit(string command, bool xr, bool nokia)
    {
        var words = Words().Matches(command).Select(match => match.Value.ToLowerInvariant())
            .TakeWhile(word => !(nokia && word.StartsWith('#')))
            .ToList();
        if (words is not ["commit", ..])
            return null;
        var confirmed = false;
        string? action = null;
        TimeSpan? timeout = null;
        for (var index = 1; index < words.Count; index++)
        {
            var word = words[index];
            if (ValueOptions.Contains(word))
            {
                index++; // skip the value
                continue;
            }
            if (word != "confirmed")
                continue;
            confirmed = true;
            var next = index + 1 < words.Count ? words[index + 1] : null;
            if (next is "accept" or "cancel")
            {
                action = next;
                index++;
            }
            else if (next == "minutes" && index + 2 < words.Count)
            {
                timeout = Timeout(words[index + 2], TimeSpan.FromMinutes(1));
                index += 2;
            }
            // MD-CLI's optional keyword: "commit confirmed timeout 5" is "commit confirmed 5".
            else if (next == "timeout" && index + 2 < words.Count)
            {
                timeout = Timeout(words[index + 2], xr ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1));
                index += 2;
            }
            else if (next is not null && Timeout(next, xr ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1)) is { } value)
            {
                timeout = value;
                index++;
            }
        }
        return new ParsedCommit(confirmed, action, timeout);
    }

    /// <summary><paramref name="text"/> units, or null when it is not a number a device takes
    /// (1 to 65535 on all three platforms; a Junos reminder can say 0), so nothing out of
    /// range reaches a TimeSpan.</summary>
    private static TimeSpan? Timeout(string text, TimeSpan unit, int minimum = 1) =>
        int.TryParse(text, out var value) && value >= minimum && value <= 65535 ? unit * value : null;

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

    // A refused commit. Junos "error: commit failed", "syntax error, expecting <command>",
    // "unknown command."; IOS XR "% Failed to commit", "% Invalid input detected at '^' marker.",
    // "% Incomplete command."; MD-CLI "MINOR: MGMT_CORE #...: ...".
    [GeneratedRegex(@"commit failed|failed to commit|no configuration changes|syntax error|unknown command|^error:|^%\s*(?:invalid input|incomplete command|ambiguous command)|^MINOR:|^MAJOR:|^CRITICAL:", RegexOptions.IgnoreCase)]
    private static partial Regex CommitFailed();

    // A command's words, a quoted value counting as one.
    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+")]
    private static partial Regex Words();

    private enum EscapeState { None, Escape, Csi, String, StringEscape }

    private enum PendingKind { Arm, Confirm, Cancel }

    private sealed record PendingCommit(PendingKind Kind, TimeSpan Duration, DateTimeOffset At);

    private sealed record ParsedCommit(bool Confirmed, string? Action, TimeSpan? Timeout);
}
