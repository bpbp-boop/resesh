namespace Resesh.Core.Ssh;

/// <summary>
/// What keeps a persistent shell alive on the host: tmux, or the Resesh session agent.
/// A tab picks one when it connects and uses it for everything it then asks the host about
/// its sessions. Session names, slots and <see cref="TmuxPersistence.PlanConnect"/> are
/// the same for both.
/// </summary>
public abstract class PersistenceBackend
{
    public static PersistenceBackend Tmux { get; } = new TmuxBackend();

    /// <param name="agentPath">The agent on the host, quoted for a POSIX shell
    /// (<see cref="AgentInstall.RemotePath"/>).</param>
    public static PersistenceBackend Agent(string agentPath) => new AgentBackend(agentPath);

    /// <summary>tmux keeps a program's status report to itself, so it travels behind the
    /// window title (<see cref="Agents.TmuxStatus"/>). The agent forwards it as it is.</summary>
    public abstract bool StatusRidesOnTitle { get; }

    /// <summary>Lists the host's sessions with client counts, start times, foreground
    /// commands and directories.</summary>
    public abstract string ManagementCommand();

    public abstract IReadOnlyList<TmuxSessionInfo> ParseManagedSessions(string output, Guid id);

    /// <summary>A failed listing that only means nothing is running.</summary>
    public abstract bool IsAbsent(SshCommandResult result);

    public abstract string BootstrapCommand(Guid id, int slot, string? newShellCommand, string? fallbackCommand);

    public abstract string ResumeCommand(Guid id, int slot);

    public abstract string KillCommand(Guid id, int slot);

    public abstract string KillCommand(Guid id, IEnumerable<int> slots);

    public abstract string CurrentPathCommand();

    public abstract string? ParseCurrentPath(string output, Guid id, int slot);

    private sealed class TmuxBackend : PersistenceBackend
    {
        public override bool StatusRidesOnTitle => true;
        public override string ManagementCommand() => TmuxPersistence.ManagementCommand();
        public override IReadOnlyList<TmuxSessionInfo> ParseManagedSessions(string output, Guid id) =>
            TmuxPersistence.ParseManagedSessions(output, id);
        public override bool IsAbsent(SshCommandResult result) => TmuxPersistence.IsServerAbsent(result);
        public override string BootstrapCommand(Guid id, int slot, string? newShellCommand, string? fallbackCommand) =>
            TmuxPersistence.BootstrapCommand(id, slot, newShellCommand, fallbackCommand);
        public override string ResumeCommand(Guid id, int slot) => TmuxPersistence.ResumeCommand(id, slot);
        public override string KillCommand(Guid id, int slot) => TmuxPersistence.KillCommand(id, slot);
        public override string KillCommand(Guid id, IEnumerable<int> slots) => TmuxPersistence.KillCommand(id, slots);
        public override string CurrentPathCommand() => TmuxPersistence.CurrentPathCommand();
        public override string? ParseCurrentPath(string output, Guid id, int slot) =>
            TmuxPersistence.ParseCurrentPath(output, id, slot);
    }

    private sealed class AgentBackend(string agentPath) : PersistenceBackend
    {
        public override bool StatusRidesOnTitle => false;
        public override string ManagementCommand() => AgentPersistence.ListCommand(agentPath);
        public override IReadOnlyList<TmuxSessionInfo> ParseManagedSessions(string output, Guid id) =>
            AgentPersistence.ParseSessions(output, id);
        // A host with no sessions lists nothing and succeeds.
        public override bool IsAbsent(SshCommandResult result) => false;
        public override string BootstrapCommand(Guid id, int slot, string? newShellCommand, string? fallbackCommand) =>
            AgentPersistence.BootstrapCommand(agentPath, id, slot, newShellCommand, fallbackCommand);
        public override string ResumeCommand(Guid id, int slot) => AgentPersistence.ResumeCommand(agentPath, id, slot);
        public override string KillCommand(Guid id, int slot) => AgentPersistence.KillCommand(agentPath, id, slot);
        public override string KillCommand(Guid id, IEnumerable<int> slots) => AgentPersistence.KillCommand(agentPath, id, slots);
        public override string CurrentPathCommand() => AgentPersistence.ListCommand(agentPath);
        public override string? ParseCurrentPath(string output, Guid id, int slot) =>
            AgentPersistence.ParseSessions(output, id).FirstOrDefault(session => session.Slot == slot)
                is { CurrentPath: ['/', ..] path } ? path : null;
    }
}
