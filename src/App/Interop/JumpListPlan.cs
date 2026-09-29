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
        Func<Guid, Session?> find)
    {
        var pinned = Items(pinnedIds, find, int.MaxValue);
        var shown = pinned.Select(item => item.Arguments).ToHashSet();
        var recent = Items(recentIds, find, RecentLimit + shown.Count)
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

    private static List<JumpListItem> Items(IReadOnlyList<Guid> ids, Func<Guid, Session?> find, int limit) =>
        ids.Distinct()
            .Select(find)
            .OfType<Session>()
            .Take(limit)
            .Select(session => new JumpListItem(Title(session), ArgumentsFor(session.Id), Description(session)))
            .ToList();

    // The shell shows titles on one line and truncates them; keep them readable.
    private static string Title(Session session) =>
        string.IsNullOrWhiteSpace(session.Name) ? Description(session) : session.Name.Trim();

    private static string Description(Session session) => session switch
    {
        { IsLocal: true } => "Local terminal",
        { Host: "" } => session.Name,
        { Username: "" } => session.Host,
        _ => $"{session.Username}@{session.Host}",
    };

    /// <summary>Plans are compared before rewriting the jump list, which the shell persists.</summary>
    public bool SameAs(JumpListPlan? other) =>
        other is not null && Pinned.SequenceEqual(other.Pinned) && Recent.SequenceEqual(other.Recent);
}
