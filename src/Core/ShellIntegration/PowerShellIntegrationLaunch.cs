namespace Resesh.Core.ShellIntegration;

/// <summary>Loads the real script after profiles without changing execution policy.
/// A blocked integration must leave an ordinary, usable interactive shell.</summary>
internal static class PowerShellIntegrationLaunch
{
    internal static string Command(string scriptPath)
    {
        var quotedPath = "'" + scriptPath.Replace("'", "''") + "'";
        return "try { . " + quotedPath + " } " +
            "catch [System.Management.Automation.PSSecurityException] { " +
            "Write-Host 'Resesh: PowerShell security settings blocked shell integration. You can still use this shell.' } " +
            "catch { Write-Host 'Resesh: Shell integration could not load. You can still use this shell.' }";
    }
}
