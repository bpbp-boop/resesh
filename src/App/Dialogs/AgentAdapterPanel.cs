using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Manual setup",
            Message = "Resesh does not install adapters or change any host. An adapter only reports status to its terminal; it cannot approve requests or send input.",
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose an adapter",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var index = 0;
        foreach (var snippet in AgentAdapters.All)
            panel.Children.Add(SnippetExpander(snippet, index++));

        panel.Children.Add(ProtocolReference());
        return panel;
    }

    /// <summary>One adapter as a compact card. Its destination stays visible, while the
    /// explanation and code stay behind the chevron. Copy remains available without
    /// expanding the card.</summary>
    private static UIElement SnippetExpander(AgentAdapterSnippet snippet, int index)
    {
        var header = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 8, 88, 8),
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
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.62,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                },
            },
        };

        var expander = new Expander
        {
            Header = header,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = snippet.Description,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.78,
                    },
                    CodeBlock(snippet.Text, wrap: false),
                },
            },
        };

        var copy = new Button
        {
            Content = "Copy",
            MinWidth = 72,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 42, 0),
        };
        AutomationProperties.SetAutomationId(expander, $"SettingsAgentAdapter_{index}");
        AutomationProperties.SetAutomationId(copy, $"SettingsAgentAdapterCopy_{index}");
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(snippet.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copy.Content = "Copied";
        };

        return RowCard(new Grid { Children = { expander, copy } });
    }

    private static UIElement ProtocolReference()
    {
        var expander = new Expander
        {
            Header = new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(0, 8, 0, 8),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Protocol reference",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = "For custom integrations",
                        Opacity = 0.62,
                        FontSize = 12,
                    },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = CodeBlock(AgentAdapters.SequenceReference, wrap: true),
        };
        AutomationProperties.SetAutomationId(expander, "SettingsAgentProtocolReference");
        return RowCard(expander);
    }

    private static Border RowCard(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(42, 128, 128, 128)),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(12, 128, 128, 128)),
        Child = child,
    };

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
