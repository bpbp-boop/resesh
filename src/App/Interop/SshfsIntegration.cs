using System.Diagnostics;
using System.Runtime.InteropServices;
using Sessions.Core.Models;

namespace Sessions.App.Interop;

/// <summary>
/// A live sshfs.exe drive mount (key-auth sessions). The child process holds the mount;
/// killing it unmounts. Owned by the tab that created it and disposed with it.
/// </summary>
internal sealed class SshfsMount : IDisposable
{
    private readonly Process _process;

    /// <summary>Drive root, e.g. "S:" — maps to the remote "/".</summary>
    public string Root { get; }

    internal SshfsMount(Process process, string root)
    {
        _process = process;
        Root = root;
    }

    public bool IsAlive
    {
        get
        {
            try
            {
                return !_process.HasExited && Directory.Exists(Root + @"\");
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(2000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }
}

/// <summary>
/// Optional SSHFS-Win/WinFsp integration: when the driver is installed, "Open in Explorer"
/// launches a real Explorer window on the remote filesystem via the \\sshfs.r\ UNC
/// provider. Detection only — nothing is bundled or reimplemented (per the roadmap).
///
/// The connection is established with WNetAddConnection2 BEFORE Explorer is launched:
/// explorer.exe given an unmounted sshfs UNC as an argument cannot prompt for credentials,
/// silently fails to resolve it, and opens Documents instead (observed live). Mounting
/// first also keeps the password off any command line. Password-auth sessions use the
/// \\sshfs.r\ prefix with the session's secret; key-auth sessions use \\sshfs.kr\, which
/// has sshfs pick up the user's default %USERPROFILE%\.ssh key.
/// </summary>
internal static class SshfsIntegration
{
    /// <summary>Checked once per run; an install mid-session is picked up on restart.</summary>
    public static bool IsInstalled { get; } = Detect();

    private static bool Detect()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(root)
                && File.Exists(Path.Combine(root, "SSHFS-Win", "bin", "sshfs-win.exe")))
                return true;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SSHFS-Win")
                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\SSHFS-Win");
            return key is not null;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string Authority(Session session) => session.Port == 22
        ? $"{session.Username}@{session.Host}"
        : $"{session.Username}@{session.Host}!{session.Port}";

    /// <summary>
    /// Establishes the sshfs connection for password-auth sessions (no drive letter — a
    /// deviceless UNC connection) and returns the UNC root, e.g. <c>\\sshfs.r\user@host</c>
    /// (rooted at / so any remote path maps directly). Key-auth sessions use
    /// <see cref="MountWithIdentity"/> instead — the UNC provider cannot carry a key.
    /// Blocking: sshfs connects and authenticates inside this call; run on a background
    /// thread. Throws IOException with a readable message on failure.
    /// </summary>
    public static string Connect(Session session, string? password)
    {
        var root = $@"\\sshfs.r\{Authority(session)}";
        var resource = new NETRESOURCE
        {
            dwType = 1, // RESOURCETYPE_DISK
            lpRemoteName = root,
        };
        var error = WNetAddConnection2W(in resource, password, null, 0);
        // 1219 = ERROR_SESSION_CREDENTIAL_CONFLICT: already connected (possibly via the
        // credential dialog) — that connection works, use it.
        if (error is 0 or 1219)
            return root;
        throw new IOException(DescribeError(error), new System.ComponentModel.Win32Exception(error));
    }

    private static string DescribeError(int error) => error switch
    {
        // ERROR_ACCESS_DENIED family: sshfs reached the host but the SSH login was refused.
        5 or 86 or 1326 => "the server refused the login with the session's password.",
        // ERROR_NO_NET_OR_BAD_PATH: no network provider claimed the UNC at all.
        1203 => @"this SSHFS-Win install did not accept \\sshfs.r\ paths. "
                + "Updating SSHFS-Win (or rebooting after install) may fix it.",
        _ => new System.ComponentModel.Win32Exception(error).Message,
    };

    /// <summary><paramref name="root"/> is either a UNC root (\\sshfs.r\user@host) or a
    /// drive root ("S:"); both map to the remote "/".</summary>
    public static void OpenInExplorer(string root, string? remotePath)
    {
        var tail = string.IsNullOrWhiteSpace(remotePath) || remotePath == "/"
            ? ""
            : Core.Sftp.RemotePath.Normalize(remotePath).Replace('/', '\\');
        if (tail.Length == 0 && !root.StartsWith(@"\\", StringComparison.Ordinal))
            tail = @"\"; // bare "S:" would open the drive's "current directory"
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{root}{tail}\"",
            UseShellExecute = true,
        });
    }

    // ---- direct sshfs.exe mount (key-auth sessions) ----
    // The UNC provider can only carry user+password and sshfs-win hard-wires the auth mode
    // per prefix (\\sshfs.r\ forces password; the key prefixes are not claimed by every
    // install — see Connect). Spawning the bundled sshfs.exe ourselves is the only route
    // that can use the SESSION'S key file.

    private static string? FindSshfsExe()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var exe = Path.Combine(root, "SSHFS-Win", "bin", "sshfs.exe");
            if (File.Exists(exe))
                return exe;
        }
        return null;
    }

    private static char? FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c))
                return c;
        }
        return null;
    }

    /// <summary>
    /// Mounts the session's remote / on a free drive letter using the session's own key
    /// file. Blocking (spawn + wait for the drive to appear); run on a background thread.
    /// Throws IOException with a readable message on failure.
    /// </summary>
    public static SshfsMount MountWithIdentity(Session session)
    {
        var exe = FindSshfsExe()
            ?? throw new IOException("sshfs.exe was not found in the SSHFS-Win install.");
        if (string.IsNullOrEmpty(session.PrivateKeyPath) || !File.Exists(session.PrivateKeyPath))
            throw new IOException($"the session's key file was not found: {session.PrivateKeyPath}");
        if (session.PassphraseRequired)
            throw new IOException(
                "the key is passphrase-protected — sshfs cannot prompt for it. "
                + "Use a passphrase-free key for the Explorer mount.");
        var drive = FreeDriveLetter()
            ?? throw new IOException("no free drive letter for the mount.");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // CRITICAL: a console-less parent (this app) passes a NULL stdin handle when
            // any stream is redirected, and cygwin's init then breaks the ssh pipe chain —
            // same "read: Connection reset by peer" as the wrong-ssh bug. Reproduced and
            // fixed via a winexe probe: giving the child a real stdin pipe cures it.
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add($"{session.Username}@{session.Host}:/");
        psi.ArgumentList.Add($"{drive}:");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(session.Port.ToString());
        psi.ArgumentList.Add("-f"); // foreground: the process IS the mount's lifetime
        foreach (var option in new[]
                 {
                     // CRITICAL: without this, sshfs resolves `ssh` from PATH and can pick
                     // Windows' native OpenSSH, whose stdio can't ride the cygwin socketpair —
                     // the remote sftp-server sees instant EOF and the mount dies with
                     // "read: Connection reset by peer" (diagnosed + verified live).
                     // /usr/bin maps to the SSHFS-Win bin directory in cygwin's view.
                     "ssh_command=/usr/bin/ssh",
                     // Cygwin handles C:/-style paths; backslashes would be mangled.
                     $"IdentityFile={session.PrivateKeyPath.Replace('\\', '/')}",
                     "PreferredAuthentications=publickey",
                     "BatchMode=yes", // fail fast instead of prompting into nowhere
                     // The app's own KnownHostsStore already pinned this host's key via the
                     // terminal; ssh's separate store would just add an unanswerable prompt.
                     "StrictHostKeyChecking=no",
                     "UserKnownHostsFile=/dev/null",
                     // Explorer-friendly defaults, mirroring sshfs-win's own svc mode.
                     "uid=-1", "gid=-1", "umask=000", "create_umask=000",
                     "rellinks", "dothidden", "reconnect", "ServerAliveInterval=15",
                 })
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(option);
        }

        var process = Process.Start(psi)
            ?? throw new IOException("sshfs.exe failed to start.");
        var root = $"{drive}:";
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            if (process.HasExited)
            {
                var stderr = process.StandardError.ReadToEnd();
                process.Dispose();
                throw new IOException(DescribeSshfsFailure(stderr));
            }
            if (Directory.Exists(root + @"\"))
                return new SshfsMount(process, root);
            Thread.Sleep(200);
        }
        try { process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
        throw new IOException("the sshfs mount did not come up within 20 seconds.");
    }

    private static string DescribeSshfsFailure(string stderr)
    {
        var detail = stderr.Trim();
        if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "the server refused the key (permission denied). Check that this key is authorized on the host.";
        return detail.Length > 0
            ? $"sshfs failed: {detail}"
            : "sshfs exited before the mount came up.";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(in NETRESOURCE lpNetResource, string? lpPassword, string? lpUserName, int dwFlags);
}
