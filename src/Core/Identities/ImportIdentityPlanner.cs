using Resesh.Core.Import;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Identities;

/// <summary>A login shared by imported sessions: a username plus, optionally, a key file
/// and the fingerprint of the password the source saved for it.</summary>
public sealed record ImportLogin(string Username, string KeyPath, string PasswordFingerprint = "")
{
    /// <summary>Stable key for checkbox state and plan lookups. SSH usernames are case-sensitive.</summary>
    public string Key => $"{Username}|{KeyPath.ToLowerInvariant()}|{PasswordFingerprint}";

    public bool UsesKey => KeyPath.Length > 0;

    /// <summary>The source saved the same password for every session with this login.</summary>
    public bool SharesSavedPassword => PasswordFingerprint.Length > 0;
}

/// <summary>An identity the import proposes (or an existing identity it reuses).</summary>
public sealed record IdentitySuggestion
{
    public required ImportLogin Login { get; init; }
    public required string Name { get; init; }

    /// <summary>The existing identity with the same login, reused instead of creating one.</summary>
    public Identity? Existing { get; init; }

    /// <summary>Imported sessions that would log in with it.</summary>
    public int SessionCount { get; init; }

    /// <summary>Folders it would be attached to (empty when only chosen per session).</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>
    /// True when the sessions very likely share one secret: a key file, the same saved
    /// password, an identity that already exists, or a directory account (DOMAIN\user,
    /// user@domain). A repeated plain username with nothing saved may have a different
    /// password on each host, so it is offered but not chosen by default.
    /// </summary>
    public bool LikelyShared => Login.UsesKey || Login.SharesSavedPassword || Existing is not null
        || Login.Username.Contains('\\') || Login.Username.Contains('@');
}

public enum ImportLoginKind { Own, Inherit, Identity }

/// <summary>How one imported SSH session logs in after the import.</summary>
public sealed record ImportLoginChoice(ImportLoginKind Kind, ImportLogin? Login = null);

/// <summary>The identities an import creates, where they attach, and how each session logs in.</summary>
public sealed record ImportIdentityPlan
{
    public IReadOnlyList<IdentitySuggestion> Suggestions { get; init; } = [];
    public IReadOnlyList<(string FolderPath, ImportLogin Login)> Attachments { get; init; } = [];

    /// <summary>Per candidate, by reference. Candidates not listed keep their own login.</summary>
    public IReadOnlyDictionary<ImportCandidate, ImportLoginChoice> Choices { get; init; } =
        new Dictionary<ImportCandidate, ImportLoginChoice>(ReferenceEqualityComparer.Instance);

    public static ImportIdentityPlan Empty { get; } = new();
}

/// <summary>
/// Turns repeated usernames in an import into identities: a login used by two or more
/// imported sessions (or matching an existing identity) becomes one identity, attached to
/// each folder where most sessions use it. Sessions inside inherit it; the rest choose it
/// explicitly or keep their own login. Imported sessions with no username inherit, so they
/// pick up a folder identity or ask once for the whole folder.
/// </summary>
public static class ImportIdentityPlanner
{
    public static ImportIdentityPlan Plan(
        IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<Identity> existingIdentities,
        Func<string, Guid?> existingFolderIdentity,
        Func<Guid, string?> keyPathOf,
        IReadOnlySet<string>? declined = null)
    {
        declined ??= new HashSet<string>();
        var ssh = candidates.Where(c => !c.IsTelnet).ToList();
        var loginOf = new Dictionary<ImportCandidate, ImportLogin?>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in ssh)
            loginOf[candidate] = LoginOf(candidate);

        // Existing identities by login (password and ask-once both mean "a password").
        var existingByKey = new Dictionary<string, Identity>();
        foreach (var identity in existingIdentities)
        {
            var login = LoginOf(identity, keyPathOf);
            if (login is not null)
                existingByKey.TryAdd(login.Key, identity);
        }

        var proposed = loginOf.Values.OfType<ImportLogin>()
            .GroupBy(login => login.Key)
            .Where(group => !declined.Contains(group.Key)
                && (group.Count() >= 2 || existingByKey.ContainsKey(group.Key)))
            .ToDictionary(group => group.Key, group => group.First());

        var choices = new Dictionary<ImportCandidate, ImportLoginChoice>(ReferenceEqualityComparer.Instance);
        var attachments = new List<(string, ImportLogin)>();
        var folders = AllFolders(ssh);

