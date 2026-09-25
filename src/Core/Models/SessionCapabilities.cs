namespace Resesh.Core.Models;

/// <summary>
/// What a session's target kind supports, so the UI adapts through one surface instead
/// of growing scattered kind checks. Derived, never stored.
/// </summary>
public sealed record SessionCapabilities
{
    /// <summary>A browsable per-tab file pane applies.</summary>
    public bool FilePane { get; init; }

    /// <summary>SFTP, SSHFS, and remote cwd queries apply.</summary>
    public bool RemoteFiles { get; init; }

    /// <summary>Host-key trust and connection-security summary apply.</summary>
    public bool HostKeys { get; init; }

    /// <summary>tmux persistence ("End Remote Session", cwd side-channel) can apply.</summary>
    public bool RemoteSession { get; init; }

    /// <summary>"Open Working Folder" (local starting directory in Explorer) applies.</summary>
    public bool LocalWorkingFolder { get; init; }

    /// <summary>"Send Break" applies: the transport can carry a break signal (telnet BREAK,
    /// which console servers turn into a serial break on the device's console port).</summary>
    public bool SendBreak { get; init; }

    /// <summary>Verb for ending the live connection/process: "Disconnect" or "Stop".</summary>
    public string StopVerb { get; init; } = "Disconnect";

    /// <summary>Verb for starting it again: "Reconnect" or "Restart".</summary>
    public string StartAgainVerb { get; init; } = "Reconnect";

    private static readonly SessionCapabilities Ssh = new()
    {
        FilePane = true,
        RemoteFiles = true,
        HostKeys = true,
        RemoteSession = true,
        StopVerb = "Disconnect",
        StartAgainVerb = "Reconnect",
    };

    private static readonly SessionCapabilities Local = new()
    {
        FilePane = true,
        LocalWorkingFolder = true,
        StopVerb = "Stop",
        StartAgainVerb = "Restart",
    };

    // Telnet is a bare byte stream: no side channel for files, no host identity, no tmux.
    private static readonly SessionCapabilities Telnet = new()
    {
        SendBreak = true,
        StopVerb = "Disconnect",
        StartAgainVerb = "Reconnect",
    };

    public static SessionCapabilities For(Session session) => session.Kind switch
    {
        SessionKind.Local => Local,
        SessionKind.Telnet => Telnet,
        _ => Ssh,
    };
}
