using System.Text;

namespace Resesh.Core.History;

public enum CommandHistoryStatus
{
    Any,
    Failed,
    Succeeded,
}

/// <summary>What the history view asks for. <see cref="Text"/> is the search box as typed,
/// including any <c>host:</c>, <c>in:</c>, or <c>exit:</c> filters.</summary>
public sealed record CommandHistoryQuery
{
    public string Text { get; init; } = "";
    public Guid? SessionId { get; init; }
    public CommandHistoryStatus Status { get; init; }
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive end: only commands that started before it.</summary>
    public DateTimeOffset? Until { get; init; }
    public bool SearchOutput { get; init; } = true;
}

/// <summary>The search box split into free-text terms and filters.</summary>
public sealed record ParsedHistoryQuery
{
    /// <summary>Words and quoted phrases. Each one must appear somewhere in the entry.</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    /// <summary><c>host:</c> / <c>session:</c> values; each must match the session name or target.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary><c>in:</c> values; each must match the working directory.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    public CommandHistoryStatus Status { get; init; }

    /// <summary><c>exit:N</c> — an exact exit code.</summary>
    public int? ExitCode { get; init; }
}

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>One matching entry with what to emphasize: spans in the command line, how often
/// the terms occur in the output, and the first output line that matched.</summary>
public sealed record CommandHistoryHit(
    CommandHistoryEntry Entry,
    IReadOnlyList<TextSpan> CommandSpans,
    int OutputMatches,
    string? Snippet,
    IReadOnlyList<TextSpan> SnippetSpans);

public sealed record CommandHistoryResults(
    IReadOnlyList<CommandHistoryHit> Hits,
    int TotalMatches,
    IReadOnlyList<string> Terms);

/// <summary>
/// Query parsing and match highlighting for command history, plus the reference in-memory
/// search: <see cref="CommandHistoryStore.Search"/> must return what <see cref="Search"/> does.
/// Case-insensitive search over command history, newest first. Every term must appear in
/// the command, the session name or target, the working directory, or (optionally) the
/// output. Filters narrow before terms are matched.
/// </summary>
public static class CommandHistorySearch
{
    private const int SnippetLength = 160;
    private const int SnippetLead = 48;

    public static ParsedHistoryQuery Parse(string? text)
    {
        var terms = new List<string>();
        var hosts = new List<string>();
        var directories = new List<string>();
        var status = CommandHistoryStatus.Any;
        int? exitCode = null;

        foreach (var (token, quoted) in Tokenize(text ?? ""))
        {
            var colon = token.IndexOf(':');
            if (!quoted && colon > 0 && colon < token.Length - 1)
            {
                var key = token[..colon].ToLowerInvariant();
                var value = token[(colon + 1)..].Trim('"');
                switch (key)
                {
                    case "host" or "session":
                        hosts.Add(value);
                        continue;
                    case "in" or "dir" or "cwd":
                        directories.Add(value);
                        continue;
                    case "exit" or "status":
                        switch (value.ToLowerInvariant())
                        {
                            case "fail" or "failed" or "error" or "nonzero":
                                status = CommandHistoryStatus.Failed;
                                continue;
                            case "ok" or "success" or "succeeded" or "passed":
                                status = CommandHistoryStatus.Succeeded;
                                continue;
                        }
                        if (int.TryParse(value, out var code))
                        {
                            exitCode = code;
                            continue;
                        }
                        break;
                }
            }
            terms.Add(token);
        }

        return new ParsedHistoryQuery
        {
            Terms = terms,
            Hosts = hosts,
            Directories = directories,
            Status = status,
            ExitCode = exitCode,
        };
    }