        void Assign(string folder, ImportLogin? inherited)
        {
            if (folder.Length > 0 && existingFolderIdentity(folder) is { } localId)
            {
                // The folder already has an identity here; imported sessions inherit it only when it matches.
                var local = existingIdentities.FirstOrDefault(i => i.Id == localId);
                inherited = local is null ? null : LoginOf(local, keyPathOf);
            }
            else if (folder.Length > 0)
            {
                var subtree = ssh.Where(c => FolderPaths.IsSelfOrDescendant(c.FolderPath, folder)).ToList();
                var dominant = subtree
                    .Select(c => loginOf[c])
                    .OfType<ImportLogin>()
                    .Where(login => proposed.ContainsKey(login.Key))
                    .GroupBy(login => login.Key)
                    .OrderByDescending(group => group.Count())
                    .FirstOrDefault();
                if (dominant is not null && dominant.Key != inherited?.Key && dominant.Count() * 2 > subtree.Count)
                {
                    inherited = proposed[dominant.Key];
                    attachments.Add((folder, inherited));
                }
            }

            foreach (var candidate in ssh.Where(c => FolderPaths.Normalize(c.FolderPath).Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                var login = loginOf[candidate];
                choices[candidate] = login is null ? new ImportLoginChoice(ImportLoginKind.Inherit)
                    : login.Key == inherited?.Key ? new ImportLoginChoice(ImportLoginKind.Inherit, login)
                    : proposed.ContainsKey(login.Key) ? new ImportLoginChoice(ImportLoginKind.Identity, login)
                    : new ImportLoginChoice(ImportLoginKind.Own, login);
            }

            foreach (var child in folders.Where(f => FolderPaths.Parent(f).Equals(folder, StringComparison.OrdinalIgnoreCase) && f.Length > 0))
                Assign(child, inherited);
        }

        // The tree root is never attached by an import: that would change existing sessions.
        var rootIdentity = existingFolderIdentity("") is { } rootId
            ? existingIdentities.FirstOrDefault(i => i.Id == rootId)
            : null;
        Assign("", rootIdentity is null ? null : LoginOf(rootIdentity, keyPathOf));

        var counted = proposed.Values
            .Select(login => (Login: login, Count: choices.Values.Count(choice =>
                choice.Kind != ImportLoginKind.Own && choice.Login?.Key == login.Key)))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Login.Key, StringComparer.Ordinal)
            .ToList();
        var suggestions = new List<IdentitySuggestion>();
        foreach (var (login, count) in counted)
        {
            var existing = existingByKey.GetValueOrDefault(login.Key);
            suggestions.Add(new IdentitySuggestion
            {
                Login = login,
                Existing = existing,
                Name = existing?.Name ?? DistinctName(login, suggestions, ssh, loginOf),
                SessionCount = count,
                Folders = attachments.Where(a => a.Item2.Key == login.Key).Select(a => a.Item1).ToList(),
            });
        }

        // Logins of one user sit together, the user with the most sessions first.
        var userTotals = suggestions.GroupBy(sg => sg.Login.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(sg => sg.SessionCount), StringComparer.OrdinalIgnoreCase);
        suggestions = suggestions
            .OrderByDescending(sg => userTotals[sg.Login.Username])
            .ThenBy(sg => sg.Login.Username, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(sg => sg.SessionCount)
            .ToList();

        return new ImportIdentityPlan { Suggestions = suggestions, Attachments = attachments, Choices = choices };
    }

    /// <summary>The most-used login for a username keeps the plain name; others with that
    /// username are told apart by key file, then by the folder most of their sessions are in.</summary>
    private static string DistinctName(ImportLogin login, IReadOnlyList<IdentitySuggestion> earlier,
        IReadOnlyList<ImportCandidate> ssh, IReadOnlyDictionary<ImportCandidate, ImportLogin?> loginOf)
    {
        bool Taken(string name) => earlier.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!Taken(login.Username))
            return login.Username;
        var byKey = $"{login.Username} ({Path.GetFileName(login.KeyPath)})";
        if (login.UsesKey && !Taken(byKey) && !earlier.Any(s => s.Login.KeyPath == login.KeyPath
                && s.Login.Username.Equals(login.Username, StringComparison.OrdinalIgnoreCase)))
            return byKey;
        var folder = ssh.Where(c => loginOf[c]?.Key == login.Key)
            .GroupBy(c => FolderPaths.Normalize(c.FolderPath), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? "";
        var place = folder.Length == 0 ? "top level" : folder;
        var name = $"{login.Username} ({place})";
        // Two logins for one user can live mostly in the same folder: number the later ones.
        for (var n = 2; Taken(name); n++)
            name = $"{login.Username} ({place} {n})";
        return name;
    }

    private static ImportLogin? LoginOf(ImportCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Username))
            return null;
        var keyPath = NormalizeKeyPath(candidate.PrivateKeyPath);
        return new ImportLogin(candidate.Username.Trim(), keyPath,
            keyPath.Length > 0 ? "" : candidate.PasswordFingerprint ?? "");
    }

    private static ImportLogin? LoginOf(Identity identity, Func<Guid, string?> keyPathOf)
    {
        if (string.IsNullOrWhiteSpace(identity.Username))
            return null;
        var keyPath = identity.Proof == IdentityProof.Key
            ? identity.KeyId is { } keyId && keyPathOf(keyId) is { } path && !string.IsNullOrWhiteSpace(path)
                ? NormalizeKeyPath(path) : null
            : "";
        return keyPath is null ? null : new ImportLogin(identity.Username.Trim(), keyPath);
    }

    private static string NormalizeKeyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static List<string> AllFolders(IEnumerable<ImportCandidate> candidates)
    {
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            foreach (var folder in FolderPaths.SelfAndAncestors(candidate.FolderPath))
                all.Add(folder);
        }
        return all.ToList();
    }
}
