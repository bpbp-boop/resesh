using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>One rule in the list. Toggling its check box enables or disables it at once.</summary>
public sealed class HighlightRuleItem(HighlightRule rule, string packTag, Action<string, bool> setEnabled)
{
    private bool _enabled = rule.Enabled;

    public HighlightRule Rule { get; } = rule;
    public string Name => Rule.Name;

    public Brush SwatchBrush { get; } = new SolidColorBrush(HighlightRulesEditor.TryParseRuleColor(rule.Color) ?? Colors.Transparent);

    /// <summary>The terminal skips a rule whose colour it cannot read, so the list says so
    /// (with an empty swatch) rather than showing a colour that is never painted.</summary>
    public string PackTag { get; } = HighlightRulesEditor.TryParseRuleColor(rule.Color) is null
        ? (packTag.Length == 0 ? "" : packTag + " · ") + "invalid color, not applied"
        : packTag;
    public Windows.UI.Text.FontWeight NameWeight => Rule.Bold ? FontWeights.SemiBold : FontWeights.Normal;
    public string EnabledAutomationId => $"SettingsHighlightRuleEnabled_{Rule.Id}";

    public bool? Enabled
    {
        get => _enabled;
        set
        {
            if (value is not { } enabled || enabled == _enabled)
                return;
            _enabled = enabled;
            setEnabled(Rule.Id, enabled);
        }
    }

    public override string ToString() => Name;
}

/// <summary>
/// Keyword-highlighting rules on the Settings page: per-rule enable toggles, custom rules
/// with a live regex preview, and the same editing for built-in rules — stored as overrides
/// with a per-rule "Reset to default" back to the shipped definition. Edits go into the
/// supplied draft; <see cref="Changed"/> tells the page to commit them.
/// </summary>
public sealed partial class HighlightRulesEditor : UserControl
{
    private const string DefaultSample =
        "GigabitEthernet0/0/1 is up, eth0 is down — 10.0.0.1/24 fe80::1 00:1a:2b:3c:4d:5e ospf uptime 1w2d";

    private readonly HighlightsStore _draft;
    private HighlightRule? _editing; // null while adding a new custom rule

    /// <summary>Raised after a rule is saved, toggled, deleted, or reset.</summary>
    public event Action? Changed;

    public ObservableCollection<HighlightRuleItem> Rules { get; } = [];

    public HighlightRulesEditor(HighlightsStore draft)
    {
        _draft = draft;
        InitializeComponent();
        ListSampleBox.Text = DefaultSample;
        RefreshList();
        RefreshCombinedPreview();
    }

    private HighlightRule? SelectedRule => (RuleList.SelectedItem as HighlightRuleItem)?.Rule;

    private void RaiseChanged()
    {
        RefreshCombinedPreview();
        Changed?.Invoke();
    }

    private void RefreshList()
    {
        Rules.Clear();
        foreach (var rule in _draft.AllRules)
        {
            var packTag = rule.IsBuiltin
                ? _draft.IsOverridden(rule.Id) ? rule.Pack + " · edited" : rule.Pack
                : "custom";
            Rules.Add(new HighlightRuleItem(rule, packTag, (id, enabled) =>
            {
                _draft.SetEnabled(id, enabled);
                RaiseChanged();
            }));
        }
    }

    /// <summary>The sample with every enabled rule applied; a later rule wins an overlap.</summary>
    private void RefreshCombinedPreview()
    {
        CombinedPreview.Inlines.Clear();
        var sample = ListSampleBox.Text;
        // As the terminal does, a rule whose colour it cannot read paints nothing.
        var rules = _draft.AllRules.Where(r => r.Enabled && TryParseRuleColor(r.Color) is not null).ToList();
        var winner = new int[sample.Length];
        Array.Fill(winner, -1);
        for (var r = 0; r < rules.Count; r++)
        {
            try
            {
                var regex = new Regex(rules[r].Pattern, PreviewOptions(rules[r].MatchCase), TimeSpan.FromMilliseconds(200));
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
                run.Foreground = new SolidColorBrush(TryParseRuleColor(rule.Color)!.Value);
                if (rule.Bold)
                    run.FontWeight = FontWeights.Bold;
                if (rule.Underline)
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            }
            CombinedPreview.Inlines.Add(run);
            start = i;
        }
    }

    // ---- form ----

    private void ShowForm(HighlightRule? existing)
    {
        _editing = existing;
        NameBox.Text = existing?.Name ?? "";
        PatternBox.Text = existing?.Pattern ?? "";
        ColorBox.Text = existing?.Color ?? "#e5c07b";
        BoldSwitch.IsOn = existing?.Bold ?? false;
        UnderlineSwitch.IsOn = existing?.Underline ?? false;
        MatchCaseSwitch.IsOn = existing?.MatchCase ?? false;
        OverviewSwitch.IsOn = existing?.ShowInOverview ?? false;
        // One logical sample: the form's box and the list's box mirror each other, so a
        // pasted line survives the round trip.
        FormSampleBox.Text = ListSampleBox.Text;
        ResetButton.Visibility = existing is { IsBuiltin: true } ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.IsEnabled = existing is not null && _draft.IsOverridden(existing.Id);
        FormStatus.Text = "";
        ListPanel.Visibility = Visibility.Collapsed;
        FormPanel.Visibility = Visibility.Visible;
        FormPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        NameBox.Focus(FocusState.Programmatic);
        UpdateFormPreview();
    }

    private void HideForm()
    {
        FormPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        ListPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        AddButton.Focus(FocusState.Programmatic);
    }

