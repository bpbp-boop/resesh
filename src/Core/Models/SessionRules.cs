namespace Resesh.Core.Models;

/// <summary>Rules a saved session must keep, shared by every editor that can break them.</summary>
public static class SessionRules
{
    /// <summary>PowerShell shell integration cannot run inside a persistent shell, so a session
    /// with it can't be made persistent (the single-session editor refuses; bulk edit skips it).</summary>
    public static bool CanBePersistent(ShellIntegrationMode shellIntegration) =>
        shellIntegration != ShellIntegrationMode.PowerShell;

    public const string PersistentPowerShellConflict =
        "Turn off Persistent session to use PowerShell shell integration.";
}
