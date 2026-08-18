using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sessions.Core.Agents;

namespace Sessions.App.Dialogs;

/// <summary>
/// Shows the opt-in adapter snippets (Phase 6.2). Sessions deliberately installs nothing:
/// the exact text is shown here, the user copies it to a target they choose, and removing
/// it is deleting the lines again. An adapter's only power is to emit one escape sequence
/// describing what the agent is doing — it can never send input or approve anything.
/// </summary>
public static class AgentAdapterDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Sessions detects agents on its own — from the command you run, the terminal "
                 + "title, and (for local tabs) the processes in the tab's own job. Detection can "
                 + "say which agent is running, but only the agent itself can say it is waiting for "
                 + "you. Installing one of these snippets on a target upgrades its tabs from a guess "
                 + "to reported idle / working / needs-approval / complete states. The Codex adapter "
                 + "uses Codex's lifecycle hooks; review and trust it from Codex with /hooks.",
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Text = "Nothing here is installed for you, on this machine or any remote host. "
                 + "Copy what you want, where you want it; delete the lines to remove it. The hooks "
                 + "only report status to the terminal. They never approve a request or send input.",
        });

        foreach (var snippet in AgentAdapters.All)
            panel.Children.Add(SnippetCard(snippet));

        panel.Children.Add(new TextBlock
        {
            Text = "Escape sequence",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBox
        {
            Text = AgentAdapters.SequenceReference,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        });

        var dialog = new ContentDialog
        {
            Title = "Agent adapters",
            Content = new ScrollViewer { MaxHeight = 560, Content = panel },
            CloseButtonText = "Close",
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static UIElement SnippetCard(AgentAdapterSnippet snippet)
    {
        var text = new TextBox
        {
            Text = snippet.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 190,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(snippet.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copy.Content = "Copied";
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new TextBlock
        {
            Text = snippet.Title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(copy);

        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(header);
        card.Children.Add(new TextBlock { Text = snippet.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        card.Children.Add(new TextBlock
        {
            Text = snippet.Target,
            Opacity = 0.65,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        });
        card.Children.Add(text);
        return card;
    }
}
