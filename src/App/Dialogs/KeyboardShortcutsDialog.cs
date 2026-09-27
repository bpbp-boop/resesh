using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Input;

namespace Resesh.App.Dialogs;

/// <summary>Read-only reference of every shortcut in <see cref="KeyBindings"/>, grouped and searchable.</summary>
internal static class KeyboardShortcutsDialog
{
    private const double PreferredWidth = 640;
    private const double PreferredHeight = 600;
    private const double HorizontalChrome = 72;
    private const double VerticalChrome = 180;

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var search = new TextBox
        {
            PlaceholderText = "Search shortcuts",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(search, "Search shortcuts");
        AutomationProperties.SetAutomationId(search, "KeyboardShortcutsSearch");

        var intro = new TextBlock
        {
            Text = "Terminal shortcuts work while a terminal has focus. All others work anywhere in "
                + "the window, including the terminal. Ctrl with a single letter always goes to the shell.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("SessionTreeMutedForegroundBrush"),
        };

        var sections = new StackPanel { Spacing = 20, Padding = new Thickness(0, 0, 16, 0) };
        var rows = new List<(KeyBinding Binding, FrameworkElement Row, StackPanel Section)>();
        foreach (var category in KeyBindings.Categories)
        {
            var section = new StackPanel { Spacing = 2 };
            var header = new TextBlock
            {
                Text = category,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, 0, 0, 6),
            };
            AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
            section.Children.Add(header);
            foreach (var binding in KeyBindings.All.Where(binding => binding.Category == category))
            {
                var row = Row(binding);
                section.Children.Add(row);
                rows.Add((binding, row, section));
            }
            sections.Children.Add(section);
        }

        var empty = new TextBlock
        {
            Text = "No matching shortcuts",
            Visibility = Visibility.Collapsed,
            Foreground = Brush("SessionTreeMutedForegroundBrush"),
        };
        sections.Children.Add(empty);

        search.TextChanged += (_, _) =>
        {
            var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (binding, row, _) in rows)
                row.Visibility = Matches(binding, terms) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var section in rows.Select(entry => entry.Section).Distinct())
            {
                section.Visibility = rows.Any(entry => entry.Section == section && entry.Row.Visibility == Visibility.Visible)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            empty.Visibility = rows.Any(entry => entry.Row.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;
        };

        var scroller = new ScrollViewer
        {
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var content = new Grid { RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(intro);
        Grid.SetRow(search, 1);
        content.Children.Add(search);
        Grid.SetRow(scroller, 2);
        content.Children.Add(scroller);

        var dialog = new ContentDialog
        {
            Title = "Keyboard Shortcuts",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            Background = Brush("SessionShellBrush"),
            BorderBrush = Brush("SettingsCardBorderBrush"),
            Foreground = Brush("SessionTreeForegroundBrush"),
        };
        AutomationProperties.SetAutomationId(dialog, "KeyboardShortcutsDialog");

        void UpdateLayout()
        {
            var width = Math.Min(PreferredWidth, Math.Max(240, xamlRoot.Size.Width - HorizontalChrome));
            var height = Math.Min(PreferredHeight, Math.Max(180, xamlRoot.Size.Height - VerticalChrome));
            content.Width = width;
            content.Height = height;
            dialog.Resources["ContentDialogMaxWidth"] = width + 48;
            dialog.Resources["ContentDialogMaxHeight"] = Math.Max(280, xamlRoot.Size.Height - 24);
        }

        void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateLayout();
        UpdateLayout();
        xamlRoot.Changed += XamlRootChanged;
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        try
        {
            await dialog.ShowModalAsync();
        }
        finally
        {
            xamlRoot.Changed -= XamlRootChanged;
        }
    }

    /// <summary>Every search term must appear in the title, note, category or key names.</summary>
    internal static bool Matches(KeyBinding binding, IReadOnlyList<string> terms)
    {
        var keys = string.Join(" ", binding.Chords.Select(AppShortcuts.Label));
        return terms.All(term =>
            binding.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || binding.Note.Contains(term, StringComparison.OrdinalIgnoreCase)
            || binding.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
            || keys.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static Grid Row(KeyBinding binding)
    {
        var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = binding.Title, TextWrapping = TextWrapping.Wrap });
        if (binding.Note.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = binding.Note,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("SessionTreeMutedForegroundBrush"),
            });
        }
        row.Children.Add(text);

        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // A long run (Ctrl+1 to Ctrl+8) shows its ends; the rest list every chord.
        var chords = binding.Chords.Count > 2
            ? new[] { binding.Chords[0], binding.Chords[^1] }
            : binding.Chords.ToArray();
        var separator = binding.Chords.Count > 2 ? "to" : "or";
        for (var i = 0; i < chords.Length; i++)
        {
            if (i > 0)
                keys.Children.Add(Muted(separator));
            keys.Children.Add(Chord(chords[i]));
        }
        Grid.SetColumn(keys, 1);
        row.Children.Add(keys);

        var spoken = binding.Chords.Count > 2
            ? $"{AppShortcuts.Label(chords[0])} to {AppShortcuts.Label(chords[1])}"
            : string.Join(" or ", chords.Select(AppShortcuts.Label));
        AutomationProperties.SetName(row, $"{binding.Title}: {spoken}");
        AutomationProperties.SetAutomationId(row, "Shortcut_" + binding.Id);
        return row;
    }

    private static StackPanel Chord(KeyChord chord)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var part in AppShortcuts.Parts(chord))
        {
            panel.Children.Add(new Border
            {
                MinWidth = 26,
                Padding = new Thickness(6, 1, 6, 2),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1, 1, 1, 2),
                BorderBrush = Brush("SettingsCardBorderBrush"),
                Background = Brush("SessionInputBrush"),
                Child = new TextBlock
                {
                    Text = part,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            });
        }
        return panel;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("SessionTreeMutedForegroundBrush"),
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
