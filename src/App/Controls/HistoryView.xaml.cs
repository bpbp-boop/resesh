using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.History;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Windows.UI;

namespace Resesh.App.Controls;

/// <summary>Text plus the character ranges to emphasize in it.</summary>
public sealed record HighlightedText(string Text, IReadOnlyList<TextSpan> Spans, Brush? Background = null)
{
    public static readonly HighlightedText Empty = new("", []);
}

/// <summary>Sets a TextBlock's text with search matches marked by a highlight background,
/// the way terminal find marks them.</summary>
public static class HistoryHighlight
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(HighlightedText), typeof(HistoryHighlight), new PropertyMetadata(null, OnChanged));

    public static void SetSource(DependencyObject target, HighlightedText? value) => target.SetValue(SourceProperty, value);
    public static HighlightedText? GetSource(DependencyObject target) => (HighlightedText?)target.GetValue(SourceProperty);

    /// <summary>Amber at partial opacity reads on both dark and light surfaces.</summary>
    internal static Brush MatchBackground { get; } = new SolidColorBrush(Color.FromArgb(0x70, 0xF2, 0xB7, 0x05));
    internal static Brush ActiveMatchBackground { get; } = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0x8C, 0x28));
    internal static Brush ActiveMatchForeground { get; } = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x10, 0x10));

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block)
            return;
        var source = args.NewValue as HighlightedText ?? HighlightedText.Empty;
        block.TextHighlighters.Clear();
        block.Text = source.Text;
        if (source.Spans.Count == 0)
            return;
        var highlighter = new TextHighlighter { Background = source.Background ?? MatchBackground };
        foreach (var span in source.Spans)
        {
            if (span.End > source.Text.Length)
                break;
            highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        }
        block.TextHighlighters.Add(highlighter);
    }
}

