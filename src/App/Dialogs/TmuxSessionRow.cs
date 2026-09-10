using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Consistent session details in the connection picker and session manager.</summary>
internal static class TmuxSessionRow
{
    public static ListViewItem Create(TmuxSessionInfo session)
    {
        var label = session.Slot == 0 ? "Primary" : $"Session {session.Slot + 1}";
        var attachment = session.AttachedClients == 0 ? "Detached" : $"Attached: {session.AttachedClients} client(s)";
        var row = new ListViewItem
        {
            Tag = session,
            Content = new StackPanel
            {
                Spacing = 4, Margin = new Thickness(0, 8, 0, 8),
                Children =
                {
                    new TextBlock { Text = $"{label} · {Age(session.CreatedAt)} · {attachment}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Command: {(string.IsNullOrWhiteSpace(session.CurrentCommand) ? "unavailable" : session.CurrentCommand)}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Folder: {session.CurrentPath}", TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        AutomationProperties.SetName(row, $"{label}, {Age(session.CreatedAt)}, {attachment}, {session.CurrentCommand}, {session.CurrentPath}");
        return row;
    }

    private static string Age(DateTimeOffset? created)
    {
        if (created is null) return "Age unavailable";
        var elapsed = DateTimeOffset.UtcNow - created.Value;
        if (elapsed < TimeSpan.Zero) return "Started just now";
        return elapsed.TotalDays >= 1 ? $"Running {elapsed.Days}d {elapsed.Hours}h"
            : elapsed.TotalHours >= 1 ? $"Running {elapsed.Hours}h {elapsed.Minutes}m"
            : $"Running {(int)elapsed.TotalMinutes}m";
    }
}
