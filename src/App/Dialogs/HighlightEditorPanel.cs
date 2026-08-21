using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Models;

namespace Resesh.App.Dialogs;

/// <summary>
/// Global keyword-highlighting editor, hosted inline in the Settings dialog's Highlighting
/// tab: per-rule enable toggles, CRUD (with live regex preview) for custom rules, and the
/// same editing for built-in rules — stored as overrides with a per-rule "Reset to default"
/// path back to the shipped definition. Changes are persisted to the highlights store
/// immediately and pushed live to open terminals via <c>onChanged</c>; there is no cancel —
/// same model as the tab toggles. Add/Edit swaps the list for the rule form in place, and
/// the standing preview renders an editable sample against every enabled rule.
/// </summary>
public static class HighlightEditorPanel
{
    private const string DefaultSample =
        "GigabitEthernet0/0/1 is up, eth0 is down — 10.0.0.1/24 fe80::1 00:1a:2b:3c:4d:5e ospf uptime 1w2d";

    public static UIElement Create(Action onChanged)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
        };

        var addButton = new Button { Content = "Add custom rule" };
        var editButton = new Button { Content = "Edit", IsEnabled = false };
        var deleteButton = new Button { Content = "Delete", IsEnabled = false };
        var listButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { addButton, editButton, deleteButton },
        };

        // Standing preview: an editable sample line with every enabled rule applied,
        // matching the terminal's overlap resolution (later rule wins). Paste a line from
        // your own terminal to see how the current rules treat it.
        var listSample = new TextBox
        {
            Header = "Preview sample",
            Text = DefaultSample,
            AcceptsReturn = false,
        };
        var combinedPreview = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        var combinedPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                listSample,
                new Border
                {
                    Padding = new Thickness(12, 10, 12, 10),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 128, 128, 128)),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 128, 128, 128)),
                    Child = combinedPreview,
                },
            },
        };
        // The list takes the star row so it expands to whatever height the host grants the
        // panel; the buttons and the preview section stay pinned below it.
        var listPanel = new Grid { RowSpacing = 10 };
        listPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(list, 0);
        Grid.SetRow(listButtons, 1);
        Grid.SetRow(combinedPanel, 2);
        listPanel.Children.Add(list);
        listPanel.Children.Add(listButtons);
        listPanel.Children.Add(combinedPanel);

        // ---- custom-rule form (swapped in place of the list) ----

        var nameBox = new TextBox { Header = "Name", PlaceholderText = "e.g. Customer VRF names" };
        var patternBox = new TextBox
        {
            Header = "Regular expression (applied per line)",
            PlaceholderText = "\\bVRF-[A-Z]+\\b",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        var colorBox = new TextBox { Header = "Color (#RRGGBB)", Text = "#e5c07b", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var swatch = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 6),
        };
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { colorBox, swatch } };
        var boldCheck = new CheckBox { Content = "Bold (renders as a background tint)" };
        var underlineCheck = new CheckBox { Content = "Underline" };
        var matchCaseCheck = new CheckBox { Content = "Match case" };
        var overviewCheck = new CheckBox { Content = "Mark hits in the scrollbar overview" };
        var sampleBox = new TextBox { Header = "Preview sample", Text = DefaultSample, AcceptsReturn = false };
        var preview = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var formStatus = new TextBlock { Foreground = new SolidColorBrush(Colors.IndianRed), TextWrapping = TextWrapping.Wrap };
        var saveButton = new Button { Content = "Save rule", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancelButton = new Button { Content = "Cancel" };
        var resetButton = new Button { Content = "Reset to default", Margin = new Thickness(16, 0, 0, 0) };
        var formButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { saveButton, cancelButton, resetButton },
        };
        var formPanel = new StackPanel
        {
            Spacing = 10,
            Children = { nameBox, patternBox, colorRow, boldCheck, underlineCheck, matchCaseCheck, overviewCheck, sampleBox, preview, formStatus, formButtons },
        };
        // The form keeps its natural height inside its own scroller, so a long wrapped
        // preview or error text can never clip against the panel's fixed height.
        var formHost = new ScrollViewer
        {
            Content = formPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid { Children = { listPanel, formHost } };

        HighlightRule? editing = null; // null = adding a new custom rule

        // ---- list plumbing ----

        void RefreshCombinedPreview()
        {
            combinedPreview.Inlines.Clear();
            var sample = listSample.Text;
            var rules = App.Highlights.AllRules.Where(r => r.Enabled).ToList();
            var winner = new int[sample.Length];
            Array.Fill(winner, -1);
            for (var r = 0; r < rules.Count; r++)
            {
                try
                {
                    var options = rules[r].MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(rules[r].Pattern, options, TimeSpan.FromMilliseconds(200));
                    foreach (Match m in regex.Matches(sample))
                        for (var c = m.Index; c < m.Index + m.Length; c++)
                            winner[c] = r;
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    // A broken stored pattern just doesn't paint the preview.
                }
            }

            var start = 0;
            for (var i = 1; i <= sample.Length; i++)
            {
                if (i < sample.Length && winner[i] == winner[start])
                    continue;
                var run = new Run { Text = sample[start..i] };
                if (winner[start] >= 0)
                {
                    var rule = rules[winner[start]];
                    run.Foreground = new SolidColorBrush(TryParseColor(rule.Color) ?? Colors.White);
                    if (rule.Bold)
                        run.FontWeight = FontWeights.Bold;
                    if (rule.Underline)
                        run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                }
                combinedPreview.Inlines.Add(run);
                start = i;
            }
        }

        void Changed()
        {
            RefreshCombinedPreview();
            onChanged();
        }

        void RefreshList()
        {
            list.Items.Clear();
            foreach (var rule in App.Highlights.AllRules)
            {
                var check = new CheckBox
                {
                    IsChecked = rule.Enabled,
                    MinWidth = 32,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var id = rule.Id;
                check.Checked += (_, _) => { App.Highlights.SetEnabled(id, true); Changed(); };
                check.Unchecked += (_, _) => { App.Highlights.SetEnabled(id, false); Changed(); };

                var color = TryParseColor(rule.Color) ?? Colors.White;
                var packTag = rule.IsBuiltin
                    ? App.Highlights.IsOverridden(rule.Id) ? rule.Pack + " · edited" : rule.Pack
                    : "custom";
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Tag = rule,
                    Children =
                    {
                        check,
                        new Border
                        {
                            Width = 14, Height = 14, CornerRadius = new CornerRadius(2),
                            Background = new SolidColorBrush(color),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = rule.Name,
                            MinWidth = 190,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontWeight = rule.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                        },
                        new TextBlock
                        {
                            Text = packTag,
                            Opacity = 0.6,
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                };
                list.Items.Add(row);
            }
        }

        HighlightRule? SelectedRule() => (list.SelectedItem as FrameworkElement)?.Tag as HighlightRule;

        list.SelectionChanged += (_, _) =>
        {
            editButton.IsEnabled = SelectedRule() is not null;
            deleteButton.IsEnabled = SelectedRule() is { IsBuiltin: false };
        };

        // ---- form plumbing ----

        void ShowForm(HighlightRule? existing)
        {
            editing = existing;
            nameBox.Text = existing?.Name ?? "";
            patternBox.Text = existing?.Pattern ?? "";
            colorBox.Text = existing?.Color ?? "#e5c07b";
            boldCheck.IsChecked = existing?.Bold ?? false;
            underlineCheck.IsChecked = existing?.Underline ?? false;
            matchCaseCheck.IsChecked = existing?.MatchCase ?? false;
            overviewCheck.IsChecked = existing?.ShowInOverview ?? false;
            sampleBox.Text = listSample.Text;
            resetButton.Visibility = existing is { IsBuiltin: true } ? Visibility.Visible : Visibility.Collapsed;
            resetButton.IsEnabled = existing is not null && App.Highlights.IsOverridden(existing.Id);
            formStatus.Text = "";
            listPanel.Visibility = Visibility.Collapsed;
            formHost.Visibility = Visibility.Visible;
            formHost.ChangeView(null, 0, null, disableAnimation: true);
            UpdatePreview();
        }

        void HideForm()
        {
            formHost.Visibility = Visibility.Collapsed;
            listPanel.Visibility = Visibility.Visible;
        }

        void UpdatePreview()
        {
            var color = TryParseColor(colorBox.Text.Trim()) ?? Colors.White;
            swatch.Background = new SolidColorBrush(color);
            preview.Inlines.Clear();

            var pattern = patternBox.Text;
            var sample = sampleBox.Text;
            if (pattern.Length == 0)
            {
                preview.Inlines.Add(new Run { Text = sample });
                formStatus.Text = "";
                return;
            }

            try
            {
                var options = matchCaseCheck.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
                var regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(200));
                var index = 0;
                var matched = 0;
                foreach (Match m in regex.Matches(sample))
                {
                    if (m.Length == 0)
                        continue;
                    if (m.Index > index)
                        preview.Inlines.Add(new Run { Text = sample[index..m.Index] });
                    var run = new Run
                    {
                        Text = m.Value,
                        Foreground = new SolidColorBrush(color),
                        FontWeight = boldCheck.IsChecked == true ? FontWeights.Bold : FontWeights.Normal,
                    };
                    if (underlineCheck.IsChecked == true)
                        run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                    preview.Inlines.Add(run);
                    index = m.Index + m.Length;
                    matched++;
                }
                if (index < sample.Length)
                    preview.Inlines.Add(new Run { Text = sample[index..] });
                formStatus.Text = matched == 0 ? "No matches in the sample." : "";
                formStatus.Foreground = new SolidColorBrush(matched == 0 ? Colors.Gray : Colors.IndianRed);
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                preview.Inlines.Add(new Run { Text = sample });
                formStatus.Foreground = new SolidColorBrush(Colors.IndianRed);
                formStatus.Text = ex is RegexMatchTimeoutException
                    ? "Pattern is too slow (timed out on the sample)."
                    : $"Invalid regex: {ex.Message}";
            }
        }

        patternBox.TextChanged += (_, _) => UpdatePreview();
        // One logical sample: the form's box and the list's box mirror each other (the two
        // are never visible at once), so a pasted line survives the form round-trip.
        sampleBox.TextChanged += (_, _) => { UpdatePreview(); listSample.Text = sampleBox.Text; };
        listSample.TextChanged += (_, _) => RefreshCombinedPreview();
        colorBox.TextChanged += (_, _) => UpdatePreview();
        boldCheck.Click += (_, _) => UpdatePreview();
        underlineCheck.Click += (_, _) => UpdatePreview();
        matchCaseCheck.Click += (_, _) => UpdatePreview();

        addButton.Click += (_, _) => ShowForm(null);
        editButton.Click += (_, _) =>
        {
            if (SelectedRule() is { } rule)
                ShowForm(rule);
        };
        deleteButton.Click += (_, _) =>
        {
            if (SelectedRule() is { IsBuiltin: false } rule)
            {
                App.Highlights.RemoveCustom(rule.Id);
                RefreshList();
                Changed();
            }
        };
        cancelButton.Click += (_, _) => HideForm();
        resetButton.Click += (_, _) =>
        {
            if (editing is { IsBuiltin: true } rule && App.Highlights.ResetBuiltin(rule.Id))
            {
                HideForm();
                RefreshList();
                Changed();
            }
        };
        saveButton.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var pattern = patternBox.Text.Trim();
            var color = colorBox.Text.Trim();
            if (name.Length == 0 || pattern.Length == 0)
            {
                formStatus.Foreground = new SolidColorBrush(Colors.IndianRed);
                formStatus.Text = "Name and pattern are required.";
                return;
            }
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                formStatus.Foreground = new SolidColorBrush(Colors.IndianRed);
                formStatus.Text = $"Invalid regex: {ex.Message}";
                return;
            }
            if (TryParseColor(color) is null)
            {
                formStatus.Foreground = new SolidColorBrush(Colors.IndianRed);
                formStatus.Text = "Color must be #RRGGBB.";
                return;
            }

            if (editing is { IsBuiltin: true } builtin)
            {
                App.Highlights.SaveBuiltinOverride(builtin with
                {
                    Name = name,
                    Pattern = pattern,
                    Color = color.ToLowerInvariant(),
                    Bold = boldCheck.IsChecked == true,
                    Underline = underlineCheck.IsChecked == true,
                    MatchCase = matchCaseCheck.IsChecked == true,
                    ShowInOverview = overviewCheck.IsChecked == true,
                });
            }
            else
            {
                App.Highlights.SaveCustom(new HighlightRule
                {
                    Id = editing?.Id ?? $"custom-{Guid.NewGuid():N}"[..15],
                    Name = name,
                    Pattern = pattern,
                    Color = color.ToLowerInvariant(),
                    Bold = boldCheck.IsChecked == true,
                    Underline = underlineCheck.IsChecked == true,
                    MatchCase = matchCaseCheck.IsChecked == true,
                    ShowInOverview = overviewCheck.IsChecked == true,
                    Enabled = true,
                });
            }
            HideForm();
            RefreshList();
            Changed();
        };

        RefreshList();
        RefreshCombinedPreview();
        return root;
    }

    private static Windows.UI.Color? TryParseColor(string text)
    {
        if (!Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$"))
            return null;
        return Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(text.Substring(1, 2), 16),
            Convert.ToByte(text.Substring(3, 2), 16),
            Convert.ToByte(text.Substring(5, 2), 16));
    }
}
