using System.Security.Cryptography;
using System.Text;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Local;

/// <summary>
/// Finds the local shells installed on this machine and syncs them into the profile
/// store as built-in local profiles with stable identities, so pinned tabs and future
/// workspaces can reference them across restarts. User edits to a built-in profile are
/// preserved (sync never overwrites an existing record); unavailable shells are hidden
/// by the tree/search layer via the availability set, not deleted.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class LocalShellDiscovery
{
    /// <summary>One discovered shell in its default configuration.</summary>
    public sealed record DiscoveredShell(Guid Id, string Name, LocalTarget Target, string? Icon);

    /// <summary>Deterministic profile id for a discovery key ("pwsh", "wsl:Ubuntu", …),
    /// so identity is stable across restarts and machines.</summary>
    public static Guid StableId(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("sessions-local:" + key.ToLowerInvariant()));
        return new Guid(hash);
    }

    /// <summary>Currently-installed shells, in default-profile priority order.</summary>
    public static IReadOnlyList<DiscoveredShell> Discover()
    {
        var shells = new List<DiscoveredShell>();

        var pwsh = FindPwsh();
        if (pwsh is not null)
        {
            shells.Add(new DiscoveredShell(StableId("pwsh"), "PowerShell",
                new LocalTarget { Executable = pwsh, Arguments = ["-NoLogo"] }, "windows"));
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            shells.Add(new DiscoveredShell(StableId("powershell"), "PowerShell 5.1",
                new LocalTarget { Executable = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", Arguments = ["-NoLogo"] },
                "windows"));
        }

        if (File.Exists(Path.Combine(systemRoot, "System32", "cmd.exe")))
        {
            shells.Add(new DiscoveredShell(StableId("cmd"), "Command Prompt",
                new LocalTarget { Executable = @"%SystemRoot%\System32\cmd.exe" }, "windows"));
        }

        foreach (var distribution in WslDistributions())
        {
            shells.Add(new DiscoveredShell(StableId("wsl:" + distribution), distribution,
                new LocalTarget
                {
                    Executable = @"%SystemRoot%\System32\wsl.exe",
                    Arguments = ["-d", distribution, "--cd", "~"],
                },
                WslIcon(distribution)));
        }

        var gitBash = FindGitBash();
        if (gitBash is not null)
        {
            shells.Add(new DiscoveredShell(StableId("git-bash"), "Git Bash",
                new LocalTarget { Executable = gitBash, Arguments = ["--login", "-i"] }, "linux"));
        }

        return shells;
    }

    /// <summary>
    /// Adds newly discovered shells to the store (existing records — including user-edited
    /// ones — are left untouched) and returns the ids of the built-in profiles whose shell
    /// is present right now. Built-ins outside this set are hidden, not deleted, so a
    /// reinstalled shell brings its profile (and its pins) back.
    /// </summary>
    public static IReadOnlySet<Guid> SyncBuiltIns(SessionStore store)
    {
        var discovered = Discover();
        var available = discovered.Select(s => s.Id).ToHashSet();
        foreach (var shell in discovered)
        {
            var existing = store.Find(shell.Id);
            if (existing is null)
            {
                store.Add(new Session
                {
                    Id = shell.Id,
                    Kind = SessionKind.Local,
                    BuiltIn = true,
                    Name = shell.Name,
                    Local = shell.Target,
                    Icon = shell.Icon,
                });
            }
            else if (shell.Id == StableId("powershell")
                && existing.BuiltIn
                && existing.Name == "Windows PowerShell")
            {
                // Migrate the old default label, but preserve user-edited names.
                store.Update(existing with { Name = shell.Name });
            }
        }
        return available;
    }

    /// <summary>The discovered default target for a built-in profile id, for "Reset to defaults".</summary>
    public static DiscoveredShell? FindDefaults(Guid id) =>
        Discover().FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// The profile to open for "+ Session" / Ctrl+Shift+T: the configured default when it
    /// still exists and is visible, else the first available built-in in priority order,
    /// else any local profile.
    /// </summary>
    public static Session? DefaultProfile(SessionStore store, Guid? configuredId, IReadOnlySet<Guid> available)
    {
        bool Visible(Session s) => !s.BuiltIn || available.Contains(s.Id);
        if (configuredId is { } id && store.Find(id) is { Kind: SessionKind.Local } configured && Visible(configured))
            return configured;
        var locals = store.Sessions.Where(s => s.Kind == SessionKind.Local && Visible(s)).ToList();
        return Discover().Select(d => locals.FirstOrDefault(s => s.Id == d.Id)).FirstOrDefault(s => s is not null)
            ?? locals.FirstOrDefault();
    }

    private static string? FindPwsh()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standard = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(standard))
            return standard;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip it.
            }
        }
        return null;
    }

    private static IReadOnlyList<string> WslDistributions()
    {
        // The Lxss registry key lists registered distributions without spawning wsl.exe
        // (which could flash a console and is slow when the utility VM is cold).
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null)
                return [];
            var names = new List<string>();
            foreach (var subKeyName in lxss.GetSubKeyNames())
            {
                using var subKey = lxss.OpenSubKey(subKeyName);
                if (subKey?.GetValue("DistributionName") is string name && name.Length > 0
                    && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? WslIcon(string distribution)
    {
        foreach (var (marker, icon) in new[]
        {
            ("ubuntu", "ubuntu"), ("debian", "debian"), ("kali", "debian"), ("alpine", "alpine"),
            ("fedora", "fedora"), ("suse", "suse"), ("arch", "arch"), ("centos", "centos"),
            ("rhel", "rhel"), ("oracle", "rhel"),
        })
        {
            if (distribution.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return icon;
        }
        return "linux";
    }

    private static string? FindGitBash()
    {
        try
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string installPath)
                {
                    var bash = Path.Combine(installPath, "bin", "bash.exe");
                    if (File.Exists(bash))
                        return bash;
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}
