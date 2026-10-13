using Resesh.Core.Models;

namespace Resesh.Core.Agents;

/// <summary>What a program says it is doing in an OSC 7501 report. Values match
/// libghostty-vt's <c>GhosttyProgramStatusState</c>.</summary>
public enum ProgramStatusState
{
    Idle = 0,
    Working = 1,
    Done = 2,
    Blocked = 3,
    Error = 4,

    /// <summary>Not a state: remove the record (and, with no id, every record).</summary>
    Clear = 5,
}

/// <summary>What a blocked program needs. Values match <c>GhosttyProgramStatusKind</c>.</summary>
public enum ProgramStatusKind
{
    None = 0,
    Permission = 1,
    Question = 2,
    Auth = 3,
}

/// <summary>
/// One program status report (OSC 7501): a program telling the terminal it is idle,
/// working, done, blocked or failed. It travels in the terminal output, so it works over
/// SSH with nothing installed on the server. libghostty-vt validates and decodes the
/// report; the shim hands it on as <c>state;kind;progress</c> followed by id, app, title
/// and message, each behind a unit separator.
/// </summary>
public sealed record ProgramStatus(
    ProgramStatusState State,
    ProgramStatusKind Kind,
    int Progress,
    string Id,
    string App,
    string? Title,
    string? Message)
{
    public const int Code = 7501;

    private const char FieldSeparator = '\u001F';

    /// <summary>The attention this report asks for. <see cref="ProgramStatusState.Clear"/>
    /// has none of its own; the tracker handles it before asking.</summary>
    public AgentAttention Attention => State switch
    {
        ProgramStatusState.Working => AgentAttention.Working,
        ProgramStatusState.Blocked when Kind == ProgramStatusKind.Permission => AgentAttention.NeedsApproval,
        ProgramStatusState.Blocked => AgentAttention.NeedsAnswer,
        ProgramStatusState.Done => AgentAttention.Complete,
        ProgramStatusState.Error => AgentAttention.Failed,
        _ => AgentAttention.Idle,
    };

    /// <summary>Tooltip text: the program's message, else its title, else a hint that a
    /// blocked program is waiting for a sign-in.</summary>
    public string? Label =>
        Message ?? Title ?? (State == ProgramStatusState.Blocked && Kind == ProgramStatusKind.Auth ? "needs sign-in" : null);

    /// <summary>
    /// What this report means for the tab's progress bar, or null when it says nothing
    /// about it (a child record). A percentage fills the bar while the program works and
    /// holds it, paused, while the program waits for the user. Work with no percentage
    /// shows no bar: "working" alone is the badge's job, and most agents would otherwise
    /// keep every tab's bar spinning.
    /// </summary>
    public TerminalProgress? AsTerminalProgress()
    {
        if (Id.Length > 0)
            return null;
        if (Progress < 0)
            return TerminalProgress.None;
        return State switch
        {
            ProgramStatusState.Working => TerminalProgress.Percent(Progress),
            ProgramStatusState.Blocked => new TerminalProgress(TerminalProgressState.Paused, Progress),
            _ => TerminalProgress.None,
        };
    }

    /// <summary>Parses the shim's payload. Returns null for anything malformed or for a
    /// state this version does not know.</summary>
    public static ProgramStatus? Parse(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        var fields = payload.Split(FieldSeparator);
        var numbers = fields[0].Split(';');
        if (numbers.Length != 3
            || !int.TryParse(numbers[0], out var state) || !Enum.IsDefined((ProgramStatusState)state)
            || !int.TryParse(numbers[1], out var kind)
            || !int.TryParse(numbers[2], out var progress))
        {
            return null;
        }

        string Field(int index) => index < fields.Length ? fields[index] : "";
        return new ProgramStatus(
            (ProgramStatusState)state,
            Enum.IsDefined((ProgramStatusKind)kind) ? (ProgramStatusKind)kind : ProgramStatusKind.None,
            progress is >= 0 and <= 100 ? progress : -1,
            Field(1).Trim(),
            Field(2).Trim().ToLowerInvariant(),
            AgentText.Sanitize(Field(3), AgentOsc.MaxLabelLength),
            AgentText.Sanitize(Field(4), AgentOsc.MaxLabelLength));
    }
}
