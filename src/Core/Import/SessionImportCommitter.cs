using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Import;

/// <summary>Commits scanned <see cref="ImportCandidate"/>s from any importer into the
/// session, identity, and SSH key stores in one atomic session write.</summary>
public static class SessionImportCommitter
{
    /// <summary>True when a session of the candidate's kind with its name, host, and port
    /// already exists. A telnet line and an SSH session can share a name and an address
    /// (a console server answers both), so the kind has to match too. Hosts compare without
    /// IPv6 brackets, which older builds kept in a saved session's host.</summary>
    public static bool IsDuplicate(SessionStore store, ImportCandidate candidate) =>
        IsDuplicate(store.Sessions, candidate);

    private static bool IsDuplicate(IEnumerable<Session> sessions, ImportCandidate candidate) =>
        sessions.Any(s =>
            s.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
            && (candidate.IsSerial
                ? s.IsSerial && string.Equals(s.Serial?.PortName, candidate.Serial!.PortName, StringComparison.OrdinalIgnoreCase)
                : s.Kind == (candidate.IsTelnet ? SessionKind.Telnet : SessionKind.Ssh)
                    && HostPort.Unbracket(s.Host).Equals(HostPort.Unbracket(candidate.Host), StringComparison.OrdinalIgnoreCase)
                    && s.Port == candidate.Port));

