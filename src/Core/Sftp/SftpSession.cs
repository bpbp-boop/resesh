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
/// current file and honor cancellation. A failed or cancelled transfer leaves no partial file:
/// downloads always go to a new local file, and an upload over an existing remote file is
/// written beside it and renamed over it only once complete.
/// </summary>
public sealed class SftpSession : IDisposable
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly KnownHostsStore _knownHosts;
    private readonly Backend.BackendLifecycle _lifecycle = new();
    // Published by exchange as soon as they exist, so a Dispose while Connect runs closes
    // them (cutting the handshake short) and Connect, finding the stop, closes the rest.
    private SftpClient? _client;
    private SshJumpChain? _jumpChain;

    public SftpSession(KnownHostsStore knownHosts)
    {
        _knownHosts = knownHosts;
    }

    public bool IsConnected => _lifecycle.IsOpen && _client?.IsConnected == true;

    /// <summary>The user's home directory, resolved at connect time.</summary>
    public string HomeDirectory { get; private set; } = "/";

    /// <summary>
    /// Connects the SFTP channel. The host key is trusted only when it matches the entry
    /// already accepted through the terminal path — an unknown or changed key fails here
    /// instead of raising a second surprise dialog. A <see cref="Dispose"/> from another
    /// thread, or <paramref name="cancellationToken"/>, abandons the connect: it throws
    /// <see cref="OperationCanceledException"/> and leaves nothing open.
    /// <paramref name="connecting"/> hears of each host before it is dialled (jump hosts, then
    /// the session), so a keyboard-interactive prompt can say which host is asking.
    /// </summary>
    public void Connect(Session session, string? secret,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder = null,
        IReadOnlyList<SshHop>? jumps = null,
        string? proxyPassword = null,
        CancellationToken cancellationToken = default,
        Action<Session>? connecting = null)
    {
        if (!_lifecycle.TryBeginConnect())
            throw _lifecycle.CannotConnect();
        try
        {
            ThrowIfAbandoned(cancellationToken);
            // Its own jump connections, like its own SSH connection. Their keys were accepted
            // through the terminal too, so nothing here asks.
            var via = jumps is { Count: > 0 }
                ? SshJumpChain.Open(jumps, session.Host, Models.SessionEndpoint.EffectivePort(session), _knownHosts,
                    interactiveResponder: interactiveResponder, cancellationToken: cancellationToken,
                    connecting: connecting)
                : null;
            Interlocked.Exchange(ref _jumpChain, via);
            ThrowIfAbandoned(cancellationToken);
            connecting?.Invoke(session);
            var client = ConnectClient(session, secret, interactiveResponder, via, proxyPassword, cancellationToken);
            Interlocked.Exchange(ref _client, client);
            ThrowIfAbandoned(cancellationToken);
            HomeDirectory = RemotePath.Normalize(client.WorkingDirectory);
            if (!_lifecycle.TryPublishOpen())
                throw _lifecycle.CannotConnect();
        }
        catch
        {
            // A failed connect is final: the pane makes a new instance to retry.
            _lifecycle.BeginStop();
            Teardown();
            throw;
        }
    }

    private void ThrowIfAbandoned(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifecycle.IsStopped)
            throw new OperationCanceledException("The file connection was closed while it was connecting.");
    }

    private SftpClient ConnectClient(Session session, string? secret,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder,
        SshJumpChain? via, string? proxyPassword, CancellationToken cancellationToken)
    {
        var auth = SshConnectionFactory.BuildAuthMethods(session, secret, interactiveResponder);
        var connectionInfo = SshConnectionFactory.CreateConnectionInfo(
            session, auth, TimeSpan.FromSeconds(30), via, proxyPassword, _knownHosts);

        var client = new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            BufferSize = CopyBufferSize,
        };

        SshSessionException? hostKeyFailure = null;
        client.HostKeyReceived += (_, e) =>
        {
            var sha256 = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            var verdict = _knownHosts.Check(session.Host, Models.SessionEndpoint.EffectivePort(session), e.HostKeyName, sha256);
            e.CanTrust = verdict == HostKeyVerdict.Match;
            if (e.CanTrust)
                _knownHosts.BackfillHostKey(session.Host, Models.SessionEndpoint.EffectivePort(session), e.HostKeyName, e.HostKey);
            if (!e.CanTrust)
                hostKeyFailure = verdict == HostKeyVerdict.Mismatch
                    ? (_knownHosts.Lookup(session.Host, Models.SessionEndpoint.EffectivePort(session)) is { } trusted
                        ? SshHostKeyTypes.TrustedTypeTurnedOff(connectionInfo, session, trusted, e.HostKeyName)
                        : null) ?? new SshSessionException(
                        SshFailureKind.HostKeyMismatch,
                        $"Host key for {session.Host}:{Models.SessionEndpoint.EffectivePort(session)} does not match the trusted key. " +
                        "Reconnect the terminal to review the change.")
                    : new SshSessionException(
                        SshFailureKind.HostKeyRejected,
                        "Host key is not trusted yet — connect the terminal first to accept it.");
        };

        try
        {
            SshConnectionFactory.Connect(client, session, via, TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception ex)
        {
            client.Dispose();
            ThrowIfAbandoned(cancellationToken);
            if (hostKeyFailure is not null)
                throw hostKeyFailure;
            throw ex as SshSessionException ?? SshConnectionFactory.Classify(ex, session, via);
        }
        finally
        {
            SshConnectionFactory.DisposeAuthMethods(auth);
        }

        return client;
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

    /// <summary>
    /// Downloads to <paramref name="localPath"/>, or, when something has appeared there since
    /// the name was chosen, to the next free "name (2).ext". Returns the path written. The file
    /// is always one this call created, so a failure or cancel deletes it rather than leaving a
    /// truncated download that looks complete.
    /// </summary>
    public string Download(string remotePath, string localPath, Action<long>? progress, CancellationToken token)
    {
        var client = Require();
        using var remote = client.OpenRead(remotePath);
        var (local, path) = CreateNewLocalFile(localPath);
        try
        {
            using (local)
                CopyWithProgress(remote, local, progress, token);
            return path;
        }
        catch
        {
            TryDeleteLocal(path);
            throw;
        }
    }

    /// <summary>Creates the file without ever opening an existing one (CreateNew), picking the
    /// next free name when the planned one was taken in the meantime.</summary>
    private static (FileStream Stream, string Path) CreateNewLocalFile(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var candidate = path;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None), candidate);
            }
            catch (IOException) when (attempt < 100 && (File.Exists(candidate) || Directory.Exists(candidate)))
            {
                candidate = Path.Combine(directory, RemotePath.UniqueName(Path.GetFileName(path),
                    name => File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name))));
            }
        }
    }

    /// <summary>
    /// Uploads <paramref name="localPath"/> to <paramref name="remotePath"/>. A new file is
    /// written in place and deleted if the upload fails. An existing regular file (which the
    /// user agreed to overwrite) is not truncated first: the upload goes to a temporary file
    /// beside it, takes its permissions, and is renamed over it once complete, so a dropped
    /// connection leaves the old file intact. Where a rename would change more than the
    /// contents the file is overwritten in place, as SFTP clients usually do: a symlink (a
    /// rename replaces the link instead of writing through it), a file owned by another user
    /// or group (a rename gives it the login's), and a folder that refuses new files or the
    /// rename (a sticky /tmp).
    /// </summary>
    public void Upload(string localPath, string remotePath, Action<long>? progress, CancellationToken token)
    {
        var client = Require();
        using var local = File.OpenRead(localPath);
        var existing = TryGetFile(client, remotePath);
        if (existing is not null
            && ReplacesByRename(existing.IsRegularFile, existing.IsSymbolicLink, existing.FullName, LinkPath(client, remotePath))
            && TryUploadBeside(client, local, existing, remotePath, progress, token))
        {
            return;
        }

        local.Position = 0;
        var created = existing is null;
        try
        {
            using var target = client.Open(remotePath, FileMode.Create, FileAccess.Write);
            CopyWithProgress(local, target, progress, token);
        }
        catch (Exception e) when (created || e is OperationCanceledException)
        {
            // Only a file this upload created (or a cancelled one, as before) is removed;
            // an existing file overwritten in place stays as far as it got.
            TryDeleteRemote(client, remotePath);
            throw;
        }
    }

    /// <summary>
    /// Whether an existing file can be replaced by renaming a finished upload over it.
    /// <paramref name="resolvedPath"/> is where the server resolved the path: SSH.NET's Get
    /// asks the server's realpath, which follows a symlink, and then lstats the result, so a
    /// link reads as the regular file it points at. <paramref name="linkPath"/> is the path
    /// with only its folder resolved. When they differ the name is a link, and a rename would
    /// replace the link rather than write through it.
    /// </summary>
    internal static bool ReplacesByRename(bool isRegularFile, bool isSymbolicLink, string resolvedPath, string? linkPath) =>
        isRegularFile && !isSymbolicLink && linkPath is not null && string.Equals(resolvedPath, linkPath, StringComparison.Ordinal);

    /// <summary>Whether the temporary file, created by the login, would leave the replaced file
    /// with its owner and group. A rename keeps the new file's, and only root can give a file
    /// away, so another user's file is written in place.</summary>
    internal static bool KeepsOwnership(int existingUser, int existingGroup, int uploadUser, int uploadGroup) =>
        existingUser == uploadUser && existingGroup == uploadGroup;

    /// <summary>The path with its folder resolved by the server and its name as given: a
    /// symlink's own path. Null when the folder cannot be resolved.</summary>
    private static string? LinkPath(SftpClient client, string path)
    {
        try
        {
            return RemotePath.Join(client.Get(RemotePath.Parent(path)).FullName, RemotePath.FileName(path));
        }
        catch (Exception e) when (e is SshException or IOException)
        {
            return null;
        }
    }

    /// <summary>Uploads to a temporary file and renames it over <paramref name="remotePath"/>.
    /// False, with nothing left behind and the target untouched, when that would change the
    /// file's owner or group or the folder refuses the temporary file or the rename; the
    /// caller then overwrites in place.</summary>
    private static bool TryUploadBeside(
        SftpClient client, Stream local, ISftpFile existing, string remotePath, Action<long>? progress, CancellationToken token)
    {
        if (TryOpenTemporary(client, remotePath) is not var (temporary, remote))
            return false;
        try
        {
            using (remote)
            {
                if (TryGetFile(client, temporary) is not { } created
                    || !KeepsOwnership(existing.UserId, existing.GroupId, created.UserId, created.GroupId))
                {
                    remote.Dispose();
                    TryDeleteRemote(client, temporary);
                    return false;
                }
                CopyWithProgress(local, remote, progress, token);
            }
            token.ThrowIfCancellationRequested();
            TryCopyPermissions(client, existing, temporary);
        }
        catch
        {
            TryDeleteRemote(client, temporary);
            throw;
        }
        return ReplaceRemote(client, temporary, remotePath);
    }

    private static ISftpFile? TryGetFile(SftpClient client, string path)
    {
        try
        {
            return client.Get(path);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A new hidden file in the target's folder, so the final rename stays on one
    /// file system; null when the folder does not allow it.</summary>
    private static (string Path, SftpFileStream Stream)? TryOpenTemporary(SftpClient client, string remotePath)
    {
        var temporary = RemotePath.Join(RemotePath.Parent(remotePath),
            $".{RemotePath.FileName(remotePath)}.resesh-{Guid.NewGuid().ToString("N")[..8]}.part");
        try
        {
            return (temporary, client.Open(temporary, FileMode.CreateNew, FileAccess.Write));
        }
        catch (SftpPermissionDeniedException)
        {
            return null;
        }
    }

    /// <summary>Moves the finished upload over the target: one atomic posix-rename where the
    /// server offers it (OpenSSH does), else delete then rename, since plain SFTP rename
    /// refuses to replace a file. Either way the upload is complete before the old file goes.
    /// False, with the upload removed and the old file untouched, when the server refuses to
    /// replace it (a sticky folder, where only a file's owner may), so the caller writes in place.</summary>
    private static bool ReplaceRemote(SftpClient client, string temporary, string remotePath)
    {
        try
        {
            client.RenameFile(temporary, remotePath, isPosix: true);
            return true;
        }
        catch (NotSupportedException)
        {
        }
        catch (SftpPermissionDeniedException)
        {
            TryDeleteRemote(client, temporary);
            return false;
        }
        catch
        {
            TryDeleteRemote(client, temporary);
            throw;
        }
        try
        {
            client.DeleteFile(remotePath);
        }
        catch (SftpPermissionDeniedException)
        {
            TryDeleteRemote(client, temporary);
            return false;
        }
        catch
        {
            TryDeleteRemote(client, temporary);
            throw;
        }
        try
        {
            client.RenameFile(temporary, remotePath);
        }
        catch (Exception e) when (e is SshException or IOException)
        {
            // The old file is gone; the finished upload is the only copy, so it stays.
            throw new IOException(
                $"{RemotePath.FileName(remotePath)} was uploaded but could not be renamed into place. It was kept as {temporary}.", e);
        }
        return true;
    }

    private static void TryCopyPermissions(SftpClient client, ISftpFile original, string path)
    {
        try
        {
            client.ChangePermissions(path, ToEntry("/", original).Mode);
        }
        catch (Exception e) when (e is SshException or IOException)
        {
            // The upload still replaces the file; it just keeps the server's default mode.
        }
    }

    private static void TryDeleteRemote(SftpClient client, string path)
    {
        try { client.DeleteFile(path); } catch (Exception e) when (e is SshException or IOException) { }
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
        if (_lifecycle.BeginStop())
            Teardown();
    }

    /// <summary>Releases whatever is published; safe to run from both Disconnect and a
    /// failing Connect, in either order.</summary>
    private void Teardown()
    {
        var client = Interlocked.Exchange(ref _client, null);
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
        // After the client: the jumps carry its connection.
        Interlocked.Exchange(ref _jumpChain, null)?.Dispose();
    }

    public void Dispose() => Disconnect();

    private SftpClient Require()
    {
        var client = _client;
        if (client is null || !_lifecycle.IsOpen || !client.IsConnected)
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
