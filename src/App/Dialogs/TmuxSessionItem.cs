using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>One row of the persistent-shell lists (connection picker and session
/// manager): a running shell, or the "start a new shell" option. The folder and program
/// lead because they are what tells shells apart; the slot label is only a stable name.</summary>
public sealed class TmuxSessionItem
{
    private TmuxSessionItem() { }

    /// <summary>The running shell, or null for the "start new" row.</summary>
    public TmuxSessionInfo? Session { get; private init; }

    public bool IsNew => Session is null;
    public bool IsSession => Session is not null;

    public string Folder { get; private init; } = "";
    public string Detail { get; private init; } = "";
    public string State { get; private init; } = "";

    /// <summary>Someone else's live terminal gets the caution outline.</summary>
    public bool IsAttachedElsewhere { get; private init; }

    public string AutomationName { get; private init; } = "";

    // ListViewItem takes its accessible name from the item's text.
    public override string ToString() => AutomationName;

    public static TmuxSessionItem ForSession(TmuxSessionInfo session, bool openInTab = false)
    {
        var label = SlotLabel(session.Slot);
        var state = openInTab ? "Open in a tab"
            : session.AttachedClients == 0 ? "Detached"
            : session.AttachedClients == 1 ? "Attached elsewhere"
            : $"Attached elsewhere ({session.AttachedClients})";
        var folder = string.IsNullOrWhiteSpace(session.CurrentPath) ? "Folder unavailable" : session.CurrentPath;
        var command = string.IsNullOrWhiteSpace(session.CurrentCommand) ? "program unavailable" : session.CurrentCommand;
        var age = Age(session.CreatedAt);
        return new TmuxSessionItem
        {
            Session = session,
            Folder = folder,
            Detail = $"{command} · {label} · {age}",
            State = state,
            IsAttachedElsewhere = session.AttachedClients > 0 && !openInTab,
            AutomationName = $"{folder}, {command}, {label}, {age}, {state}",
        };
    }

    /// <summary>The "start a new shell" entry, styled like a row so it reads as an option.</summary>
    public static TmuxSessionItem StartNew(string text) => new() { Folder = text, AutomationName = text };

    public static string SlotLabel(int slot) => slot == 0 ? "Primary" : $"Session {slot + 1}";

    private static string Age(DateTimeOffset? created)
    {
        if (created is null) return "age unavailable";
        var elapsed = DateTimeOffset.UtcNow - created.Value;
        if (elapsed < TimeSpan.FromMinutes(1)) return "started just now";
        return elapsed.TotalDays >= 1 ? $"started {elapsed.Days}d {elapsed.Hours}h ago"
            : elapsed.TotalHours >= 1 ? $"started {elapsed.Hours}h {elapsed.Minutes}m ago"
            : $"started {(int)elapsed.TotalMinutes}m ago";
    }
}