    public static CommandHistoryResults Search(
        IReadOnlyList<CommandHistoryEntry> entries,
        CommandHistoryQuery query,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var parsed = Parse(query.Text);
        var status = parsed.Status != CommandHistoryStatus.Any ? parsed.Status : query.Status;
        var hits = new List<CommandHistoryHit>();
        var total = 0;

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if ((i & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];
            if (query.SessionId is { } sessionId && entry.SessionId != sessionId)
                continue;
            if (query.Since is { } since && entry.StartedAt < since)
                continue;
            if (query.Until is { } until && entry.StartedAt >= until)
                continue;
            if (status == CommandHistoryStatus.Failed && !entry.Failed)
                continue;
            if (status == CommandHistoryStatus.Succeeded && !entry.Succeeded)
                continue;
            if (parsed.ExitCode is { } exit && entry.ExitCode != exit)
                continue;
            if (!parsed.Hosts.All(host => Contains(entry.SessionName, host) || Contains(entry.Target, host)))
                continue;
            if (!parsed.Directories.All(directory => Contains(entry.WorkingDirectory, directory)))
                continue;
            if (!parsed.Terms.All(term =>
                    Contains(entry.Command, term)
                    || Contains(entry.SessionName, term)
                    || Contains(entry.Target, term)
                    || Contains(entry.WorkingDirectory, term)
                    || (query.SearchOutput && Contains(entry.Output, term))))
                continue;

            total++;
            if (hits.Count < limit)
                hits.Add(CreateHit(entry, parsed.Terms, query.SearchOutput));
        }

        return new CommandHistoryResults(hits, total, parsed.Terms);
    }

    /// <summary>Every occurrence of any term in <paramref name="text"/>, sorted, with
    /// overlapping or touching occurrences merged into one span.</summary>
    public static IReadOnlyList<TextSpan> FindAll(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0)
            return [];

        var spans = new List<TextSpan>();
        foreach (var term in terms)
        {
            if (term.Length == 0)
                continue;
            var index = 0;
            while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                spans.Add(new TextSpan(index, term.Length));
                index += term.Length;
            }
        }
        if (spans.Count < 2)
            return spans;

        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var merged = new List<TextSpan> { spans[0] };
        for (var i = 1; i < spans.Count; i++)
        {
            var last = merged[^1];
            if (spans[i].Start <= last.End)
            {
                if (spans[i].End > last.End)
                    merged[^1] = new TextSpan(last.Start, spans[i].End - last.Start);
            }
            else
            {
                merged.Add(spans[i]);
            }
        }
        return merged;
    }

    /// <summary>What to emphasize for one matching entry: command spans, output match count, snippet.</summary>
    internal static CommandHistoryHit CreateHit(CommandHistoryEntry entry, IReadOnlyList<string> terms, bool searchOutput)
    {
        var commandSpans = FindAll(entry.Command, terms);
        if (!searchOutput || terms.Count == 0 || entry.Output.Length == 0)
            return new CommandHistoryHit(entry, commandSpans, 0, null, []);

        var outputSpans = FindAll(entry.Output, terms);
        if (outputSpans.Count == 0)
            return new CommandHistoryHit(entry, commandSpans, 0, null, []);

        var (snippet, snippetSpans) = Snippet(entry.Output, outputSpans[0].Start, terms);
        return new CommandHistoryHit(entry, commandSpans, outputSpans.Count, snippet, snippetSpans);
    }

    /// <summary>The output line holding the first match, cut to a window around it.</summary>
    private static (string Text, IReadOnlyList<TextSpan> Spans) Snippet(string output, int matchStart, IReadOnlyList<string> terms)
    {
        var lineStart = output.LastIndexOf('\n', Math.Max(0, matchStart - 1)) + 1;
        if (matchStart == 0)
            lineStart = 0;
        var lineEnd = output.IndexOf('\n', matchStart);
        if (lineEnd < 0)
            lineEnd = output.Length;
        var line = output[lineStart..lineEnd].TrimEnd('\r');
        var offset = matchStart - lineStart;

        var start = 0;
        if (line.Length > SnippetLength && offset > SnippetLead)
            start = Math.Min(offset - SnippetLead, line.Length - SnippetLength);
        var length = Math.Min(SnippetLength, line.Length - start);
        var text = new StringBuilder();
        if (start > 0)
            text.Append('…');
        text.Append(line.AsSpan(start, length).TrimStart());
        if (start + length < line.Length)
            text.Append('…');

        var snippet = text.ToString();
        return (snippet, FindAll(snippet, terms));
    }

    private static IEnumerable<(string Token, bool Quoted)> Tokenize(string text)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                if (inQuotes)
                {
                    inQuotes = false;
                    if (current.Length > 0)
                    {
                        yield return (current.ToString(), quoted);
                        current.Clear();
                    }
                    quoted = false;
                }
                else if (current.Length == 0)
                {
                    inQuotes = true;
                    quoted = true;
                }
                else
                {
                    // host:"web 01" — the quotes belong to the filter value.
                    inQuotes = true;
                }
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return (current.ToString(), quoted);
                    current.Clear();
                }
                quoted = false;
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            yield return (current.ToString(), quoted);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
