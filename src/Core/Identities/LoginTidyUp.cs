using System.Security.Cryptography;
using System.Text;
using Resesh.Core.Credentials;
using Resesh.Core.Import;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Identities;

/// <summary>A tidy-up proposal: the planned identities plus the saved session behind each candidate.</summary>
public sealed record LoginTidyUpPlan
{
    public required ImportIdentityPlan Plan { get; init; }

    /// <summary>The saved session each planned candidate stands for, by reference.</summary>
    public required IReadOnlyDictionary<ImportCandidate, Session> Sessions { get; init; }

    /// <summary>Sessions whose login the plan changes (inherit or explicit identity).</summary>
    public int ChangedSessions => Plan.Choices.Values.Count(choice => choice.Kind != ImportLoginKind.Own);
}

/// <summary>
/// The tidy-up assistant for sessions saved before identities: it groups SSH sessions that
/// keep their own login by username, key, and saved password, and plans identities for them
/// with the importer's rules. Saved passwords are compared in memory only, through an HMAC
/// with a key made for each process, so tokens stay stable while the user ticks and unticks
/// logins but never outlive the app or match across runs.
/// Folders are only attached where no session already inherits its login, so applying the
/// plan changes nothing but the sessions it lists.
/// </summary>
public static class LoginTidyUp
{
    private static readonly byte[] FingerprintKey = RandomNumberGenerator.GetBytes(32);

    public static LoginTidyUpPlan Plan(
        IReadOnlyList<Session> sessions,
        IReadOnlyList<Identity> identities,
        Func<string, Guid?> folderIdentity,
        Func<Guid, string?> keyPathOf,
        Func<Guid, string?> savedPasswordOf,
        IReadOnlySet<string>? declined = null)
    {
        var bySession = new Dictionary<ImportCandidate, Session>(ReferenceEqualityComparer.Instance);
        foreach (var session in sessions)
        {
            if (session.Kind != SessionKind.Ssh || session.Login != SessionLogin.Own
                || string.IsNullOrWhiteSpace(session.Username) || session.AuthMethod == AuthMethod.None)
                continue;
            string? keyPath = null;
            string? fingerprint = null;
            if (session.AuthMethod == AuthMethod.PrivateKey)
            {
                keyPath = session.PrivateKeyId is { } keyId ? keyPathOf(keyId) : session.PrivateKeyPath;
                // A key login whose key can't be found can't be compared; leave it alone.
                if (string.IsNullOrWhiteSpace(keyPath))
                    continue;
            }
            else if (savedPasswordOf(session.Id) is { Length: > 0 } password)
            {
                fingerprint = Convert.ToHexString(HMACSHA256.HashData(FingerprintKey, Encoding.UTF8.GetBytes(password)));
            }
            bySession[new ImportCandidate
            {
                Name = session.Name,
                FolderPath = session.FolderPath,
                Host = session.Host,
                Port = session.Port,
                Username = session.Username,
                Protocol = "SSH2",
                PrivateKeyPath = keyPath,
                PasswordFingerprint = fingerprint,
            }] = session;
        }

        // Attaching a folder where a session already inherits would change that session too.
        var inheriting = sessions
            .Where(s => s.Kind == SessionKind.Ssh && s.Login == SessionLogin.Inherit)
            .Select(s => FolderPaths.Normalize(s.FolderPath))
            .ToList();
        bool CanAttach(string folder) => !inheriting.Any(path => FolderPaths.IsSelfOrDescendant(path, folder));

        var plan = ImportIdentityPlanner.Plan(bySession.Keys.ToList(), identities, folderIdentity, keyPathOf,
            declined, CanAttach);
        return new LoginTidyUpPlan { Plan = plan, Sessions = bySession };
    }

    /// <summary>
    /// Saves the plan: new identities first (with the shared saved password when the group had
    /// one), then folder attachments and every changed session in one write, then drops the
    /// per-session passwords they no longer use. If the session write fails, the identities and
    /// folder attachments made for it are removed again. Returns the identities created.
    /// </summary>
    public static IReadOnlyList<Identity> Apply(LoginTidyUpPlan tidyUp, SessionStore store, IdentityStore identities,
        SshKeyStore keys, ICredentialService credentials)
    {
        var plan = tidyUp.Plan;
        var identityIds = new Dictionary<string, Guid>();
        var created = new List<Identity>();
        var attached = new List<string>();

        Guid IdentityFor(ImportLogin login)
        {
            if (identityIds.TryGetValue(login.Key, out var known))
                return known;
            var suggestion = plan.Suggestions.First(s => s.Login.Key == login.Key);
            if (suggestion.Existing is { } reused && identities.Find(reused.Id) is not null)
                return identityIds[login.Key] = reused.Id;

            var members = tidyUp.Sessions
                .Where(pair => plan.Choices.GetValueOrDefault(pair.Key)?.Login?.Key == login.Key)
                .Select(pair => pair.Value)
                .ToList();
            Guid? keyId = login.UsesKey
                ? members.Select(s => s.PrivateKeyId).FirstOrDefault(id => id is not null)
                    ?? keys.RegisterExternal(login.KeyPath, allowMissing: true).Id
                : null;
            var identity = identities.Add(new Identity
            {
                Name = suggestion.Name,
                Username = login.Username,
                Proof = keyId is null ? IdentityProof.Password : IdentityProof.Key,
                KeyId = keyId,
            });
            created.Add(identity);
            if (login.SharesSavedPassword
                && members.Select(s => credentials.Read(s.Id)).FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } password)
                credentials.WriteIdentity(identity.Id, password);
            return identityIds[login.Key] = identity.Id;
        }

        var updated = new List<Session>();
        var dropPasswords = new List<Guid>();
        try
        {
            foreach (var (candidate, session) in tidyUp.Sessions)
            {
                if (plan.Choices.GetValueOrDefault(candidate) is not { Kind: not ImportLoginKind.Own, Login: { } login } choice
                    || !plan.Suggestions.Any(s => s.Login.Key == login.Key))
                    continue;
                var identityId = IdentityFor(login);
                if (session.UsesOwnPassword)
                    dropPasswords.Add(session.Id);
                updated.Add(session with
                {
                    Username = "",
                    InheritLogin = choice.Kind == ImportLoginKind.Inherit,
                    IdentityId = choice.Kind == ImportLoginKind.Identity ? identityId : null,
                    PrivateKeyId = null,
                    PrivateKeyPath = null,
                });
            }
            foreach (var (folder, login) in plan.Attachments)
            {
                store.SetFolderIdentity(folder, IdentityFor(login));
                attached.Add(folder);
            }
            store.UpdateMany(updated);
        }
        catch
        {
            foreach (var folder in attached)
                store.SetFolderIdentity(folder, null);
            foreach (var identity in created)
            {
                identities.Remove(identity.Id);
                credentials.DeleteIdentity(identity.Id);
            }
            throw;
        }

        foreach (var id in dropPasswords)
            credentials.Delete(id);
        return created;
    }
}
