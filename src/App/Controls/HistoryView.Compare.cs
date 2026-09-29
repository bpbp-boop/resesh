using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.History;
using Windows.UI;

namespace Resesh.App.Controls;

/// <summary>One row of the comparison list: a folded stretch, a unified line, or a
/// side-by-side pair.</summary>
public sealed class DiffRowItem
{
    public bool IsGap { get; init; }
    public bool IsSideBySide { get; init; }
    public bool IsChange { get; init; }
    public string GapText { get; init; } = "";
    public string OldNumber { get; init; } = "";
    public string NewNumber { get; init; } = "";
    public string Marker { get; init; } = "";
    public HighlightedText Text { get; init; } = HighlightedText.Empty;
    public HighlightedText LeftText { get; init; } = HighlightedText.Empty;
    public HighlightedText RightText { get; init; } = HighlightedText.Empty;
    public Brush? Background { get; init; }
    public Brush? LeftBackground { get; init; }
    public Brush? RightBackground { get; init; }
    public string? Note { get; init; }
    public FontFamily? MonoFont { get; init; }
}

public sealed partial class DiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GapTemplate { get; set; }
    public DataTemplate? UnifiedTemplate { get; set; }
    public DataTemplate? SideBySideTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        DiffRowItem { IsGap: true } => GapTemplate,
        DiffRowItem { IsSideBySide: true } => SideBySideTemplate,
        _ => UnifiedTemplate,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

public sealed record HistoryRunChoice(CommandHistoryEntry Entry, string Label);

/// <summary>Compare mode: two runs of one command on one host, line by line.</summary>
public sealed partial class HistoryView
{
    private const int CompareContext = 3;