    private void ShowFormStatus(string message, bool error)
    {
        FormStatus.Text = message;
        FormStatus.Foreground = (Brush)Application.Current.Resources[
            error ? "SystemFillColorCriticalBrush" : "SessionTreeMutedForegroundBrush"];
    }

    private void UpdateFormPreview()
    {
        var color = TryParseColor(ColorBox.Text.Trim()) ?? Colors.White;
        Swatch.Background = new SolidColorBrush(color);
        FormPreview.Inlines.Clear();

        var pattern = PatternBox.Text;
        var sample = FormSampleBox.Text;
        if (pattern.Length == 0)
        {
            FormPreview.Inlines.Add(new Run { Text = sample });
            FormStatus.Text = "";
            return;
        }

        try
        {
            var regex = new Regex(pattern, PreviewOptions(MatchCaseSwitch.IsOn), TimeSpan.FromMilliseconds(200));
            var index = 0;
            var matched = 0;
            foreach (Match m in regex.Matches(sample))
            {
                if (m.Length == 0)
                    continue;
                if (m.Index > index)
                    FormPreview.Inlines.Add(new Run { Text = sample[index..m.Index] });
                var run = new Run
                {
                    Text = m.Value,
                    Foreground = new SolidColorBrush(color),
                    FontWeight = BoldSwitch.IsOn ? FontWeights.Bold : FontWeights.Normal,
                };
                if (UnderlineSwitch.IsOn)
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                FormPreview.Inlines.Add(run);
                index = m.Index + m.Length;
                matched++;
            }
            if (index < sample.Length)
                FormPreview.Inlines.Add(new Run { Text = sample[index..] });
            if (matched == 0)
                ShowFormStatus("No matches in the sample.", error: false);
            else
                FormStatus.Text = "";
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            FormPreview.Inlines.Add(new Run { Text = sample });
            ShowFormStatus(ex is RegexMatchTimeoutException
                ? "Pattern is too slow (timed out on the sample)."
                : $"Invalid regex: {ex.Message}", error: true);
        }
    }

    // ---- events ----

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditButton.IsEnabled = SelectedRule is not null;
        DeleteButton.IsEnabled = SelectedRule is { IsBuiltin: false };
    }

    private void ListSample_TextChanged(object sender, TextChangedEventArgs e) => RefreshCombinedPreview();

    private void FormSample_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFormPreview();
        ListSampleBox.Text = FormSampleBox.Text;
    }

    private void FormField_TextChanged(object sender, TextChangedEventArgs e) => UpdateFormPreview();

    private void FormField_Toggled(object sender, RoutedEventArgs e) => UpdateFormPreview();

    private void Add_Click(object sender, RoutedEventArgs e) => ShowForm(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is { } rule)
            ShowForm(rule);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is not { IsBuiltin: false } rule)
            return;
        _draft.RemoveCustom(rule.Id);
        RefreshList();
        RaiseChanged();
    }

    private void CancelRule_Click(object sender, RoutedEventArgs e) => HideForm();

    private void ResetRule_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { IsBuiltin: true } rule || !_draft.ResetBuiltin(rule.Id))
            return;
        HideForm();
        RefreshList();
        RaiseChanged();
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var pattern = PatternBox.Text.Trim();
        var color = ColorBox.Text.Trim();
        if (name.Length == 0 || pattern.Length == 0)
        {
            ShowFormStatus("Name and pattern are required.", error: true);
            return;
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            ShowFormStatus($"Invalid regex: {ex.Message}", error: true);
            return;
        }
        if (TryParseColor(color) is null)
        {
            ShowFormStatus("Color must be #RRGGBB.", error: true);
            return;
        }

        if (_editing is { IsBuiltin: true } builtin)
        {
            _draft.SaveBuiltinOverride(builtin with
            {
                Name = name,
                Pattern = pattern,
                Color = color.ToLowerInvariant(),
                Bold = BoldSwitch.IsOn,
                Underline = UnderlineSwitch.IsOn,
                MatchCase = MatchCaseSwitch.IsOn,
                ShowInOverview = OverviewSwitch.IsOn,
            });
        }
        else
        {
            _draft.SaveCustom(new HighlightRule
            {
                Id = _editing?.Id ?? $"custom-{Guid.NewGuid():N}"[..15],
                Name = name,
                Pattern = pattern,
                Color = color.ToLowerInvariant(),
                Bold = BoldSwitch.IsOn,
                Underline = UnderlineSwitch.IsOn,
                MatchCase = MatchCaseSwitch.IsOn,
                ShowInOverview = OverviewSwitch.IsOn,
                Enabled = _editing?.Enabled ?? true,
            });
        }
        HideForm();
        RefreshList();
        RaiseChanged();
    }

    /// <summary>The options the terminal's highlighter compiles with (culture-invariant
    /// case folding), so the preview matches what the terminal will paint.</summary>
    private static RegexOptions PreviewOptions(bool matchCase) =>
        RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);

    /// <summary>A typed colour, accepted exactly when the terminal accepts it.</summary>
    internal static Windows.UI.Color? TryParseColor(string text) =>
        Resesh.Terminal.HexColor.Parse(text) is { } rgb ? ToColor(rgb) : null;

    /// <summary>A stored rule's colour as the terminal reads it: none means the default.</summary>
    internal static Windows.UI.Color? TryParseRuleColor(string? text) =>
        Resesh.Terminal.HexColor.ParseHighlight(text) is { } rgb ? ToColor(rgb) : null;

    private static Windows.UI.Color ToColor(uint rgb) =>
        Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
