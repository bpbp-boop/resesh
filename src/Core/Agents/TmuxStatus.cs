namespace Resesh.Core.Agents;

/// <summary>
/// Program status inside a persistent (tmux) session. tmux discards OSC 7501 and the
/// adapters' own sequences, so nothing a program prints about its state reaches the app.
/// What does reach it is the title tmux sends, built from a format we set. An adapter
/// therefore stores its report in a pane option, the title format appends that option to
/// the title, and the app takes it off again before the title is shown or interpreted.
/// <para>
/// The option holds the body of an OSC 7501 report (<c>state=blocked:kind=permission:app=claude</c>),
/// so a persistent session shows the same states as a plain one. A hook needs no terminal
/// to set it, only tmux's own environment variables:
/// <c>tmux set -p -t "$TMUX_PANE" @resesh_status state=working:app=claude</c>.
/// Pane options need tmux 3.0; an older server reports nothing, as before.
/// </para>
/// </summary>
public static class TmuxStatus
{
    /// <summary>The pane option an adapter sets, and unsets when its program exits.</summary>
    public const string Option = "@resesh_status";

    private const string Open = " [[resesh-status:";
    private const string Close = "]]";

    /// <summary>The tmux format that appends the option to a title, or nothing when it is
    /// unset. No comma or closing brace may appear in it: both end a format branch.</summary>
    public const string TitleSuffixFormat = "#{?#{" + Option + "}," + Open + "#{" + Option + "}" + Close + ",}";

    /// <summary>
    /// Splits a title from a persistent session into the title itself and the report that
    /// rode along with it (null when there is none). The last marker wins: a program can
    /// put anything in its own title, and the genuine suffix is appended after it.
    /// </summary>
    public static string SplitTitle(string? title, out string? report)
    {
        report = null;
        if (string.IsNullOrEmpty(title))
            return "";
        var trimmed = title.TrimEnd();
        if (!trimmed.EndsWith(Close, StringComparison.Ordinal))
            return title;
        var open = trimmed.LastIndexOf(Open, StringComparison.Ordinal);
        if (open < 0)
            return title;
        report = trimmed[(open + Open.Length)..^Close.Length];
        return trimmed[..open];
    }
}