    // Tints read on both themes: a faint wash for the line, a stronger one for changed words.
    private static readonly Brush RemovedLine = new SolidColorBrush(Color.FromArgb(0x30, 0xF8, 0x51, 0x49));
    private static readonly Brush RemovedWords = new SolidColorBrush(Color.FromArgb(0x80, 0xF8, 0x51, 0x49));
    private static readonly Brush AddedLine = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0xA0, 0x43));
    private static readonly Brush AddedWords = new SolidColorBrush(Color.FromArgb(0x80, 0x2E, 0xA0, 0x43));
    private static readonly Brush Transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private IReadOnlyList<CommandHistoryEntry> _runs = [];
    private IReadOnlyDictionary<string, OutputComparison> _runChanges = new Dictionary<string, OutputComparison>();
    private int _runsVersion;
    private int _compareVersion;
    private bool _compareMode;
    private bool _suppressCompareEvents;
    private List<int> _changeStarts = [];
    private int _changeCursor = -1;

    private OutputDiffOptions CompareOptions => new()
    {
        IgnoreTimestamps = IgnoreTimestampsBox.IsChecked == true,
        IgnoreNumbers = IgnoreNumbersBox.IsChecked == true,
    };

    /// <summary>Finds the other runs of the selected command on its host and offers them.</summary>
    private async Task LoadRunsAsync(CommandHistoryEntry? entry)
    {
        var version = ++_runsVersion;
        CompareButton.Visibility = Visibility.Collapsed;
        CompareFlyout.Items.Clear();
        _runs = [];
        _runChanges = new Dictionary<string, OutputComparison>();
        if (entry is null)
            return;

        IReadOnlyList<CommandHistoryEntry> runs;
        try
        {
            runs = await Task.Run(() => App.History.Runs(entry));
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            return;
        }
        if (version != _runsVersion || !ReferenceEquals(entry, _selected) || runs.Count < 2)
            return;

        _runs = runs;
        var (earlier, later) = Neighbors(entry);
        CompareText.Text = earlier is not null ? "Compare with Previous" : "Compare with Next";
        ToolTipService.SetToolTip(CompareButton, earlier is not null
            ? $"Compare with the previous run of this command on this host ({RunTime(earlier)})"
            : $"Compare with the next run of this command on this host ({RunTime(later!)})");
        CompareButton.Visibility = Visibility.Visible;
        BuildRunMenu(entry, changes: null);

        // How each other run differs from this one, for the menu labels.
        var options = CompareOptions;
        Dictionary<string, OutputComparison> changes;
        try
        {
            changes = await Task.Run(() => runs
                .Where(run => run.Id != entry.Id)
                .ToDictionary(run => run.Id, run => run.StartedAt <= entry.StartedAt
                    ? OutputDiff.Compare(run.Output, entry.Output, options)
                    : OutputDiff.Compare(entry.Output, run.Output, options)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }
        if (version != _runsVersion || !ReferenceEquals(entry, _selected))
            return;
        _runChanges = changes;
        BuildRunMenu(entry, changes);
    }

    private (CommandHistoryEntry? Earlier, CommandHistoryEntry? Later) Neighbors(CommandHistoryEntry entry)
    {
        // Runs are most recent first.
        var index = _runs.ToList().FindIndex(run => run.Id == entry.Id);
        if (index < 0)
            return (null, null);
        return (index + 1 < _runs.Count ? _runs[index + 1] : null, index > 0 ? _runs[index - 1] : null);
    }

    private void BuildRunMenu(CommandHistoryEntry entry, IReadOnlyDictionary<string, OutputComparison>? changes)
    {
        CompareFlyout.Items.Clear();
        CompareFlyout.Items.Add(new MenuFlyoutItem { Text = "Compare this run with…", IsEnabled = false });
        foreach (var run in _runs)
        {
            if (run.Id == entry.Id)
                continue;
            var label = RunLabel(run);
            if (changes?.TryGetValue(run.Id, out var comparison) == true)
                label += "  ·  " + (comparison.Identical ? "same output" : $"+{comparison.Added} −{comparison.Removed}");
            var item = new MenuFlyoutItem { Text = label };
            var captured = run;
            item.Click += (_, _) => EnterCompare(captured, entry);
            CompareFlyout.Items.Add(item);
        }
    }

    private void CompareButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (_selected is not { } entry)
            return;
        var (earlier, later) = Neighbors(entry);
        if (earlier is not null)
            EnterCompare(earlier, entry);
        else if (later is not null)
            EnterCompare(entry, later);
    }

    /// <summary>Opens compare mode for two runs; the earlier one is always on the left.</summary>
    private void EnterCompare(CommandHistoryEntry first, CommandHistoryEntry second)
    {
        var (older, newer) = first.StartedAt <= second.StartedAt ? (first, second) : (second, first);
        _compareMode = true;
        ApplyModeVisibility();
        CompareTitle.FontFamily = _monoFont;
        CompareTitle.Text = newer.Command;
        CompareSubtitle.Text = $"{SessionLabel(newer)}  ·  {newer.Target}";

        var choices = _runs.Select(run => new HistoryRunChoice(run, RunLabel(run))).ToList();
        _suppressCompareEvents = true;
        OlderRunBox.ItemsSource = choices;
        NewerRunBox.ItemsSource = choices;
        OlderRunBox.SelectedItem = choices.FirstOrDefault(choice => choice.Entry.Id == older.Id);
        NewerRunBox.SelectedItem = choices.FirstOrDefault(choice => choice.Entry.Id == newer.Id);
        _suppressCompareEvents = false;
        BuildKeyHints();
        _ = RefreshComparisonAsync(older, newer);
        DispatcherQueue.TryEnqueue(() => CompareBackButton.Focus(FocusState.Programmatic));
    }

    private void ExitCompare()
    {
        if (!_compareMode)
            return;
        _compareMode = false;
        _compareVersion++;
        DiffList.ItemsSource = null;
        ApplyModeVisibility();
        BuildKeyHints();
        if (IsOpen)
            DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    private void CompareBack_Click(object sender, RoutedEventArgs e) => ExitCompare();

    private void CompareRun_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCompareEvents || OlderRunBox.SelectedItem is not HistoryRunChoice older
            || NewerRunBox.SelectedItem is not HistoryRunChoice newer)
            return;
        _ = RefreshComparisonAsync(older.Entry, newer.Entry);
    }

    private void CompareLayout_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => RefreshCurrent();

    private void CompareOption_Changed(object sender, RoutedEventArgs e) => RefreshCurrent();

    private void RefreshCurrent()
    {
        if (_compareMode && OlderRunBox.SelectedItem is HistoryRunChoice older && NewerRunBox.SelectedItem is HistoryRunChoice newer)
            _ = RefreshComparisonAsync(older.Entry, newer.Entry);
    }

    private async Task RefreshComparisonAsync(CommandHistoryEntry older, CommandHistoryEntry newer)
    {
        var version = ++_compareVersion;
        var options = CompareOptions;
        var sideBySide = ReferenceEquals(CompareLayoutBar.SelectedItem, SideBySideLayoutItem);
        var context = ShowAllLinesBox.IsChecked == true ? -1 : CompareContext;
        var font = _monoFont;
        var same = older.Id == newer.Id;

        OutputComparison comparison;
        List<DiffRowItem> rows;
        try
        {
            (comparison, rows) = await Task.Run(() =>
            {
                var result = OutputDiff.Compare(older.Output, newer.Output, options);
                return (result, sideBySide
                    ? result.SideBySide(context).Select(row => SideBySideItem(row, font)).ToList()
                    : result.Unified(context).Select(row => UnifiedItem(row, font)).ToList());
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }
        if (version != _compareVersion || !_compareMode)
            return;

        DiffList.ItemsSource = rows;
        _changeStarts = [];
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].IsChange && (i == 0 || !rows[i - 1].IsChange))
                _changeStarts.Add(i);
        }
        _changeCursor = -1;
        UpdateChangePosition();

        var apart = FormatApart(newer.StartedAt - older.StartedAt);
        var ignored = comparison.Lines.Count(line => line.DiffersOnlyInIgnoredValues);
        CompareSummary.Text = same
            ? "Pick two different runs"
            : comparison.Identical
                ? $"No differences{(ignored > 0 ? $" ({ignored} {(ignored == 1 ? "line differs" : "lines differ")} only in ignored values)" : "")}  ·  {apart}"
                : $"{Plural(comparison.Added, "line")} added, {comparison.Removed:N0} removed  ·  {apart}";

        var warnings = new List<string>();
        void Check(CommandHistoryEntry run, string which)
        {
            if (run.OutputLost)
                warnings.Add($"The {which} run's output was not kept, so there is nothing to compare.");
            else if (run.LooksPaged)
                warnings.Add($"The {which} run went through a pager (--More--), so its output holds only the pages shown. "
                    + "Turn paging off first (terminal length 0 on network devices) for complete output.");
            if (run.OutputTruncated)
                warnings.Add($"Only the first 64 KB of the {which} run's output was kept.");
        }
        Check(older, "earlier");
        Check(newer, "later");
        if (!comparison.Aligned)
            warnings.Add("The outputs are too different to line up, so the changed part is shown as removed, then added.");
        CompareWarning.Message = string.Join(" ", warnings);
        CompareWarning.IsOpen = warnings.Count > 0;
    }

    private static DiffRowItem UnifiedItem(DiffRow row, FontFamily font)
    {
        if (row.Line is not { } line)
            return Gap(row.Skipped, font);
        var (lineBrush, wordBrush, marker) = DiffStyle(line.Kind);
        return new DiffRowItem
        {
            IsChange = line.Kind != DiffKind.Same,
            OldNumber = line.OldNumber?.ToString(CultureInfo.CurrentCulture) ?? "",
            NewNumber = line.NewNumber?.ToString(CultureInfo.CurrentCulture) ?? "",
            Marker = line.DiffersOnlyInIgnoredValues ? "≈" : marker,
            Text = new HighlightedText(line.Text, line.Changed, wordBrush),
            Background = lineBrush,
            Note = line.DiffersOnlyInIgnoredValues ? $"Differs only in ignored values. Earlier: {line.OldText}" : null,
            MonoFont = font,
        };
    }

    private static DiffRowItem SideBySideItem(SideBySideRow row, FontFamily font)
    {
        if (row.IsGap)
            return Gap(row.Skipped, font);
        var left = row.Left;
        var right = row.Right;
        var (leftLine, leftWords, _) = DiffStyle(left?.Kind ?? DiffKind.Same);
        var (rightLine, rightWords, _) = DiffStyle(right?.Kind ?? DiffKind.Same);
        var ignored = left?.Kind == DiffKind.Same && left.DiffersOnlyInIgnoredValues;
        return new DiffRowItem
        {
            IsSideBySide = true,
            IsChange = left?.Kind != DiffKind.Same || right?.Kind != DiffKind.Same,
            OldNumber = left?.OldNumber?.ToString(CultureInfo.CurrentCulture) ?? "",
            NewNumber = right?.NewNumber?.ToString(CultureInfo.CurrentCulture) ?? "",
            LeftText = left is null ? HighlightedText.Empty : new HighlightedText(left.Text, left.Changed, leftWords),
            RightText = right is null ? HighlightedText.Empty : new HighlightedText(right.Text, right.Changed, rightWords),
            LeftBackground = left is null ? Transparent : leftLine,
            RightBackground = right is null ? Transparent : rightLine,
            Note = ignored ? "Differs only in ignored values" : null,
            MonoFont = font,
        };
    }

    private static DiffRowItem Gap(int skipped, FontFamily font) => new()
    {
        IsGap = true,
        GapText = $"⋯  {Plural(skipped, "unchanged line")}. Click to show all lines.",
        MonoFont = font,
    };

    private static (Brush Line, Brush? Words, string Marker) DiffStyle(DiffKind kind) => kind switch
    {
        DiffKind.Removed => (RemovedLine, RemovedWords, "−"),
        DiffKind.Added => (AddedLine, AddedWords, "+"),
        _ => (Transparent, null, ""),
    };

    private void DiffList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DiffRowItem { IsGap: true })
        {
            ShowAllLinesBox.IsChecked = true;
            RefreshCurrent();
        }
    }

    private void NextChange_Click(object sender, RoutedEventArgs e) => GoToChange(_changeCursor + 1);

    private void PreviousChange_Click(object sender, RoutedEventArgs e) => GoToChange(_changeCursor - 1);

    private void GoToChange(int index)
    {
        if (_changeStarts.Count == 0 || DiffList.ItemsSource is not List<DiffRowItem> rows)
            return;
        _changeCursor = ((index % _changeStarts.Count) + _changeStarts.Count) % _changeStarts.Count;
        // Land a couple of rows above the change so its context shows.
        var target = Math.Max(0, _changeStarts[_changeCursor] - 2);
        DiffList.ScrollIntoView(rows[target], ScrollIntoViewAlignment.Leading);
        UpdateChangePosition();
    }

    private void UpdateChangePosition()
    {
        var count = _changeStarts.Count;
        ChangePosition.Text = count switch
        {
            0 => "",
            _ when _changeCursor < 0 => Plural(count, "change"),
            _ => $"Change {_changeCursor + 1} of {count}",
        };
        PreviousChangeButton.IsEnabled = count > 0;
        NextChangeButton.IsEnabled = count > 0;
    }

    /// <summary>Shows the pane for the current mode: compare, results, or an empty state.</summary>
    private void ApplyModeVisibility()
    {
        var hasHistory = _historyCount > 0;
        ComparePane.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = !_compareMode && hasHistory ? Visibility.Visible : Visibility.Collapsed;
        OffNotice.IsOpen = !_compareMode && !_historyEnabled && hasHistory;
        KeyHints.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = !_compareMode && _hasHits ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !_compareMode && !_hasHits ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string RunLabel(CommandHistoryEntry run)
    {
        var label = RunTime(run);
        label += run.ExitCode switch
        {
            0 => "  ·  exit 0",
            { } code => $"  ·  exit {code}",
            null => "",
        };
        if (run.OutputLost)
            label += "  ·  output not kept";
        else if (run.LooksPaged)
            label += "  ·  paged";
        else if (run.OutputTruncated)
            label += "  ·  truncated";
        return label;
    }

    private static string RunTime(CommandHistoryEntry run)
    {
        var started = run.StartedAt.ToLocalTime();
        return started.Date == DateTime.Today
            ? $"Today, {started.ToString("T", CultureInfo.CurrentCulture)}"
            : started.ToString("ddd d MMM yyyy, ", CultureInfo.CurrentCulture) + started.ToString("T", CultureInfo.CurrentCulture);
    }

    private static string FormatApart(TimeSpan span)
    {
        span = span.Duration();
        return span.TotalMinutes switch
        {
            < 1 => "less than a minute apart",
            < 60 => $"{Plural((int)span.TotalMinutes, "minute")} apart",
            < 60 * 24 => $"{Plural((int)span.TotalHours, "hour")} apart",
            _ => $"{Plural((int)span.TotalDays, "day")} apart",
        };
    }

    private static string Plural(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(count == 1 ? "" : "s")}";
}
