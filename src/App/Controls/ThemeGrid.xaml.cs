using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;
using Resesh.Terminal;

namespace Resesh.App.Controls;

/// <summary>Themes in rows that wrap to the available width: <see cref="Rows"/> of them, or
/// all of them when that is null. The list has no ScrollViewer of its own, so the page scrolls
/// over it and its ItemsWrapGrid builds only the cards near the page's viewport, however many
/// themes it holds. A grid out of view gets its themes once it comes into view, its height
/// held for them till then. The selected theme always shows, in the last place, when
/// it would otherwise be past the cut. Filtering and reordering never change the saved choice.</summary>
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
    // Every theme to show.
    private List<ThemeChoice> _shown = [];
    // Whether the list holds _shown. An ItemsWrapGrid given items before it is laid out in its
    // place takes the whole window as its view and builds every card that fits, so a grid gets
    // its themes only once it sits, at its reserved height, in the page's view.
    private bool _populated;
    private ScrollViewer? _scroller;
    // One row of cards with the gap below; every grid shows the same card.
    private static double s_rowHeight;

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
            else
                PopulateIfVisible();
        };
        EffectiveViewportChanged += (_, _) => PopulateIfVisible();
        ThemeList.SizeChanged += (_, _) =>
        {
            if (ThemeList.ContainerFromIndex(0) is FrameworkElement card && card.ActualHeight > 0)
                s_rowHeight = card.ActualHeight + card.Margin.Bottom;
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
        var shown = _shown;
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
        if (grid.SelectedTheme is { } theme && !grid._shown.Any(shownTheme => Same(shownTheme, theme)))
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
        _shown = shown;
        // An unfilled grid waits for the layout pass that puts it at its new height.
        if (_populated)
            Populate();
        else
            MinHeight = RowCount * RowHeight;
    }

    /// <summary>Gives an unfilled grid its themes once it is laid out in the page's view.</summary>
    private void PopulateIfVisible()
    {
        if (_populated || _columns == 0 || ActualHeight < MinHeight - 0.5)
            return;
        if (_scroller is null)
            for (var node = VisualTreeHelper.GetParent(this); node is not null && _scroller is null; node = VisualTreeHelper.GetParent(node))
                _scroller = node as ScrollViewer;
        if (_scroller is { } scroller)
        {
            var top = TransformToVisual(scroller).TransformPoint(default).Y;
            if (top > scroller.ViewportHeight || top + ActualHeight < 0)
                return;
        }
        Populate();
    }

    private void Populate()
    {
        _populated = true;
        MinHeight = 0;
        _updating = true;
        try
        {
            ThemeList.ItemsSource = _shown;
            ThemeList.SelectedItem = MatchingSelection(_shown);
        }
        finally { _updating = false; }
    }

    private int RowCount => (_shown.Count + _columns - 1) / _columns;

    /// <summary>A card measured off-screen, until a grid laid out with cards gives the real height.</summary>
    private static double RowHeight
    {
        get
        {
            if (s_rowHeight <= 0)
            {
                var card = new ThemeSwatch { Theme = ThemeCatalog.All[0] };
                card.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                s_rowHeight = card.DesiredSize.Height + 10;
            }
            return s_rowHeight;
        }
    }

    private ThemeChoice? MatchingSelection(IList<ThemeChoice> shown) =>
        SelectedTheme is { } selected ? shown.FirstOrDefault(theme => Same(theme, selected)) : null;

    private static bool Same(ThemeChoice a, ThemeChoice b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private void FollowSelection()
    {
        if (!_populated)
            return;
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
