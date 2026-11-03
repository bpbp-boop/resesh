using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.App.Interop;

/// <summary>A foreground sshfs process and its private host-key file, owned by the tab.</summary>
internal sealed partial class SshfsMount : IDisposable
{
    private readonly Process _process;
    private readonly string _knownHostsDirectory;
    private readonly Task _stderr;
    private readonly StringBuilder _stderrTail = new(16 * 1024);
    private readonly Action? _releaseDrive;
    private volatile bool _disposed;

    /// <summary>Drive root, e.g. "S:" — maps to the remote "/".</summary>
    public string Root { get; }

    internal SshfsMount(Process process, string root, string knownHostsDirectory, Action? releaseDrive = null)
    {
        _process = process;
        Root = root;
        _knownHostsDirectory = knownHostsDirectory;
        _releaseDrive = releaseDrive;
        // Drain both pipes throughout the mount's life, not just when startup fails.
        _stderr = DrainErrorAsync(process.StandardError);
        _ = DrainOutputAsync(process.StandardOutput);
    }

    /// <summary>True once sshfs has ended, and after Dispose (the process object is gone then).</summary>
    public bool HasExited
    {
        get
        {
            if (_disposed) return true;
            try { return _process.HasExited; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
        }
    }
    public string FailureDetail
    {
        get
        {
            // An orphaned ssh child can retain the pipe even after sshfs exits.
            _stderr.Wait(2000);
            lock (_stderrTail) return _stderrTail.ToString();
        }
    }

    public bool IsAlive
    {
        get
        {
            if (_disposed) return false;
            try { return !_process.HasExited && Directory.Exists(Root + @"\"); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    private static async Task DrainOutputAsync(StreamReader reader)
    {
        try { await reader.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private async Task DrainErrorAsync(StreamReader reader)
    {
        const int limit = 16 * 1024;
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Append(buffer, 0, count);
                    if (_stderrTail.Length > limit)
                        _stderrTail.Remove(0, _stderrTail.Length - limit);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally
        {
            _process.Dispose();
            _releaseDrive?.Invoke();
            Directory.Delete(_knownHostsDirectory, recursive: true);
        }
    }
}

/// <summary>Optional SSHFS-Win drive mounts, verified against the app's accepted host key.</summary>
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
        catch (System.Security.SecurityException) { return false; }
    }

    /// <summary>The drive root maps to the remote "/".</summary>
    public static void OpenInExplorer(string root, string? remotePath)
    {
        var tail = string.IsNullOrWhiteSpace(remotePath) || remotePath == "/"
            ? @"\"
            : Core.Sftp.RemotePath.Normalize(remotePath).Replace('/', '\\');
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{root}{tail}\"",
            UseShellExecute = true,
        });
    }

    private static string? FindSshfsExe()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "SSHFS-Win", "bin", "sshfs.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>A letter no drive uses and no other tab's mount holds; the caller releases it.</summary>
    private static char? ReserveFreeDriveLetter() =>
        DriveLetterReservations.Shared.Reserve(DriveInfo.GetDrives().Select(d => d.Name[0]));

    private static string CreateKnownHostsDirectory()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("The current Windows user could not be identified.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "resesh-sshfs-" + Guid.NewGuid().ToString("N")));
        // Create with the private DACL in place, rather than narrowing access after writing.
        directory.Create(security);
        return directory.FullName;
    }

    // OpenSSH parses path values as config tokens; quote spaces and escape FUSE option commas.
    private static string PathOption(string name, string path) =>
        $"{name}=\"{path.Replace('\\', '/').Replace(",", "\\,")}\"";

    /// <summary>
    /// Mounts remote / on a free drive using the session's credentials and trusted host key.
    /// Blocking (spawn + wait); run on a background thread. Passwords travel only over stdin.
    /// Cancelling (the tab closed) stops the wait and kills sshfs.
    /// </summary>
    public static SshfsMount Mount(Session session, KnownHostsStore knownHosts, string? password,
        CancellationToken cancellationToken = default)
    {
        var entry = knownHosts.Lookup(session.Host, session.Port)
            ?? throw new IOException("No trusted host key is stored. Connect the terminal first, then open Explorer again.");
        var line = entry.ToKnownHostsLine(session.Host, session.Port);
        var exe = FindSshfsExe()
            ?? throw new IOException("sshfs.exe was not found in the SSHFS-Win install.");
        string? keyPath = null;
        if (session.AuthMethod == AuthMethod.PrivateKey)
        {
            // The key registry is the one record of a key's file and whether it is encrypted;
            // a session (resolved login or not) only names the key.
            var key = session.PrivateKeyId is { } keyId ? App.SshKeys.Find(keyId) : null;
            if (key is null)
                throw new IOException("the session does not have a registered SSH key.");
            keyPath = key.Path;
            if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
                throw new IOException($"the session's key file was not found: {keyPath}");
            if (key.IsEncrypted == true)
                throw new IOException("the key is passphrase-protected — sshfs cannot prompt for it. "
                    + "Use a passphrase-free key for the Explorer mount.");
        }
        else if (password is null)
            throw new IOException("The session's password is not available. Connect the terminal first.");
        else if (password.Contains('\n') || password.Contains('\r'))
            throw new IOException("SSHFS cannot send a password containing a line break through password_stdin.");
        cancellationToken.ThrowIfCancellationRequested();
        var drive = ReserveFreeDriveLetter() ?? throw new IOException("no free drive letter for the mount.");
        var releaseDrive = () => DriveLetterReservations.Shared.Release(drive);
        string directory;
        try { directory = CreateKnownHostsDirectory(); }
        catch
        {
            releaseDrive();
            throw;
        }
        SshfsMount? mount = null;
        try
        {
            var knownHostsPath = Path.Combine(directory, "known_hosts");
            File.WriteAllText(knownHostsPath, line + "\n", new UTF8Encoding(false));
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                // Cygwin needs a real stdin handle even for key auth in a console-less app.
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            var host = session.Host.Contains(':') && !session.Host.StartsWith('[')
                ? $"[{session.Host}]" : session.Host;
            psi.ArgumentList.Add($"{session.Username}@{host}:/");
            psi.ArgumentList.Add($"{drive}:");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(session.Port.ToString());
            psi.ArgumentList.Add("-f"); // the foreground process IS the mount's lifetime
            var options = new List<string>
            {
                // Windows OpenSSH cannot use sshfs's Cygwin socketpair; use the bundled ssh.
                "ssh_command=/usr/bin/ssh",
                "StrictHostKeyChecking=yes",
                PathOption("UserKnownHostsFile", knownHostsPath),
                "GlobalKnownHostsFile=/dev/null", // no other trust store can authorize a different key
                "uid=-1", "gid=-1", "umask=000", "create_umask=000",
                "rellinks", "dothidden", "reconnect", "ServerAliveInterval=15",
            };
            if (session.AuthMethod == AuthMethod.PrivateKey)
            {
                options.Add(PathOption("IdentityFile", keyPath!));
                options.Add("PreferredAuthentications=publickey");
                options.Add("BatchMode=yes");
            }
            else
            {
                options.Add("PreferredAuthentications=password");
                options.Add("BatchMode=no");
                options.Add("NumberOfPasswordPrompts=1");
                options.Add("password_stdin");
            }
            foreach (var option in options)
            {
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(option);
            }
            var process = Process.Start(psi) ?? throw new IOException("sshfs.exe failed to start.");
            mount = new SshfsMount(process, $"{drive}:", directory, releaseDrive);
            if (session.AuthMethod != AuthMethod.PrivateKey)
            {
                process.StandardInput.Write(password);
                process.StandardInput.Write('\n');
            }
            process.StandardInput.Close();
            var deadline = Environment.TickCount64 + 20_000;
            while (Environment.TickCount64 < deadline)
            {
                if (mount.HasExited)
                    throw new IOException(DescribeSshfsFailure(mount.FailureDetail));
                if (mount.IsAlive) return mount;
                if (cancellationToken.WaitHandle.WaitOne(200))
                    cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("the sshfs mount did not come up within 20 seconds.");
        }
        catch
        {
            if (mount is not null) mount.Dispose(); // releases the drive letter too
            else
            {
                releaseDrive();
                Directory.Delete(directory, recursive: true);
            }
            throw;
        }
    }

    private static string DescribeSshfsFailure(string stderr)
    {
        var detail = stderr.Trim();
        if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "the server refused the session's credentials (permission denied). Check the password or authorized key.";
        return detail.Length > 0 ? $"sshfs failed: {detail}" : "sshfs exited before the mount came up.";
    }
}
