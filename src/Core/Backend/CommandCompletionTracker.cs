namespace Resesh.Core.Backend;

public sealed record CommandCompletion(long ExecutionId, string ProgramName, TimeSpan Duration, int? ExitCode);

/// <summary>One explicit notification request, scoped to one exact shell execution.</summary>
public sealed class CommandCompletionTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _executionId;
    private long _started;
    public string? ProgramName { get; private set; }
    public bool IsRunning => _executionId is not null;
    public bool IsArmed { get; private set; }

    public void Start(long id, string commandLine)
    {
        if (_executionId == id) return; // duplicate start must not cancel the user's request
        Reset();
        if (string.IsNullOrWhiteSpace(commandLine)) return;
        ProgramName = CommandTitle.ProgramName(commandLine) ?? "Command";
        _executionId = id;
        _started = _time.GetTimestamp();
    }

    public void Arm() => IsArmed = IsRunning;
    public void Disarm() => IsArmed = false;
    public void Toggle() { if (IsArmed) Disarm(); else Arm(); }

    public CommandCompletion? Complete(long id, int? status)
    {
        if (_executionId != id) return null;
        var completion = IsArmed
            ? new CommandCompletion(id, ProgramName!, _time.GetElapsedTime(_started), status)
            : null;
        Reset();
        return completion;
    }

    public void Reset()
    {
        _executionId = null;
        ProgramName = null;
        IsArmed = false;
    }
}
