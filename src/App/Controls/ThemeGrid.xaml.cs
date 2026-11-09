using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Resesh.Core.Storage;
using Resesh.Terminal;

namespace Resesh.App.Controls;

/// <summary>Themes in rows that wrap to the available width: <see cref="Rows"/> of them, or
/// all of them when that is null. It sizes to its content rather than scrolling, so every card
/// it shows is built, none virtualized; past the first row they are added a row at a time
/// while the window is idle, so a long list never stalls it. The selected theme always shows,
/// in the last place, when it would otherwise be past the cut. Filtering and reordering never
/// change the saved choice.</summary>
public sealed partial class ThemeGrid : UserControl
{
    public static readonly DependencyProperty ThemesProperty = DependencyProperty.Register(
        nameof(Themes), typeof(IReadOnlyList<ThemeChoice>), typeof(ThemeGrid), new PropertyMetadata(null, ThemesUpdated));
    public static readonly DependencyProperty SelectedThemeProperty = DependencyProperty.Register(
        nameof(SelectedTheme), typeof(ThemeChoice), typeof(ThemeGrid), new PropertyMetadata(null, SelectionUpdated));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(ThemeQuery), typeof(ThemeGrid), new PropertyMetadata(ThemeQuery.All, Reset));

    // ThemeSwatch's width plus the container's right margin.
    private const double ItemStride = 296 + 10;

    private bool _updating;
    private int? _rows = 2;
    private int _columns;
    // Themes shuffled or sorted by name, or null for the catalog's order.
    private List<ThemeChoice>? _order;
    // Every theme to show, and the ones added to the list so far.
    private List<ThemeChoice> _target = [];
    private ObservableCollection<ThemeChoice> _shown = [];
    // Bumped by each refilter, so rows queued for an older list are dropped.
    private int _generation;

    public ThemeGrid()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            var columns = Math.Max(1, (int)((args.NewSize.Width + 10) / ItemStride));
            if (columns != _columns)
            {
                _columns = columns;
                ApplyFilter();
                ColumnsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public IReadOnlyList<ThemeChoice>? Themes
    {
        get => (IReadOnlyList<ThemeChoice>?)GetValue(ThemesProperty);
        set => SetValue(ThemesProperty, value);
    }

    public ThemeChoice? SelectedTheme
    {
        get => (ThemeChoice?)GetValue(SelectedThemeProperty);
        set => SetValue(SelectedThemeProperty, value);
    }

    public ThemeQuery Query
    {
        get => (ThemeQuery)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value ?? ThemeQuery.All);
    }

    /// <summary>How many rows to show, or null for every matching theme.</summary>
    public int? Rows
    {
        get => _rows;
        set
        {
            if (_rows == value)
                return;
            _rows = value is { } rows ? Math.Max(1, rows) : null;
            ApplyFilter();
        }
    }

    public Control FocusTarget => ThemeList;

    /// <summary>The width the cards fill, from the first card's left edge to the last one's right.</summary>
    public double RowWidth => Math.Max(0, _columns * ItemStride - 10);

    /// <summary>Raised when the width fits a different number of cards per row.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Only a user pick, not a source refresh or a filtered-out selection.</summary>
    public event EventHandler<ThemeChoice>? SelectionChanged;

    /// <summary>Puts the themes in a new random order. The selected theme keeps its place, so
    /// the card under the pointer is still the current theme.</summary>
    public void Shuffle()
    {
        if (Themes is not { Count: > 1 } themes)
            return;
        var shown = _target;
        var selected = MatchingSelection(shown);
        var place = selected is null ? -1 : shown.IndexOf(selected);
        var order = themes.ToArray();
        Random.Shared.Shuffle(order);
        _order = [.. order];
        if (selected is not null && place >= 0)
        {
            _order.RemoveAll(theme => Same(theme, selected));
            // Back in at the same place among the themes the query shows.
            var at = 0;
            for (var seen = 0; at < _order.Count && seen < place; at++)
                if (Query.Matches(_order[at]))
                    seen++;
            _order.Insert(at, selected);
        }
        ApplyFilter();
    }

    /// <summary>Puts the themes in name order, A to Z.</summary>
    public void SortByName()
    {
        _order = Themes?.OrderBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApplyFilter();
    }

    private static void ThemesUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((ThemeGrid)sender)._order = null;
        Reset(sender, args);
    }

    private static void Reset(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ThemeGrid)sender).ApplyFilter();

    private static void SelectionUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var grid = (ThemeGrid)sender;
        // A pick from another row can be past this row's cut; it shows in the last place.
        if (grid.SelectedTheme is { } theme && !grid._target.Any(shownTheme => Same(shownTheme, theme)))
            grid.ApplyFilter();
        else
            grid.FollowSelection();
    }

    private void ApplyFilter()
    {
        if (_columns == 0)
            return; // laid out once the width is known
        var matches = ((IEnumerable<ThemeChoice>?)_order ?? Themes)?.Where(Query.Matches).ToList() ?? [];
        var limit = _rows is { } rows ? _columns * rows : int.MaxValue;
        var shown = matches.Take(limit).ToList();
        if (SelectedTheme is { } selected && shown.Count == limit && !shown.Any(theme => Same(theme, selected))
            && matches.FirstOrDefault(theme => Same(theme, selected)) is { } match)
            shown[^1] = match;
        _target = shown;
        var generation = ++_generation;
        _shown = [.. shown.Take(_columns)];
        _updating = true;
        try
        {
            ThemeList.ItemsSource = _shown;
            ThemeList.SelectedItem = MatchingSelection(_shown);
        }
        finally { _updating = false; }
        if (shown.Count > _shown.Count)
            AfterLayout(generation);
    }

    /// <summary>Adds the next row of cards, then queues the one after it.</summary>
    private void AddRow(int generation)
    {
        if (generation != _generation)
            return;
        var end = Math.Min(_target.Count, _shown.Count + _columns);
        for (var i = _shown.Count; i < end; i++)
            _shown.Add(_target[i]);
        if (ThemeList.SelectedItem is null && MatchingSelection(_shown) is not null)
            FollowSelection();
        if (_shown.Count < _target.Count)
            AfterLayout(generation);
    }

    /// <summary>Queues the next row once the cards just added are laid out. Queued straight
    /// away, several idle turns can run before one layout pass, which then builds all their
    /// cards at once and stalls the window.</summary>
    private void AfterLayout(int generation)
    {
        void Laid(object? sender, object args)
        {
            ThemeList.LayoutUpdated -= Laid;
            DispatcherQueue.TryPost(DispatcherQueuePriority.Low, () => AddRow(generation));
        }
        ThemeList.LayoutUpdated += Laid;
    }

    private ThemeChoice? MatchingSelection(IList<ThemeChoice> shown) =>
        SelectedTheme is { } selected ? shown.FirstOrDefault(theme => Same(theme, selected)) : null;

    private static bool Same(ThemeChoice a, ThemeChoice b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private void FollowSelection()
    {
        _updating = true;
        try { ThemeList.SelectedItem = MatchingSelection(_shown); }
        finally { _updating = false; }
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (var item in args.RemovedItems.Concat(args.AddedItems))
            if (ThemeList.ContainerFromItem(item) is ListViewItem container)
                UpdateSwatch(container);
        if (_updating || ThemeList.SelectedItem is not ThemeChoice picked)
            return;
        SelectedTheme = picked;
        SelectionChanged?.Invoke(this, picked);
    }

    private void ThemeList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
            return;
        if (args.Phase == 0)
        {
            if (args.Item is ThemeChoice theme)
                AutomationProperties.SetName(args.ItemContainer, theme.Name);
            args.RegisterUpdateCallback(1, ThemeList_ContainerContentChanging);
        }
        else
            UpdateSwatch(args.ItemContainer);
    }

    private static void UpdateSwatch(SelectorItem container)
    {
        if (container.ContentTemplateRoot is ThemeSwatch swatch)
            swatch.IsSelected = container.IsSelected;
    }
}
