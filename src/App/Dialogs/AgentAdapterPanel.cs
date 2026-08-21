using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Agents;

namespace Resesh.App.Dialogs;

/// <summary>
/// The opt-in adapter snippets (Phase 6.2), hosted inline in the Settings dialog's Agents
/// tab. Resesh deliberately installs nothing: the exact text is shown here, the user
/// copies it to a target they choose, and removing it is deleting the lines again. An
/// adapter's only power is to emit one escape sequence describing what the agent is doing —
/// it can never send input or approve anything.
/// </summary>
public static class AgentAdapterPanel
{
    public static UIElement Create()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Resesh detects agents on its own — from the command you run, the terminal "
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

        var first = true;
        foreach (var snippet in AgentAdapters.All)
        {
            panel.Children.Add(SnippetExpander(snippet, first));
            first = false;
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Escape sequence",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(CodeBlock(AgentAdapters.SequenceReference, wrap: true));
        return panel;
    }

    /// <summary>One adapter as a collapsible row: title + target always visible, the
    /// description and snippet text behind the chevron. Copy works without expanding; the
    /// button floats over the header because an Expander header does not reliably stretch.</summary>
    private static UIElement SnippetExpander(AgentAdapterSnippet snippet, bool expanded)
    {
        var header = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 10, 0, 10),
            Children =
            {
                new TextBlock
                {
                    Text = snippet.Title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = snippet.Target,
                    Opacity = 0.65,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                },
            },
        };

        var expander = new Expander
        {
            Header = header,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = snippet.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                    CodeBlock(snippet.Text, wrap: false),
                },
            },
        };

        var copy = new Button
        {
            Content = "Copy",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 48, 0), // clear of the expand chevron
        };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(snippet.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copy.Content = "Copied";
        };

        return new Grid { Children = { expander, copy } };
    }

    /// <summary>Read-only code block: a selectable TextBlock, not a TextBox — a TextBox
    /// measures programmatically-set multi-line text as a single line here.</summary>
    private static UIElement CodeBlock(string text, bool wrap)
    {
        var block = new TextBlock
        {
            Text = text,
            IsTextSelectionEnabled = true,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        return new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 128, 128, 128)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 128, 128, 128)),
            Child = wrap
                ? block
                : new ScrollViewer
                {
                    Content = block,
                    MaxHeight = 190,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
        };
    }
}
