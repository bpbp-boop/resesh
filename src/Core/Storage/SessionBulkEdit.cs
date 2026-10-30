using Resesh.Core.Models;

namespace Resesh.Core.Storage;

/// <summary>
/// One change applied to many sessions at once. Every member defaults to "keep what the
/// session has"; a field that does not exist for a session's kind is skipped for it.
/// </summary>
public sealed record SessionBulkEdit
{
    /// <summary>Inherit or Identity; null keeps each session's login. Own is not a bulk
    /// choice: it needs a username and secret per session. SSH sessions only.</summary>
    public SessionLogin? Login { get; init; }

    /// <summary>The identity to log in as when <see cref="Login"/> is Identity.</summary>
    public Guid? IdentityId { get; init; }

    /// <summary>Keeps each session's username as an override of the identity's username.
    /// Off by default: a session that had its own login would otherwise keep that name.</summary>
    public bool KeepUsernames { get; init; }

    /// <summary>SSH and telnet sessions only.</summary>
    public int? Port { get; init; }

    /// <summary>With <see cref="SetJumpHost"/>, null connects without a jump host. SSH and
    /// telnet sessions only; a session is never made its own jump.</summary>
    public bool SetJumpHost { get; init; }
    public Guid? JumpHostId { get; init; }

    public bool SetColor { get; init; }
    public string? ColorTag { get; init; }

    public bool SetIcon { get; init; }
    public string? Icon { get; init; }

    /// <summary>With <see cref="SetTheme"/>, null returns the sessions to the app theme.</summary>
    public bool SetTheme { get; init; }
    public string? Theme { get; init; }

    /// <summary>SSH sessions only. Never turned on for a session that uses PowerShell
    /// shell integration, which cannot run inside tmux.</summary>
    public bool? Persistent { get; init; }

    /// <summary>What keeps a persistent shell alive: the Resesh agent (true) or tmux
    /// (false). SSH sessions only, and only kept on a session that is persistent.</summary>
    public bool? SessionAgent { get; init; }

    public bool IsEmpty =>
        Login is null && Port is null && !SetJumpHost && !SetColor && !SetIcon && !SetTheme && Persistent is null
        && SessionAgent is null;

    /// <summary>The session with this edit applied; the same instance when nothing changes.</summary>
    public Session Apply(Session session)
    {
        var edited = session;
        if (Login is SessionLogin.Inherit or SessionLogin.Identity && session.Kind == SessionKind.Ssh
            && (Login == SessionLogin.Inherit || IdentityId is not null))
        {
            edited = edited with
            {
                InheritLogin = Login == SessionLogin.Inherit,
                IdentityId = Login == SessionLogin.Identity ? IdentityId : null,
                Username = KeepUsernames ? session.Username : "",
                AuthMethod = AuthMethod.Password,
                PrivateKeyId = null,
                PrivateKeyPath = null,
                PassphraseRequired = false,
            };
        }
        if (Port is { } port && !session.IsLocal && !session.IsSerial)
            edited = edited with { Port = Math.Clamp(port, 1, 65535) };
        if (SetJumpHost && !session.IsLocal && !session.IsSerial && JumpHostId != session.Id)
            edited = edited with { JumpHostId = JumpHostId };
        if (SetColor)
            edited = edited with { ColorTag = ColorTag };
        if (SetIcon)
            edited = edited with { Icon = Icon };
        if (SetTheme)
        {
            var overrides = (session.Overrides ?? new TerminalOverrides()) with { Theme = Theme };
            edited = edited with { Overrides = overrides.IsEmpty ? null : overrides };
        }
        if (Persistent is { } persistent && session.Kind == SessionKind.Ssh
            && !(persistent && session.ShellIntegration == ShellIntegrationMode.PowerShell))
        {
            edited = edited with { Persistent = persistent };
        }
        if (session.Kind == SessionKind.Ssh)
            edited = edited with { SessionAgent = (SessionAgent ?? edited.SessionAgent) && edited.Persistent };
        return edited == session ? session : edited;
    }

    /// <summary>The sessions this edit changes, already edited.</summary>
    public IReadOnlyList<Session> ApplyTo(IEnumerable<Session> sessions) =>
        sessions.Select(session => (Before: session, After: Apply(session)))
            .Where(pair => !ReferenceEquals(pair.Before, pair.After))
            .Select(pair => pair.After)
            .ToList();
}

/// <summary>Copies saved sessions under new ids and names that are free in their folder.</summary>
public static class SessionDuplicator
{
    /// <summary>Each source paired with its copy: "web-01 copy", then "web-01 copy 2".
    /// A copy of a built-in local profile is an ordinary profile.</summary>
    public static IReadOnlyList<(Session Source, Session Copy)> Duplicate(
        IEnumerable<Session> sources, IEnumerable<Session> existing)
    {
        static string Key(Session session) =>
            $"{session.FolderScope}\n{FolderPaths.Normalize(session.FolderPath)}\n{session.Name}";

        var taken = existing.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copies = new List<(Session, Session)>();
        foreach (var source in sources)
        {
            var copy = source with { Id = Guid.NewGuid(), BuiltIn = false, Name = $"{source.Name} copy" };
            for (var number = 2; !taken.Add(Key(copy)); number++)
                copy = copy with { Name = $"{source.Name} copy {number}" };
            copies.Add((source, copy));
        }
        return copies;
    }
}
