using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Identities;

/// <summary>Where a resolved login came from.</summary>
public enum LoginSource
{
    /// <summary>The session's own username and credential.</summary>
    Session,

    /// <summary>An identity the session chose.</summary>
    SessionIdentity,

    /// <summary>The identity of the session's folder or one of its ancestors.</summary>
    Folder,

    /// <summary>Nothing applies (or the chosen identity is gone): ask when connecting.</summary>
    None,
}

/// <summary>
/// The login a session connects with. <see cref="CredentialId"/> is the Credential Manager
/// entry holding its password (the session's or the identity's); null when nothing is stored
/// (key logins keep their passphrase with the key, Ask logins keep it in memory).
/// </summary>
public sealed record ResolvedLogin
{
    public LoginSource Source { get; init; }

    /// <summary>The folder the identity is attached to, for <see cref="LoginSource.Folder"/>.</summary>
    public string? FolderPath { get; init; }

    public Identity? Identity { get; init; }

    /// <summary>The username to log in with; empty when it must be asked.</summary>
    public string Username { get; init; } = "";

    /// <summary>True when the session overrides the identity's username.</summary>
    public bool UsernameOverridden { get; init; }

    public AuthMethod AuthMethod { get; init; }
    public Guid? KeyId { get; init; }

    /// <summary>True when the session chose an identity that no longer exists.</summary>
    public bool IdentityMissing { get; init; }

    public Guid? CredentialId =>
        Source == LoginSource.Session ? null
        : Identity is { Proof: IdentityProof.Password } identity ? identity.Id
        : null;

    /// <summary>A copy of <paramref name="session"/> carrying this login's username, auth
    /// method, and key, as the SSH layer expects them.</summary>
    public Session ApplyTo(Session session) => Source == LoginSource.Session
        ? session
        : session with
        {
            Username = Username,
            AuthMethod = AuthMethod,
            PrivateKeyId = KeyId,
            PrivateKeyPath = null,
            PassphraseRequired = false,
        };
}

/// <summary>Resolves session logins: the session's own setting, then the nearest folder
/// identity (self, then ancestors, then the tree root), then a prompt.</summary>
public sealed class LoginResolver(Func<string, Guid?> folderIdentity, Func<Guid, Identity?> findIdentity)
{
    public LoginResolver(SessionStore sessions, IdentityStore identities)
        : this(sessions.FolderIdentity, identities.Find)
    {
    }

    public ResolvedLogin Resolve(Session session)
    {
        switch (session.Login)
        {
            case SessionLogin.Own:
                return new ResolvedLogin
                {
                    Source = LoginSource.Session,
                    Username = session.Username,
                    AuthMethod = session.AuthMethod,
                    KeyId = session.AuthMethod == AuthMethod.PrivateKey ? session.PrivateKeyId : null,
                };

            case SessionLogin.Identity:
                return findIdentity(session.IdentityId!.Value) is { } chosen
                    ? FromIdentity(session, chosen, LoginSource.SessionIdentity, null)
                    : Unresolved(session, identityMissing: true);

            default:
                foreach (var path in FolderPaths.SelfAndAncestors(session.FolderPath).Append(""))
                {
                    if (folderIdentity(path) is { } id && findIdentity(id) is { } inherited)
                        return FromIdentity(session, inherited, LoginSource.Folder, path);
                }
                return Unresolved(session, identityMissing: false);
        }
    }

    /// <summary>The identity a new or moved session in <paramref name="folderPath"/> inherits,
    /// and the folder it is attached to.</summary>
    public (Identity Identity, string FolderPath)? InheritedAt(string folderPath)
    {
        foreach (var path in FolderPaths.SelfAndAncestors(folderPath).Append(""))
        {
            if (folderIdentity(path) is { } id && findIdentity(id) is { } identity)
                return (identity, path);
        }
        return null;
    }

    private static ResolvedLogin FromIdentity(Session session, Identity identity, LoginSource source, string? folder)
    {
        var overridden = !string.IsNullOrWhiteSpace(session.Username);
        return new ResolvedLogin
        {
            Source = source,
            FolderPath = folder,
            Identity = identity,
            Username = overridden ? session.Username.Trim() : identity.Username,
            UsernameOverridden = overridden,
            AuthMethod = identity.AuthMethod,
            KeyId = identity.Proof == IdentityProof.Key ? identity.KeyId : null,
        };
    }

    private static ResolvedLogin Unresolved(Session session, bool identityMissing) => new()
    {
        Source = LoginSource.None,
        Username = session.Username.Trim(),
        AuthMethod = AuthMethod.Password,
        IdentityMissing = identityMissing,
    };

    /// <summary>Remote sessions whose login currently resolves to <paramref name="identityId"/>.</summary>
    public IReadOnlyList<Session> UsersOf(Guid identityId, IEnumerable<Session> sessions) =>
        sessions.Where(s => s.Kind == SessionKind.Ssh && Resolve(s).Identity?.Id == identityId).ToList();
}
