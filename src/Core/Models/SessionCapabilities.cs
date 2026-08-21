namespace Resesh.Core.Models;

/// <summary>
/// What a session's target kind supports, so the UI adapts through one surface instead
/// of growing scattered kind checks. Derived, never stored.
/// </summary>
public sealed record SessionCapabilities
{
    /// <summary>SFTP file pane, SSHFS "open in Explorer", remote cwd tracking.</summary>
    public bool RemoteFiles { get; init; }

    /// <summary>Host-key trust and connection-security summary apply.</summary>
    public bool HostKeys { get; init; }

    /// <summary>tmux persistence ("End Remote Session", cwd side-channel) can apply.</summary>
    public bool RemoteSession { get; init; }

    /// <summary>"Open Working Folder" (local starting directory in Explorer) applies.</summary>
    public bool LocalWorkingFolder { get; init; }

    /// <summary>Verb for ending the live connection/process: "Disconnect" or "Stop".</summary>
    public string StopVerb { get; init; } = "Disconnect";

    /// <summary>Verb for starting it again: "Reconnect" or "Restart".</summary>
    public string StartAgainVerb { get; init; } = "Reconnect";

    private static readonly SessionCapabilities Ssh = new()
    {
        RemoteFiles = true,
        HostKeys = true,
        RemoteSession = true,
        StopVerb = "Disconnect",
        StartAgainVerb = "Reconnect",
    };

    private static readonly SessionCapabilities Local = new()
    {
        LocalWorkingFolder = true,
        StopVerb = "Stop",
        StartAgainVerb = "Restart",
    };

    public static SessionCapabilities For(Session session) =>
        session.Kind == SessionKind.Local ? Local : Ssh;
}
