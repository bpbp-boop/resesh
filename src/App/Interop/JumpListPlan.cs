using Resesh.Core.Models;

namespace Resesh.App.Interop;

/// <summary>One jump list entry: a shortcut to this program with <see cref="Arguments"/>.</summary>
internal sealed record JumpListItem(string Title, string Arguments, string Description);

/// <summary>
/// The taskbar jump list: saved sessions pinned in the tab strip, then recently opened ones,
/// each launching <c>--session &lt;id&gt;</c>. Sessions that no longer exist are dropped, and a
/// session pinned in the tab strip is not repeated under Recent.
/// </summary>
internal sealed record JumpListPlan(IReadOnlyList<JumpListItem> Pinned, IReadOnlyList<JumpListItem> Recent)
{
    public const int RecentLimit = 8;

    public static JumpListPlan For(
        IReadOnlyList<Guid> pinnedIds,
        IReadOnlyList<Guid> recentIds,
        Func<Guid, Session?> find,
        Func<Session, string>? loginUsername = null)
    {
        var login = loginUsername ?? (session => session.Username);
        var pinned = Items(pinnedIds, find, login, int.MaxValue);
        var shown = pinned.Select(item => item.Arguments).ToHashSet();
        var recent = Items(recentIds, find, login, RecentLimit + shown.Count)
            .Where(item => !shown.Contains(item.Arguments))
            .Take(RecentLimit)
            .ToList();
        return new(pinned, recent);
    }

    /// <summary>The plan without items the user removed from the jump list.</summary>
    public JumpListPlan Without(IReadOnlySet<string> removedArguments) => removedArguments.Count == 0
        ? this
        : new(
            Pinned.Where(item => !removedArguments.Contains(item.Arguments)).ToList(),
            Recent.Where(item => !removedArguments.Contains(item.Arguments)).ToList());

    public static string ArgumentsFor(Guid sessionId) => $"{LaunchRequest.SessionOption} {sessionId:D}";

    private static List<JumpListItem> Items(
        IReadOnlyList<Guid> ids, Func<Guid, Session?> find, Func<Session, string> login, int limit) =>
        ids.Distinct()
            .Select(find)
            .OfType<Session>()
            .Take(limit)
            .Select(session =>
            {
                var description = Description(session, login);
                return new JumpListItem(Title(session, description), ArgumentsFor(session.Id), description);
            })
            .ToList();

    // The shell shows titles on one line and truncates them; keep them readable.
    private static string Title(Session session, string description) =>
        string.IsNullOrWhiteSpace(session.Name) ? description : session.Name.Trim();

    /// <summary>Where the session connects, as its tab shows it: the login it will use (an
    /// identity's or a folder's, not only its own) and any non-default port. The protocol
    /// is named, since a jump list item has no other cue that a target is telnet.</summary>
    private static string Description(Session session, Func<Session, string> login)
    {
        if (session.IsLocal)
            return "Local terminal";
        var endpoint = SessionEndpoint.Format(session, new()
        {
            Username = session.Kind == SessionKind.Ssh ? login(session) : null,
            ShowProtocol = true,
        });
        return string.IsNullOrEmpty(endpoint) ? session.Name : endpoint;
    }

    /// <summary>Plans are compared before rewriting the jump list, which the shell persists.</summary>
    public bool SameAs(JumpListPlan? other) =>
        other is not null && Pinned.SequenceEqual(other.Pinned) && Recent.SequenceEqual(other.Recent);
}
