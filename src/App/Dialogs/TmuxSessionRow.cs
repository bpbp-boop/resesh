using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Consistent session details in the connection picker and session manager.
/// The folder and program lead because they are what tells shells apart; the slot label
/// is only a stable name.</summary>
internal static class TmuxSessionRow
{
    public static ListViewItem Create(TmuxSessionInfo session, bool openInTab = false)
    {
        var label = SlotLabel(session.Slot);
        var state = openInTab ? "Open in a tab"
            : session.AttachedClients == 0 ? "Detached"
            : session.AttachedClients == 1 ? "Attached elsewhere"
            : $"Attached elsewhere ({session.AttachedClients})";
        var folder = string.IsNullOrWhiteSpace(session.CurrentPath) ? "Folder unavailable" : session.CurrentPath;
        var command = string.IsNullOrWhiteSpace(session.CurrentCommand) ? "program unavailable" : session.CurrentCommand;
        var age = Age(session.CreatedAt);

        var title = new TextBlock
        {
            Text = folder,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(title, folder);
        var detail = new TextBlock
        {
            Text = $"{command} · {label} · {age}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Someone else's live terminal gets the caution outline; everything else is neutral.
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["SessionInputBrush"],
            BorderBrush = (Brush)Application.Current.Resources[session.AttachedClients > 0 && !openInTab
                ? "SystemFillColorCautionBrush"
                : "SessionChromeFrameBrush"],
            Child = new TextBlock
            {
                Text = state,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            },
        };
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, Children = { title, detail } };
        grid.Children.Add(text);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        var row = new ListViewItem { Tag = session, Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, $"{folder}, {command}, {label}, {age}, {state}");
        return row;
    }

    /// <summary>The "start a new shell" entry, styled like a row so it reads as an option.</summary>
    public static ListViewItem CreateNew(object tag, string text)
    {
        var row = new ListViewItem
        {
            Tag = tag,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 8, 0, 8),
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new TextBlock { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                },
            },
        };
        AutomationProperties.SetName(row, text);
        return row;
    }

    /// <summary>
    /// Stock ContentDialog caps its width at 548, which leaves slightly less than 500 for
    /// content once padding and the scroll gutter are taken; a 500 minimum then clips on the
    /// right. Raise the cap instead, and shrink the minimum on small windows.
    /// </summary>
    public static void SizeDialog(ContentDialog dialog, FrameworkElement content, XamlRoot root)
    {
        const double contentWidth = 500;
        var available = Math.Max(280, root.Size.Width - 24);
        dialog.Resources["ContentDialogMaxWidth"] = Math.Min(contentWidth + 100, available);
        content.MinWidth = Math.Min(contentWidth, available - 100);
    }

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
