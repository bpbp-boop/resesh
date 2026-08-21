using Resesh.Core.Models;

namespace Resesh.Core.Search;

/// <summary>
/// Case-insensitive substring search over name, host, username, folder path, and notes.
/// </summary>
public static class SessionSearch
{
    public static bool Matches(Session session, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        // Every whitespace-separated term must match at least one field.
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            Contains(session.Name, term)
            || Contains(session.Host, term)
            || Contains(session.Username, term)
            || Contains(session.FolderPath, term)
            || Contains(session.Notes, term)
            || Contains(session.Local?.Executable, term));
    }

    public static IReadOnlyList<Session> Filter(IEnumerable<Session> sessions, string? query) =>
        sessions.Where(s => Matches(s, query)).ToList();

    /// <summary>
    /// Ranks matches for the search box's suggestion list: name matches first (prefix before
    /// substring), then host, then everything else; ties broken alphabetically by name.
    /// </summary>
    public static IReadOnlyList<Session> Rank(IEnumerable<Session> sessions, string? query)
    {
        var matches = Filter(sessions, query);
        if (string.IsNullOrWhiteSpace(query))
            return matches.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var q = query.Trim();
        return matches
            .OrderBy(s => Score(s, q))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Score(Session s, string q)
    {
        if (s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (Contains(s.Name, q)) return 1;
        if (Contains(s.Host, q)) return 2;
        return 3;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
