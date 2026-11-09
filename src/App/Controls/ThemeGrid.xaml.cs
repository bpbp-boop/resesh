using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>Themes in rows that wrap to the available width, a couple of rows at first and
/// more on request. It sizes to its content rather than scrolling, so it shows only what it
/// has realized: every card is built, none virtualized. The selected theme always shows,
/// in the last place, when it would otherwise be past the cut. Filtering never changes the
/// saved choice.</summary>
public sealed partial class ThemeGrid : UserControl
{
    public static readonly DependencyProperty ThemesProperty = DependencyProperty.Register(
        nameof(Themes), typeof(IReadOnlyList<ThemeChoice>), typeof(ThemeGrid), new PropertyMetadata(null, Reset));
    public static readonly DependencyProperty SelectedThemeProperty = DependencyProperty.Register(
        nameof(SelectedTheme), typeof(ThemeChoice), typeof(ThemeGrid), new PropertyMetadata(null, SelectionUpdated));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(ThemeQuery), typeof(ThemeGrid), new PropertyMetadata(ThemeQuery.All, Reset));

    private const int FirstRows = 2;
    private const int MoreRows = 4;
    // ThemeSwatch's width plus the container's right margin.
    private const double ItemStride = 296 + 10;

    private bool _updating;
    private int _rows = FirstRows;
    private int _columns;

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

    public Control FocusTarget => ThemeList;

    /// <summary>Only a user pick, not a source refresh or a filtered-out selection.</summary>
    public event EventHandler<ThemeChoice>? SelectionChanged;

    private static void Reset(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var grid = (ThemeGrid)sender;
        grid._rows = FirstRows;
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
        var matches = Themes?.Where(Query.Matches).ToList() ?? [];
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
        MoreButton.Visibility = matches.Count > limit ? Visibility.Visible : Visibility.Collapsed;
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