    /// <summary>
    /// Adds the selected candidates (SSH, telnet or serial, from any importer), skipping any
    /// whose name and endpoint already exist. With an identity plan, sessions sharing a login
    /// use one identity (created or reused) instead of their own. Key files are registered and
    /// new identities saved first, then every session, jump link and folder identity in one
    /// write; if that fails, the keys and identities made for it are removed again, so a
    /// failed import leaves neither sessions pointing at nothing nor identities nobody uses.
    /// The plan is rechecked here: an identity it reuses that has since been deleted is
    /// created again rather than referenced. Returns (imported, duplicatesSkipped).
    /// </summary>
    public static (int Imported, int Duplicates) Commit(SessionStore store, IEnumerable<ImportCandidate> selected,
        SshKeyStore? keys = null, Identities.ImportIdentityPlan? plan = null, IdentityStore? identities = null)
    {
        var imported = 0;
        var duplicates = 0;
        var identityIds = new Dictionary<string, Guid>();
        var newIdentities = new List<Identity>();
        var newKeyIds = new List<Guid>();
        var added = new List<(Session Session, string? JumpHostName)>();
        var existing = store.Sessions;

        void Add(ImportCandidate candidate, Session session)
        {
            session = session with
            {
                Proxy = session.Kind == SessionKind.Ssh ? candidate.Proxy : null,
                Algorithms = session.Kind == SessionKind.Ssh && candidate.Algorithms is { IsEmpty: false } ? candidate.Algorithms : null,
            };
            added.Add((session, candidate.JumpHostName));
        }

        Guid RegisterKey(string path)
        {
            var knownKeys = (keys ?? throw new InvalidOperationException("An SSH key store is required to import keys."))
                .Keys.Select(key => key.Id).ToHashSet();
            var key = keys.RegisterExternal(path, allowMissing: true);
            if (!knownKeys.Contains(key.Id))
                newKeyIds.Add(key.Id);
            return key.Id;
        }

        Guid IdentityFor(Identities.ImportLogin login)
        {
            if (identityIds.TryGetValue(login.Key, out var known))
                return known;
            var suggestion = plan!.Suggestions.First(s => s.Login.Key == login.Key);
            Guid id;
            if (suggestion.Existing is { } reused && identities!.Find(reused.Id) is not null)
                id = reused.Id;
            else
            {
                var keyId = login.UsesKey ? RegisterKey(login.KeyPath) : (Guid?)null;
                var identity = new Identity
                {
                    Name = suggestion.Name,
                    Username = login.Username,
                    Proof = keyId is null ? IdentityProof.Password : IdentityProof.Key,
                    KeyId = keyId,
                };
                newIdentities.Add(identity);
                id = identity.Id;
            }
            identityIds[login.Key] = id;
            return id;
        }

        try
        {
            foreach (var candidate in selected)
            {
                if (IsDuplicate(existing.Concat(added.Select(entry => entry.Session)), candidate))
                {
                    duplicates++;
                    continue;
                }

                if (candidate.IsTelnet)
                {
                    // Telnet carries no credentials: the server prompts in the terminal.
                    Add(candidate, new Session
                    {
                        Kind = SessionKind.Telnet,
                        Name = candidate.Name,
                        FolderPath = candidate.FolderPath,
                        Host = candidate.Host,
                        Port = candidate.Port,
                        AuthMethod = AuthMethod.None,
                    });
                    imported++;
                    continue;
                }

                if (candidate.IsSerial)
                {
                    Add(candidate, new Session
                    {
                        Kind = SessionKind.Serial,
                        Name = candidate.Name,
                        FolderPath = candidate.FolderPath,
                        Serial = candidate.Serial,
                        Port = 0,
                        AuthMethod = AuthMethod.None,
                    });
                    imported++;
                    continue;
                }

                if (identities is not null && plan?.Choices.GetValueOrDefault(candidate) is
                    { Kind: not Identities.ImportLoginKind.Own } choice)
                {
                    Add(candidate, new Session
                    {
                        Name = candidate.Name,
                        FolderPath = candidate.FolderPath,
                        Host = candidate.Host,
                        Port = candidate.Port,
                        InheritLogin = choice.Kind == Identities.ImportLoginKind.Inherit,
                        IdentityId = choice.Kind == Identities.ImportLoginKind.Identity ? IdentityFor(choice.Login!) : null,
                    });
                    if (choice.Kind == Identities.ImportLoginKind.Inherit && choice.Login is { } inherited
                        && plan.Suggestions.Any(s => s.Login.Key == inherited.Key))
                        IdentityFor(inherited);
                    imported++;
                    continue;
                }

                var keyId = !string.IsNullOrWhiteSpace(candidate.PrivateKeyPath)
                    ? RegisterKey(candidate.PrivateKeyPath)
                    : (Guid?)null;

                Add(candidate, new Session
                {
                    Name = candidate.Name,
                    FolderPath = candidate.FolderPath,
                    Host = candidate.Host,
                    Port = candidate.Port,
                    Username = candidate.Username,
                    AuthMethod = keyId is null ? AuthMethod.Password : AuthMethod.PrivateKey,
                    PrivateKeyId = keyId,
                });
                imported++;
            }

            // A jump host is named by the source, so it can only be linked once every session
            // is known: one imported just now, or one saved earlier under that name.
            var byId = existing.ToDictionary(session => session.Id);
            foreach (var (session, _) in added)
                byId[session.Id] = session;
            foreach (var (session, jumpName) in added)
            {
                if (jumpName is null || session.Kind == SessionKind.Local)
                    continue;
                var jump = added.Select(entry => entry.Session).Concat(existing).FirstOrDefault(other =>
                    other.Kind == SessionKind.Ssh && other.Id != session.Id
                    && (other.Name.Equals(jumpName, StringComparison.OrdinalIgnoreCase)
                        || other.Host.Equals(jumpName, StringComparison.OrdinalIgnoreCase)));
                // Checked against each other as well: a source can name two hosts as each other's jump.
                if (jump is not null && !Ssh.JumpRoute.WouldLoop(session.Id, jump.Id, byId.GetValueOrDefault))
                    byId[session.Id] = session with { JumpHostId = jump.Id };
            }

            // Attach only identities some imported session uses, and never over a folder's own
            // (the store keeps a folder's own setting when it merges these).
            var folderSettings = (plan?.Attachments ?? [])
                .Where(attachment => identityIds.ContainsKey(attachment.Login.Key))
                .Select(attachment => new FolderSettings
                {
                    Path = attachment.FolderPath,
                    IdentityId = identityIds[attachment.Login.Key],
                })
                .ToList();

            if (newIdentities.Count > 0)
                identities!.MergeImport(newIdentities);
            store.ApplyImport(added.Select(entry => byId[entry.Session.Id]), [], [], folderSettings);
        }
        catch
        {
            RemoveCreated(identities, newIdentities, keys, newKeyIds);
            throw;
        }

        return (imported, duplicates);
    }

    /// <summary>Best effort: what a failed import made for its sessions goes again. A removal
    /// that fails too leaves an unused identity or key, which the user can delete.</summary>
    private static void RemoveCreated(IdentityStore? identities, List<Identity> newIdentities, SshKeyStore? keys, List<Guid> newKeyIds)
    {
        foreach (var identity in newIdentities)
        {
            try { identities?.Remove(identity.Id); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        foreach (var keyId in newKeyIds)
        {
            try { keys?.Remove(keyId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
