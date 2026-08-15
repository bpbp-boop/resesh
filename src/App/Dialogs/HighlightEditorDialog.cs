using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Sessions.Core.Models;

namespace Sessions.App.Dialogs;

/// <summary>
/// Global keyword-highlighting editor: per-rule enable toggles for the built-in packs
/// and CRUD (with live regex preview) for custom rules. Changes are persisted to the
/// highlights store immediately and pushed live to open terminals via
/// <paramref name="onChanged"/>; there is no cancel — same model as the tab toggles.
/// </summary>
public static class HighlightEditorDialog
{
    private const string DefaultSample =
        "GigabitEthernet0/0/1 is up, eth0 is down — 10.0.0.1/24 fe80::1 00:1a:2b:3c:4d:5e ospf uptime 1w2d";

    public static async Task ShowAsync(XamlRoot xamlRoot, Action onChanged)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 300,
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
        var listPanel = new StackPanel { Spacing = 10, Children = { list, listButtons } };

        // ---- custom-rule form (swapped in place of the list; nested dialogs are not allowed) ----

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
        var formButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { saveButton, cancelButton },
        };
        var formPanel = new StackPanel
        {
            Spacing = 10,
            Visibility = Visibility.Collapsed,
            Children = { nameBox, patternBox, colorRow, boldCheck, underlineCheck, matchCaseCheck, sampleBox, preview, formStatus, formButtons },
        };

        var dialog = new ContentDialog
        {
            Title = "Keyword Highlighting",
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new StackPanel { MinWidth = 460, Children = { listPanel, formPanel } },
            },
            CloseButtonText = "Done",
            XamlRoot = xamlRoot,
        };

        string? editingId = null; // null = adding

        // ---- list plumbing ----

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
                check.Checked += (_, _) => { App.Highlights.SetEnabled(id, true); onChanged(); };
                check.Unchecked += (_, _) => { App.Highlights.SetEnabled(id, false); onChanged(); };

                var color = TryParseColor(rule.Color) ?? Colors.White;
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
                            Text = rule.IsBuiltin ? rule.Pack : "custom",
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
            var custom = SelectedRule() is { IsBuiltin: false };
            editButton.IsEnabled = custom;
            deleteButton.IsEnabled = custom;
        };

        // ---- form plumbing ----

        void ShowForm(HighlightRule? existing)
        {
            editingId = existing?.Id;
            nameBox.Text = existing?.Name ?? "";
            patternBox.Text = existing?.Pattern ?? "";
            colorBox.Text = existing?.Color ?? "#e5c07b";
            boldCheck.IsChecked = existing?.Bold ?? false;
            underlineCheck.IsChecked = existing?.Underline ?? false;
            matchCaseCheck.IsChecked = existing?.MatchCase ?? false;
            formStatus.Text = "";
            listPanel.Visibility = Visibility.Collapsed;
            formPanel.Visibility = Visibility.Visible;
            UpdatePreview();
        }

        void HideForm()
        {
            formPanel.Visibility = Visibility.Collapsed;
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
        sampleBox.TextChanged += (_, _) => UpdatePreview();
        colorBox.TextChanged += (_, _) => UpdatePreview();
        boldCheck.Click += (_, _) => UpdatePreview();
        underlineCheck.Click += (_, _) => UpdatePreview();
        matchCaseCheck.Click += (_, _) => UpdatePreview();

        addButton.Click += (_, _) => ShowForm(null);
        editButton.Click += (_, _) =>
        {
            if (SelectedRule() is { IsBuiltin: false } rule)
                ShowForm(rule);
        };
        deleteButton.Click += (_, _) =>
        {
            if (SelectedRule() is { IsBuiltin: false } rule)
            {
                App.Highlights.RemoveCustom(rule.Id);
                RefreshList();
                onChanged();
            }
        };
        cancelButton.Click += (_, _) => HideForm();
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

            App.Highlights.SaveCustom(new HighlightRule
            {
                Id = editingId ?? $"custom-{Guid.NewGuid():N}"[..15],
                Name = name,
                Pattern = pattern,
                Color = color.ToLowerInvariant(),
                Bold = boldCheck.IsChecked == true,
                Underline = underlineCheck.IsChecked == true,
                MatchCase = matchCaseCheck.IsChecked == true,
                Enabled = true,
            });
            HideForm();
            RefreshList();
            onChanged();
        };

        RefreshList();
        await dialog.ShowAsync();
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
