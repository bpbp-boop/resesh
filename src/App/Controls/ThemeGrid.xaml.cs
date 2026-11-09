using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>Themes in rows that wrap to the available width, <see cref="FirstRows"/> rows at
/// first and, when <see cref="CanShowMore"/>, more on request. It sizes to its content rather
/// than scrolling, so it shows only what it has realized: every card is built, none
/// virtualized. The selected theme always shows, in the last place, when it would otherwise be
/// past the cut. Filtering and shuffling never change the saved choice.</summary>
public sealed partial class ThemeGrid : UserControl
{
    public static readonly DependencyProperty ThemesProperty = DependencyProperty.Register(
        nameof(Themes), typeof(IReadOnlyList<ThemeChoice>), typeof(ThemeGrid), new PropertyMetadata(null, ThemesUpdated));
    public static readonly DependencyProperty SelectedThemeProperty = DependencyProperty.Register(
        nameof(SelectedTheme), typeof(ThemeChoice), typeof(ThemeGrid), new PropertyMetadata(null, SelectionUpdated));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(ThemeQuery), typeof(ThemeGrid), new PropertyMetadata(ThemeQuery.All, Reset));

    private const int MoreRows = 4;
    // ThemeSwatch's width plus the container's right margin.
    private const double ItemStride = 296 + 10;

    private bool _updating;
    private int _firstRows = 2;
    private int _rows = 2;
    private int _columns;
    private bool _canShowMore = true;
    // Themes in a shuffled order, or null for their own order.
    private List<ThemeChoice>? _order;

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

    /// <summary>Rows shown before any "Show more"; setting it collapses back to them.</summary>
    public int FirstRows
    {
        get => _firstRows;
        set
        {
            _firstRows = _rows = Math.Max(1, value);
            ApplyFilter();
        }
    }

    /// <summary>Offers "Show more" when themes are past the cut.</summary>
    public bool CanShowMore
    {
        get => _canShowMore;
        set
        {
            _canShowMore = value;
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
        var shown = ThemeList.ItemsSource as List<ThemeChoice> ?? [];
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

    private static void ThemesUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((ThemeGrid)sender)._order = null;
        Reset(sender, args);
    }

    private static void Reset(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var grid = (ThemeGrid)sender;
        grid._rows = grid._firstRows;
        grid.ApplyFilter();
    }

    private static void SelectionUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var grid = (ThemeGrid)sender;
        // A pick from another row can be past this row's cut; it shows in the last place.
        if (grid.SelectedTheme is { } theme && grid.ThemeList.ItemsSource is List<ThemeChoice> shown && !shown.Any(shownTheme => Same(shownTheme, theme)))
            grid.ApplyFilter();
        else
            grid.FollowSelection();
    }

    private void ApplyFilter()
    {
        if (_columns == 0)
            return; // laid out once the width is known
        var matches = ((IEnumerable<ThemeChoice>?)_order ?? Themes)?.Where(Query.Matches).ToList() ?? [];
        var limit = _columns * _rows;
        var shown = matches.Take(limit).ToList();
        if (SelectedTheme is { } selected && shown.Count == limit && !shown.Any(theme => Same(theme, selected))
            && matches.FirstOrDefault(theme => Same(theme, selected)) is { } match)
            shown[^1] = match;
        _updating = true;
        try
        {
            ThemeList.ItemsSource = shown;
            ThemeList.SelectedItem = MatchingSelection(shown);
        }
        finally { _updating = false; }
        MoreButton.Visibility = _canShowMore && matches.Count > limit ? Visibility.Visible : Visibility.Collapsed;
    }

    private ThemeChoice? MatchingSelection(List<ThemeChoice> shown) =>
        SelectedTheme is { } selected ? shown.FirstOrDefault(theme => Same(theme, selected)) : null;

    private static bool Same(ThemeChoice a, ThemeChoice b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private void FollowSelection()
    {
        _updating = true;
        try { ThemeList.SelectedItem = ThemeList.ItemsSource is List<ThemeChoice> shown ? MatchingSelection(shown) : null; }
        finally { _updating = false; }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs args)
    {
        _rows += MoreRows;
        ApplyFilter();
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
