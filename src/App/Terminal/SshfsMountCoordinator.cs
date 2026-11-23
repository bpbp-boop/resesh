using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.App.Terminal;

/// <summary>
/// Manages one terminal tab's SSHFS-Win mount lifecycle: coalesces concurrent mount requests,
/// validates passphrase-free private keys against their registered fingerprint, probes liveness
/// and disposes stale/closed mounts off the UI thread.
/// </summary>
internal sealed class SshfsMountCoordinator : IDisposable
{
    private readonly KnownHostsStore _knownHosts;
    private readonly SshKeyStore _sshKeys;
    private readonly Func<Guid, string?, CancellationToken, Task<SshKeyReference>> _validateKey;
    private readonly CancellationToken _closing;

    private Interop.SshfsMount? _mount;
    private string? _target;
    private Task<Interop.SshfsMount?>? _mounting;
    private bool _disposed;

    public SshfsMountCoordinator(
        KnownHostsStore knownHosts,
        SshKeyStore sshKeys,
        Func<Guid, string?, CancellationToken, Task<SshKeyReference>> validateKey,
        CancellationToken closing)
    {
        _knownHosts = knownHosts;
        _sshKeys = sshKeys;
        _validateKey = validateKey;
        _closing = closing;
    }

    /// <summary>Returns a live mount for <paramref name="session"/>, starting one if none is
    /// up or the session's target changed, or null when the tab closes while mounting.</summary>
    public async Task<Interop.SshfsMount?> EnsureMountedAsync(Session session, string secret, bool hasJumps)
    {
        // SSHFS-Win dials the host itself, so it cannot follow a jump host or proxy.
        if (hasJumps || session.JumpHostId is not null || JumpRoute.ActiveProxy(session) is not null)
            throw new IOException("File Explorer connects straight to the host, so it cannot open a session "
                + "reached through a jump host or proxy. Use the file pane instead.");

        var mount = _mount;
        // A mount made for another target (the session was edited and reconnected) is replaced.
        // IsAlive probes the mounted root, which can stall on a hung remote mount, so check off the UI thread.
        var mountAlive = mount is not null
            && _target == TargetKey(session)
            && await Task.Run(() => mount.IsAlive, _closing);
        if (_disposed)
            return null;
        if (mountAlive)
            return mount;

        var mounting = _mounting ??= MountAsync(session, secret);
        try
        {
            mount = await mounting;
        }
        finally
        {
            if (ReferenceEquals(_mounting, mounting))
                _mounting = null;
        }
        return _disposed ? null : mount;
    }

    private async Task<Interop.SshfsMount?> MountAsync(Session session, string secret)
    {
        var token = _closing;
        var stale = _mount;
        _mount = null;
        var password = session.AuthMethod == AuthMethod.Password ? secret : null;
        Interop.SshfsMount mount;
        try
        {
            // The key file is checked against its registered fingerprint, as every
            // connection's is; the terminal's check was made when it connected.
            var keyPath = session.AuthMethod == AuthMethod.PrivateKey ? await ValidateKeyAsync(session, token) : null;
            // Killing a dead mount's process can wait, so it happens off the UI thread too.
            mount = await Task.Run(() =>
            {
                stale?.Dispose();
                return Interop.SshfsIntegration.Mount(session, _knownHosts, password, keyPath, token);
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw new IOException("The changed SSH key was not accepted.");
        }
        if (_disposed)
        {
            // The tab closed during the mount; its Dispose could not see this process.
            _ = Task.Run(mount.Dispose);
            return null;
        }
        _mount = mount;
        _target = TargetKey(session);
        return mount;
    }

    /// <summary>The key file an Explorer mount logs in with, once its fingerprint is checked.</summary>
    private async Task<string> ValidateKeyAsync(Session session, CancellationToken token)
    {
        const string encrypted = "The key is passphrase-protected, and sshfs cannot prompt for it. "
            + "Use a passphrase-free key for the Explorer mount.";
        if (session.PrivateKeyId is not { } keyId || _sshKeys.Find(keyId) is not { } registered)
            throw new IOException("The session does not have a registered SSH key.");
        if (registered.IsEncrypted == true)
            throw new IOException(encrypted);
        SshKeyReference key;
        try
        {
            key = await _validateKey(keyId, null, token);
        }
        catch (SshKeyPassphraseException)
        {
            throw new IOException(encrypted);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        if (key.IsEncrypted == true)
            throw new IOException(encrypted);
        return key.Path;
    }

    /// <summary>What an SSHFS mount was made for: host, port and login (the registered key,
    /// whose current file the mount reads, not a path the session once had).</summary>
    private static string TargetKey(Session session) =>
        $"{session.Username}@{session.Host}:{SessionEndpoint.EffectivePort(session)}|{session.AuthMethod}|"
        + (session.AuthMethod == AuthMethod.PrivateKey ? session.PrivateKeyId?.ToString() : null);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var mount = _mount;
        _mount = null;
        if (mount is not null)
            _ = Task.Run(mount.Dispose);
    }
}