/// <summary>One row of the result list: a day heading or a matching command.</summary>
public sealed class HistoryListItem
{
    public bool IsHeader { get; init; }
    public string HeaderText { get; init; } = "";
    public CommandHistoryHit? Hit { get; init; }
    public HighlightedText Command { get; init; } = HighlightedText.Empty;
    public HighlightedText Snippet { get; init; } = HighlightedText.Empty;
    public string Meta { get; init; } = "";
    public string Time { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string OutputMatchText { get; init; } = "";
    public string StatusName { get; init; } = "";
    public Brush? StatusBrush { get; init; }
    public Visibility SnippetVisibility { get; init; } = Visibility.Collapsed;
    public FontFamily? MonoFont { get; init; }
}

public sealed partial class HistoryItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? HitTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is HistoryListItem { IsHeader: true } ? HeaderTemplate : HitTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

public sealed record HistorySessionChoice(Guid? Id, string Label);

/// <summary>
/// Search over saved command history: a search box that understands host:, in:, and exit:
/// filters, result and time filters, results grouped by day with matches highlighted, and
/// a detail pane with the full output, match navigation, and actions to reuse the command.
/// </summary>
public sealed partial class HistoryView : UserControl
{
    private const int ResultLimit = 500;
    private const int MaxOutputHighlights = 5000;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43));
    private static readonly Brush FailureBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF8, 0x51, 0x49));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    private int _historyCount;
    private bool _hasHits;
    private IReadOnlyList<string> _terms = [];
    private CancellationTokenSource? _searchCancellation;
    private DispatcherQueueTimer? _searchDebounce;
    private DispatcherQueueTimer? _feedbackTimer;
    private CommandHistoryEntry? _selected;
    private HistoryListItem? _selectedItem;
    private IReadOnlyList<TextSpan> _outputSpans = [];
    private int _activeMatch = -1;
    private Run? _outputRun;
    private bool _historyEnabled;
    private bool _loaded;
    private bool _suppressFilterEvents;
    private Guid? _pendingSessionFilter;
    private FontFamily _monoFont = new("Cascadia Mono, Consolas");

    public event Action? CloseRequested;

    /// <summary>Types the command at the active terminal's prompt. Returns false when no
    /// connected terminal can take it.</summary>
    public Func<CommandHistoryEntry, bool>? InsertCommand { get; set; }

    /// <summary>Whether a connected terminal tab is active right now.</summary>
    public Func<bool>? CanInsert { get; set; }

    /// <summary>The saved session an entry came from, if it still exists.</summary>
    public Func<CommandHistoryEntry, string?>? SessionNameFor { get; set; }

    public Action<CommandHistoryEntry>? OpenSession { get; set; }

    /// <summary>Turns history on in Settings. The view refreshes itself afterwards.</summary>
    public Action? TurnOnRequested { get; set; }

    public bool IsOpen => Visibility == Visibility.Visible;

    public HistoryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(View_KeyDown), handledEventsToo: true);
        BuildKeyHints();
    }

    public void Open(bool historyEnabled, Guid? sessionId = null, string? fontFamily = null)
    {
        _historyEnabled = historyEnabled;
        _pendingSessionFilter = sessionId;
        if (!string.IsNullOrWhiteSpace(fontFamily))
            _monoFont = new FontFamily(fontFamily);
        DetailCommand.FontFamily = _monoFont;
        OutputText.FontFamily = _monoFont;
        OffNotice.IsOpen = false;
        Visibility = Visibility.Visible;
        App.History.Changed += History_Changed;
        _ = LoadAsync(keepSelection: false);
        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        });
    }

    /// <summary>History was turned on or off while the view is open.</summary>
    public void SetHistoryEnabled(bool enabled)
    {
        _historyEnabled = enabled;
        if (IsOpen)
            _ = LoadAsync(keepSelection: true);
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        App.History.Changed -= History_Changed;
        _searchCancellation?.Cancel();
        _searchDebounce?.Stop();
        Visibility = Visibility.Collapsed;
        ExitCompare();
        ResultList.ItemsSource = null;
        _historyCount = 0;
        _selected = null;
        _selectedItem = null;
        _loaded = false;
        SetOutput(null);
    }

    private void History_Changed() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsOpen)
                _ = LoadAsync(keepSelection: true);
        });

    private async Task LoadAsync(bool keepSelection)
    {
        CommandHistorySummary summary;
        try
        {
            summary = await Task.Run(App.History.Summary);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            summary = new CommandHistorySummary(0, [], new Dictionary<DateOnly, int>());
            App.ReportRecoverableError(exception);
        }
        if (!IsOpen)
            return;
        _historyCount = summary.Count;
        _loaded = true;
        PopulateSessionFilter(summary);
        UpdateCalendarDays(summary);
        await RunSearchAsync(keepSelection);
    }

    private static bool IsStoreFailure(Exception exception) => CommandHistoryStore.IsStorageFailure(exception);

    private void PopulateSessionFilter(CommandHistorySummary summary)
    {
        var current = _pendingSessionFilter ?? (SessionFilter.SelectedItem as HistorySessionChoice)?.Id;
        _pendingSessionFilter = null;
        var choices = new List<HistorySessionChoice> { new(null, "All sessions") };
        choices.AddRange(summary.Sessions
            .Select(session => new HistorySessionChoice(session.Id,
                string.IsNullOrWhiteSpace(session.Name) ? session.Target : session.Name))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase));
        _suppressFilterEvents = true;
        SessionFilter.ItemsSource = choices;
        SessionFilter.SelectedItem = choices.FirstOrDefault(choice => choice.Id == current) ?? choices[0];
        _suppressFilterEvents = false;
    }

    private static string SessionLabel(CommandHistoryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.SessionName) ? entry.Target : entry.SessionName;

    private CommandHistoryQuery BuildQuery()
    {
        var now = DateTimeOffset.Now;
        // A picked day replaces the relative time range; the two are kept exclusive.
        DateTimeOffset? day = DateFilter.Date is { } picked
            ? new DateTimeOffset(picked.LocalDateTime.Date)
            : null;
        return new CommandHistoryQuery
        {
            Text = SearchBox.Text,
            SessionId = (SessionFilter.SelectedItem as HistorySessionChoice)?.Id,
            Status = StatusFilter.SelectedIndex switch
            {
                1 => CommandHistoryStatus.Failed,
                2 => CommandHistoryStatus.Succeeded,
                _ => CommandHistoryStatus.Any,
            },
            Until = day?.AddDays(1),
            Since = day ?? TimeFilter.SelectedIndex switch
            {
                1 => now.AddHours(-1),
                2 => new DateTimeOffset(DateTime.Today),
                3 => now.AddDays(-7),
                4 => now.AddDays(-30),
                _ => null,
            },
            SearchOutput = SearchOutputBox.IsChecked == true,
        };
    }

    private void ScheduleSearch()
    {
        if (!_loaded)
            return;
        _searchDebounce ??= CreateDebounce();
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private DispatcherQueueTimer CreateDebounce()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(120);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _ = RunSearchAsync(keepSelection: false);
        return timer;
    }

    private async Task RunSearchAsync(bool keepSelection)
    {
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var query = BuildQuery();
        CommandHistoryResults results;
        try
        {
            results = await Task.Run(
                () => App.History.Search(query, ResultLimit, cancellation.Token),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            App.ReportRecoverableError(exception);
            return;
        }
        if (cancellation.IsCancellationRequested || !IsOpen)
            return;
        ShowResults(results, query, keepSelection);
    }

    private void ShowResults(CommandHistoryResults results, CommandHistoryQuery query, bool keepSelection)
    {
        _terms = results.Terms;
        var previousId = keepSelection ? _selected?.Id : null;
        var previousIndex = keepSelection && _selectedItem is not null && ResultList.ItemsSource is List<HistoryListItem> old
            ? old.IndexOf(_selectedItem)
            : -1;

        var items = new List<HistoryListItem>(results.Hits.Count + 16);
        DateTime? day = null;
        foreach (var hit in results.Hits)
        {
            var started = hit.Entry.StartedAt.ToLocalTime();
            if (day != started.Date)
            {
                day = started.Date;
                items.Add(new HistoryListItem { IsHeader = true, HeaderText = DayLabel(started.Date) });
            }
            items.Add(CreateItem(hit));
        }
        ResultList.ItemsSource = items;

        CountText.Text = results.TotalMatches switch
        {
            0 => "",
            1 => "1 command",
            var total when total > results.Hits.Count =>
                $"Newest {results.Hits.Count:N0} of {total:N0} commands",
            var total => $"{total:N0} commands",
        };

        var hasHits = items.Count > 0;
        _hasHits = hasHits;
        // Filters and key hints mean nothing before the first command is saved; compare
        // mode keeps its pane when history changes underneath it.
        ApplyModeVisibility();
        if (!hasHits)
        {
            ShowEmptyState(query);
            SelectItem(null);
            return;
        }

        var target = previousId is null ? null : items.FirstOrDefault(item => item.Hit?.Entry.Id == previousId);
        if (target is null && previousIndex >= 0)
        {
            // The selected entry was deleted: keep the cursor where it was.
            target = items.Skip(Math.Min(previousIndex, items.Count - 1)).FirstOrDefault(item => !item.IsHeader)
                ?? items.LastOrDefault(item => !item.IsHeader);
        }
        target ??= items.First(item => !item.IsHeader);
        SelectItem(target);
    }

    private void ShowEmptyState(CommandHistoryQuery query)
    {
        EmptyAction.Visibility = Visibility.Collapsed;
        if (_historyCount == 0 && !_historyEnabled)
        {
            EmptyIcon.Glyph = "";
            EmptyTitle.Text = "Command history is off";
            EmptyMessage.Text = "Turn it on to save every command you run with its output, folder, and result. " +
                "Search across all your sessions here later. History stays on this computer.";
            EmptyAction.Visibility = Visibility.Visible;
        }
        else if (_historyCount == 0)
        {
            EmptyIcon.Glyph = "";
            EmptyTitle.Text = "No commands yet";
            EmptyMessage.Text = "Commands appear here when they finish, from every tab. " +
                "Shell integration adds exit codes; without it, commands are found at the prompt.";
        }
        else
        {
            EmptyIcon.Glyph = "";
            EmptyTitle.Text = "No matching commands";
            var hints = new List<string>();
            if (query.Until is not null)
                hints.Add("pick another day");
            if (query.SessionId is not null || query.Status != CommandHistoryStatus.Any || query.Since is not null)
                hints.Add("clear a filter");
            if (!query.SearchOutput)
                hints.Add("turn on Search output");
            hints.Add("use fewer words");
            EmptyMessage.Text = $"Try to {string.Join(", or ", hints)}. " +
                "Narrow a search with host:name, in:folder, or exit:fail.";
        }
    }

    private HistoryListItem CreateItem(CommandHistoryHit hit)
    {
        var entry = hit.Entry;
        var started = entry.StartedAt.ToLocalTime();
        var meta = SessionLabel(entry);
        if (!string.IsNullOrEmpty(entry.WorkingDirectory))
            meta += "  ·  " + entry.WorkingDirectory;
        var status = StatusOf(entry);
        return new HistoryListItem
        {
            Hit = hit,
            // Newlines and tabs become spaces: same length, so match spans still line up.
            Command = new HighlightedText(OneLine(entry.Command), hit.CommandSpans),
            Snippet = hit.Snippet is null ? HighlightedText.Empty : new HighlightedText(hit.Snippet, hit.SnippetSpans),
            SnippetVisibility = hit.Snippet is null ? Visibility.Collapsed : Visibility.Visible,
            OutputMatchText = hit.OutputMatches switch
            {
                0 => "",
                1 => "1 match",
                var count => $"{count:N0} matches",
            },
            Meta = meta,
            Time = started.ToString("t", CultureInfo.CurrentCulture),
            DurationText = entry.Duration is { } duration && duration > TimeSpan.Zero ? FormatDuration(duration) : "",
            StatusBrush = status.Brush,
            StatusName = status.Name,
            MonoFont = _monoFont,
        };
    }

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private static (Brush Brush, string Name) StatusOf(CommandHistoryEntry entry) =>
        entry.ExitCode switch
        {
            0 => (SuccessBrush, "Succeeded"),
            { } code => (FailureBrush, $"Failed, exit {code}"),
            null => (UnknownBrush, "Result unknown"),
        };

    private static string DayLabel(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
            return "Today";
        if (date == today.AddDays(-1))
            return "Yesterday";
        var format = date.Year == today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy";
        return date.ToString(format, CultureInfo.CurrentCulture);
    }

    internal static string FormatDuration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 1 => $"{duration.TotalMilliseconds:0} ms",
        < 60 => $"{duration.TotalSeconds:0.#} s",
        < 3600 => $"{(int)duration.TotalMinutes} min {duration.Seconds} s",
        _ => $"{(int)duration.TotalHours} h {duration.Minutes} min",
    };

    // ---- selection and detail ----

    private void SelectItem(HistoryListItem? item)
    {
        _selectedItem = item;
        if (item is not null && !ReferenceEquals(ResultList.SelectedItem, item))
        {
            ResultList.SelectedItem = item;
            ResultList.ScrollIntoView(item);
        }
        ShowDetail(item?.Hit?.Entry);
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is HistoryListItem { IsHeader: true })
        {
            // Headings are not selectable; put the selection back.
            ResultList.SelectedItem = _selectedItem;
            return;
        }
        if (ResultList.SelectedItem is HistoryListItem item && !ReferenceEquals(item, _selectedItem))
        {
            _selectedItem = item;
            ShowDetail(item.Hit?.Entry);
        }
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryListItem { IsHeader: false } item)
            SelectItem(item);
    }

    private void ResultList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container || args.Item is not HistoryListItem item)
            return;
        // Containers are recycled between headings and commands: set both states every time.
        container.IsHitTestVisible = !item.IsHeader;
        container.IsTabStop = !item.IsHeader;
        AutomationProperties.SetName(container, item.IsHeader
            ? item.HeaderText
            : $"{item.Command.Text}, {item.StatusName}, {item.Meta}, {item.Time}");
    }

    private void ShowDetail(CommandHistoryEntry? entry)
    {
        _selected = entry;
        if (entry is null)
        {
            SetOutput(null);
            _ = LoadRunsAsync(null);
            return;
        }

        HistoryHighlight.SetSource(DetailCommand,
            new HighlightedText(entry.Command, CommandHistorySearch.FindAll(entry.Command, _terms)));

        var status = StatusOf(entry);
        StatusChipDot.Fill = status.Brush;
        StatusChipText.Text = entry.ExitCode switch
        {
            0 => "Succeeded",
            { } code => $"Failed · exit {code}",
            null => "Result unknown",
        };
        StatusChip.BorderBrush = status.Brush;
        ToolTipService.SetToolTip(StatusChip, entry.ExitCode is null
            ? "The shell did not report an exit status. Turn on shell integration for this session to get one."
            : null);
        var started = entry.StartedAt.ToLocalTime();
        DurationChip.Text = entry.Duration is { } duration && duration > TimeSpan.Zero ? $"Ran {FormatDuration(duration)}" : "";
        SourceChip.Text = started.Date == DateTime.Today
            ? $"Today at {started:T}"
            : $"{started.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)} at {started.ToString("T", CultureInfo.CurrentCulture)}";

        FactsGrid.Children.Clear();
        FactsGrid.RowDefinitions.Clear();
        AddFact("Session", string.IsNullOrWhiteSpace(entry.SessionName) ? "(unsaved connection)" : entry.SessionName);
        AddFact("Target", entry.Target);
        if (!string.IsNullOrEmpty(entry.WorkingDirectory))
            AddFact("Folder", entry.WorkingDirectory);

        var canInsert = CanInsert?.Invoke() == true;
        InsertButton.IsEnabled = canInsert;
        ToolTipService.SetToolTip(InsertButton, canInsert
            ? "Type this command at the active tab's prompt without running it (Enter)"
            : "Open a connected terminal tab to insert this command");
        var sessionName = SessionNameFor?.Invoke(entry);
        OpenSessionButton.Visibility = sessionName is null ? Visibility.Collapsed : Visibility.Visible;
        OpenSessionText.Text = sessionName is null ? "Open Session" : $"Open {sessionName}";
        ToolTipService.SetToolTip(OpenSessionButton, "Connect in a new tab (Ctrl+Enter)");

        SetOutput(entry);
        _ = LoadRunsAsync(entry);
    }

    private void AddFact(string label, string value)
    {
        var row = FactsGrid.RowDefinitions.Count;
        FactsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
        };
        var text = new TextBlock
        {
            Text = value,
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SessionTreeForegroundBrush"],
        };
        AutomationProperties.SetName(text, label);
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        FactsGrid.Children.Add(name);
        FactsGrid.Children.Add(text);
    }

    private void SetOutput(CommandHistoryEntry? entry)
    {
        OutputText.Blocks.Clear();
        OutputText.TextHighlighters.Clear();
        _outputRun = null;
        _outputSpans = [];
        _activeMatch = -1;
        OutputScroller.ChangeView(0, 0, null, disableAnimation: true);

        var output = entry?.Output ?? "";
        NoOutputText.Visibility = entry is not null && output.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoOutputText.Text = entry?.OutputLost == true ? "Output not kept" : "No output";
        OutputNote.Text = entry switch
        {
            { OutputLost: true } =>
                "The output left the terminal's scrollback before the command finished, so it was not kept.",
            { OutputTruncated: true } => "Showing the first 64 KB of output.",
            _ => "",
        };
        OutputNote.Visibility = OutputNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyOutputButton.IsEnabled = output.Length > 0;

        if (output.Length > 0)
        {
            _outputRun = new Run { Text = output };
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(_outputRun);
            OutputText.Blocks.Add(paragraph);
            var spans = CommandHistorySearch.FindAll(output, _terms);
            _outputSpans = spans.Count > MaxOutputHighlights ? spans.Take(MaxOutputHighlights).ToList() : spans;
            if (_outputSpans.Count > 0)
            {
                var highlighter = new TextHighlighter { Background = HistoryHighlight.MatchBackground };
                foreach (var span in _outputSpans)
                    highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
                OutputText.TextHighlighters.Add(highlighter);
            }
        }
        UpdateMatchNavigation();
        if (_outputSpans.Count > 0)
        {
            // Layout must exist before a match position can be measured.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_activeMatch < 0 && _outputSpans.Count > 0 && ReferenceEquals(entry, _selected))
                    GoToMatch(0);
            });
        }
    }

    private void UpdateMatchNavigation()
    {
        var count = _outputSpans.Count;
        MatchPosition.Text = count switch
        {
            0 when _terms.Count > 0 && _selected?.Output.Length > 0 => "No matches in output",
            0 => "",
            _ when _activeMatch < 0 => $"{count:N0} matches",
            _ => $"{_activeMatch + 1:N0} of {count:N0}",
        };
        PreviousMatchButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextMatchButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GoToMatch(int index)
    {
        if (_outputSpans.Count == 0 || _outputRun is null)
            return;
        _activeMatch = ((index % _outputSpans.Count) + _outputSpans.Count) % _outputSpans.Count;
        var span = _outputSpans[_activeMatch];

        // Keep the passive highlighter, then paint the current match on top of it.
        while (OutputText.TextHighlighters.Count > 1)
            OutputText.TextHighlighters.RemoveAt(OutputText.TextHighlighters.Count - 1);
        var active = new TextHighlighter
        {
            Background = HistoryHighlight.ActiveMatchBackground,
            Foreground = HistoryHighlight.ActiveMatchForeground,
        };
        active.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        OutputText.TextHighlighters.Add(active);
        UpdateMatchNavigation();

        try
        {
            var position = _outputRun.ContentStart.GetPositionAtOffset(span.Start, LogicalDirection.Forward);
            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            var targetY = Math.Max(0, rect.Top - (OutputScroller.ViewportHeight / 3));
            var targetX = rect.Left < OutputScroller.HorizontalOffset
                || rect.Left > OutputScroller.HorizontalOffset + OutputScroller.ViewportWidth - 40
                ? Math.Max(0, rect.Left - 40)
                : (double?)null;
            OutputScroller.ChangeView(targetX, targetY, null);
        }
        catch (ArgumentException)
        {
            // A position past the laid-out text; leave the scroll where it is.
        }
    }

    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => GoToMatch(_activeMatch - 1);
    private void NextMatch_Click(object sender, RoutedEventArgs e) => GoToMatch(_activeMatch + 1);

    private void WrapToggle_Click(object sender, RoutedEventArgs e)
    {
        var wrap = WrapToggle.IsChecked == true;
        OutputText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        OutputScroller.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
        OutputScroller.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    // ---- actions ----

    private void InsertSelected()
    {
        if (_selected is not { } entry || InsertCommand is null)
            return;
        if (InsertCommand(entry))
            CloseRequested?.Invoke();
    }

    private void OpenSelectedSession()
    {
        if (_selected is not { } entry || OpenSession is null || SessionNameFor?.Invoke(entry) is null)
            return;
        CloseRequested?.Invoke();
        OpenSession(entry);
    }

    private void InsertButton_Click(object sender, RoutedEventArgs e) => InsertSelected();

    private void OpenSessionButton_Click(object sender, RoutedEventArgs e) => OpenSelectedSession();

    private void CopyCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } entry && CopyText(entry.Command))
        {
            CopyCommandIcon.Glyph = "\uE73E"; // checkmark until the feedback timer resets it
            ShowCopied(null);
        }
    }

    private void CopyOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { Output.Length: > 0 } entry && CopyText(entry.Output))
            ShowCopied(CopyOutputText);
    }

    private static bool CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ShowCopied(TextBlock? label)
    {
        if (label is not null)
            label.Text = "Copied";
        _feedbackTimer?.Stop();
        _feedbackTimer = DispatcherQueue.CreateTimer();
        _feedbackTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _feedbackTimer.IsRepeating = false;
        _feedbackTimer.Tick += (_, _) =>
        {
            CopyCommandIcon.Glyph = "\uE8C8";
            CopyOutputText.Text = "Copy Output";
        };
        _feedbackTimer.Start();
    }

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFlyout.Hide();
        if (_selected is not { } entry)
            return;
        _ = Task.Run(() =>
        {
            try { App.History.Delete([entry.Id]); }
            catch (Exception exception) when (CommandHistoryStore.IsStorageFailure(exception))
            {
                DispatcherQueue.TryEnqueue(() => App.ReportRecoverableError(exception));
            }
        });
    }

    private void TurnOn_Click(object sender, RoutedEventArgs e) => TurnOnRequested?.Invoke();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents || !IsOpen)
            return;
        if (ReferenceEquals(sender, TimeFilter) && TimeFilter.SelectedIndex != 0 && DateFilter.Date is not null)
        {
            // A relative range replaces a picked day.
            _suppressFilterEvents = true;
            DateFilter.Date = null;
            _suppressFilterEvents = false;
        }
        _ = RunSearchAsync(keepSelection: true);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Typing a new search leaves compare mode for the results.
        ExitCompare();
        ScheduleSearch();
    }

    // ---- day picker ----

    private Dictionary<DateTime, int> _commandsPerDay = [];

    /// <summary>Counts commands per local day and limits the calendar to the days history covers.</summary>
    private void UpdateCalendarDays(CommandHistorySummary summary)
    {
        var counts = summary.CommandsPerDay.ToDictionary(
            pair => pair.Key.ToDateTime(TimeOnly.MinValue), pair => pair.Value);
        _commandsPerDay = counts;
        if (counts.Count == 0)
            return;
        var first = counts.Keys.Min();
        var last = counts.Keys.Max();
        DateFilter.MinDate = new DateTimeOffset(first);
        DateFilter.MaxDate = new DateTimeOffset(last > DateTime.Today ? last : DateTime.Today);
    }

    /// <summary>Days without history cannot be picked; busier days show more density bars.</summary>
    private void DateFilter_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        var day = args.Item.Date.LocalDateTime.Date;
        if (!_commandsPerDay.TryGetValue(day, out var count))
        {
            args.Item.IsBlackout = true;
            args.Item.SetDensityColors(null);
            return;
        }
        args.Item.IsBlackout = false;
        var accent = ((SolidColorBrush)Application.Current.Resources["SessionAccentBrush"]).Color;
        var bars = count switch { < 10 => 1, < 50 => 2, < 200 => 3, _ => 4 };
        args.Item.SetDensityColors(Enumerable.Repeat(accent, bars).ToList());
        AutomationProperties.SetName(args.Item, $"{day:D}, {count} commands");
    }

    private void DateFilter_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        ClearDateButton.Visibility = sender.Date is null ? Visibility.Collapsed : Visibility.Visible;
        if (_suppressFilterEvents || !IsOpen)
            return;
        if (sender.Date is not null && TimeFilter.SelectedIndex != 0)
        {
            _suppressFilterEvents = true;
            TimeFilter.SelectedIndex = 0;
            _suppressFilterEvents = false;
        }
        _ = RunSearchAsync(keepSelection: false);
    }

    private void ClearDate_Click(object sender, RoutedEventArgs e)
    {
        DateFilter.Date = null;
        DateFilter.Focus(FocusState.Programmatic);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Backdrop_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // ---- keyboard ----

    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = AppShortcuts.CurrentModifiers().HasFlag(Resesh.Core.Input.KeyModifiers.Ctrl);
        var shift = AppShortcuts.CurrentModifiers().HasFlag(Resesh.Core.Input.KeyModifiers.Shift);
        var focus = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var inNavigator = ReferenceEquals(focus, SearchBox) || IsWithin(focus, ResultList);
        if (_compareMode)
        {
            switch (e.Key)
            {
                case VirtualKey.Escape when !e.Handled:
                    e.Handled = true;
                    ExitCompare();
                    break;
                case VirtualKey.F7:
                    e.Handled = true;
                    GoToChange(shift ? _changeCursor - 1 : _changeCursor + 1);
                    break;
            }
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (e.Handled)
                    return; // a ComboBox or flyout closed itself
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
            case VirtualKey.Down when inNavigator:
                e.Handled = true;
                MoveSelection(1);
                break;
            case VirtualKey.Up when inNavigator:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case VirtualKey.PageDown when inNavigator:
                e.Handled = true;
                MoveSelection(10);
                break;
            case VirtualKey.PageUp when inNavigator:
                e.Handled = true;
                MoveSelection(-10);
                break;
            case VirtualKey.Enter when inNavigator:
                e.Handled = true;
                if (ctrl)
                    OpenSelectedSession();
                else
                    InsertSelected();
                break;
            case VirtualKey.F3:
                e.Handled = true;
                GoToMatch(shift ? _activeMatch - 1 : _activeMatch + 1);
                break;
            case VirtualKey.Delete when IsWithin(focus, ResultList) && _selected is not null:
                e.Handled = true;
                DeleteFlyout.ShowAt(DeleteButton);
                break;
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }
        return false;
    }

    private void MoveSelection(int offset)
    {
        if (ResultList.ItemsSource is not List<HistoryListItem> items || items.Count == 0)
            return;
        var index = _selectedItem is null ? -1 : items.IndexOf(_selectedItem);
        var step = Math.Sign(offset);
        var remaining = Math.Abs(offset);
        var next = index;
        for (var i = index + step; i >= 0 && i < items.Count && remaining > 0; i += step)
        {
            if (items[i].IsHeader)
                continue;
            next = i;
            remaining--;
        }
        if (next >= 0 && next != index)
            SelectItem(items[next]);
    }

    private void BuildKeyHints()
    {
        KeyHints.Children.Clear();
        void Hint(string keys, string action)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var key in keys.Split(' '))
            {
                panel.Children.Add(new Border
                {
                    Padding = new Thickness(5, 0, 5, 1),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)Application.Current.Resources["SessionChromeFrameBrush"],
                    Background = (Brush)Application.Current.Resources["SessionInputBrush"],
                    Child = new TextBlock { Text = key, FontSize = 11 },
                });
            }
            panel.Children.Add(new TextBlock
            {
                Text = action,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
            });
            KeyHints.Children.Add(panel);
        }

        if (_compareMode)
        {
            Hint("F7", "Next change");
            Hint("Shift+F7", "Previous change");
            Hint("Esc", "Back to results");
            return;
        }
        Hint("↑ ↓", "Select");
        Hint("Enter", "Insert in terminal");
        Hint("Ctrl+Enter", "Open session");
        Hint("F3", "Next match");
        Hint("Esc", "Close");
        KeyHints.Children.Add(new TextBlock
        {
            Text = "Filters: host:name  in:folder  exit:fail  \"exact phrase\"",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
        });
    }
}
