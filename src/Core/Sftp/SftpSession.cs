using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Resesh.Core.Ssh;
using Session = Resesh.Core.Models.Session;

namespace Resesh.Core.Sftp;

/// <summary>
/// One live SFTP connection for a session's file pane — a second, independent connection
/// next to the terminal's SshClient, built from the same credentials and host-key trust.
/// All methods are blocking (run them on a background thread) and must not be called
/// concurrently: the pane serializes operations. Transfers report progress in bytes of the
/// current file and honor cancellation, deleting the partial target on cancel.
/// </summary>
public sealed class SftpSession : IDisposable
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly KnownHostsStore _knownHosts;
    private SftpClient? _client;
    private volatile bool _disposed;

    public SftpSession(KnownHostsStore knownHosts)
    {
        _knownHosts = knownHosts;
    }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>The user's home directory, resolved at connect time.</summary>
    public string HomeDirectory { get; private set; } = "/";

    /// <summary>
    /// Connects the SFTP channel. The host key is trusted only when it matches the entry
    /// already accepted through the terminal path — an unknown or changed key fails here
    /// instead of raising a second surprise dialog.
    /// </summary>
    public void Connect(Session session, string? secret,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder = null)
    {
        if (_client is not null)
            throw new InvalidOperationException("Session already used; create a new instance per connection.");

        SshConnectionFactory.PreflightTcp(session.Host, session.Port, TimeSpan.FromSeconds(10));

        var auth = SshConnectionFactory.BuildAuthMethods(session, secret, interactiveResponder);
        var connectionInfo = new ConnectionInfo(session.Host, session.Port, session.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var client = new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            BufferSize = CopyBufferSize,
        };

        SshSessionException? hostKeyFailure = null;
        client.HostKeyReceived += (_, e) =>
        {
            var sha256 = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            var verdict = _knownHosts.Check(session.Host, session.Port, e.HostKeyName, sha256);
            e.CanTrust = verdict == HostKeyVerdict.Match;
            if (!e.CanTrust)
                hostKeyFailure = verdict == HostKeyVerdict.Mismatch
                    ? new SshSessionException(
                        SshFailureKind.HostKeyMismatch,
                        $"Host key for {session.Host}:{session.Port} does not match the trusted key. " +
                        "Reconnect the terminal to review the change.")
                    : new SshSessionException(
                        SshFailureKind.HostKeyRejected,
                        "Host key is not trusted yet — connect the terminal first to accept it.");
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (hostKeyFailure is not null)
                throw hostKeyFailure;
            throw SshConnectionFactory.Classify(ex);
        }

        _client = client;
        HomeDirectory = RemotePath.Normalize(client.WorkingDirectory);
    }

    public IReadOnlyList<RemoteFileEntry> ListDirectory(string path)
    {
        var client = Require();
        var normalized = RemotePath.Normalize(path);
        return RemoteFileEntry.Sort(
            client.ListDirectory(normalized)
                .Where(f => f.Name is not "." and not "..")
                .Select(f => ToEntry(normalized, f)));
    }

    public bool DirectoryExists(string path)
    {
        var client = Require();
        try
        {
            return client.Exists(path) && client.GetAttributes(path).IsDirectory;
        }
        catch (Exception e) when (e is SshException or IOException)
        {
            return false;
        }
    }

    public void Download(string remotePath, string localPath, Action<long>? progress, CancellationToken token)
    {
        var client = Require();
        try
        {
            using var remote = client.OpenRead(remotePath);
            using var local = File.Create(localPath);
            CopyWithProgress(remote, local, progress, token);
        }
        catch (OperationCanceledException)
        {
            TryDeleteLocal(localPath);
            throw;
        }
    }

    public void Upload(string localPath, string remotePath, Action<long>? progress, CancellationToken token)
    {
        var client = Require();
        try
        {
            using var local = File.OpenRead(localPath);
            using var remote = client.Open(remotePath, FileMode.Create, FileAccess.Write);
            CopyWithProgress(local, remote, progress, token);
        }
        catch (OperationCanceledException)
        {
            try { client.DeleteFile(remotePath); } catch (Exception e) when (e is SshException or IOException) { }
            throw;
        }
    }

    public void Rename(string oldPath, string newPath) => Require().RenameFile(oldPath, newPath);

    public void CreateDirectory(string path) => Require().CreateDirectory(path);

    public void ChangePermissions(string path, short mode) => Require().ChangePermissions(path, mode);

    /// <summary>
    /// Deletes a file, symlink, or directory; directories are emptied bottom-up first
    /// (SFTP rmdir only removes empty directories). Symlinks are removed, never followed.
    /// </summary>
    public void Delete(RemoteFileEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var client = Require();
        if (entry.IsDirectory && !entry.IsSymlink)
        {
            foreach (var child in ListDirectory(entry.FullPath))
                Delete(child, token);
            client.DeleteDirectory(entry.FullPath);
        }
        else
        {
            client.DeleteFile(entry.FullPath);
        }
    }

    public void Disconnect()
    {
        if (_disposed)
            return;
        _disposed = true;
        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
            catch (Exception e) when (e is SshConnectionException or ObjectDisposedException or IOException) { }
            client.Dispose();
        }
    }

    public void Dispose() => Disconnect();

    private SftpClient Require()
    {
        var client = _client;
        if (client is null || _disposed || !client.IsConnected)
            throw new SshSessionException(SshFailureKind.HostUnreachable, "The file connection is not open.");
        return client;
    }

    private static RemoteFileEntry ToEntry(string directory, ISftpFile f) => new(
        f.Name,
        RemotePath.Join(directory, f.Name),
        f.IsDirectory,
        f.IsSymbolicLink,
        f.Length,
        f.LastWriteTime,
        Mode: (short)(
            (f.OwnerCanRead ? 400 : 0) + (f.OwnerCanWrite ? 200 : 0) + (f.OwnerCanExecute ? 100 : 0)
            + (f.GroupCanRead ? 40 : 0) + (f.GroupCanWrite ? 20 : 0) + (f.GroupCanExecute ? 10 : 0)
            + (f.OthersCanRead ? 4 : 0) + (f.OthersCanWrite ? 2 : 0) + (f.OthersCanExecute ? 1 : 0)));

    private static void CopyWithProgress(Stream source, Stream target, Action<long>? progress, CancellationToken token)
    {
        var buffer = new byte[CopyBufferSize];
        long done = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;
            target.Write(buffer, 0, read);
            done += read;
            progress?.Invoke(done);
        }
        target.Flush();
    }

    private static void TryDeleteLocal(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
