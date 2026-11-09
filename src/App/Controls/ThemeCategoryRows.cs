using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;
using Resesh.Terminal;

namespace Resesh.App.Controls;

/// <summary>Themes in a section per category, each its own <see cref="ThemeGrid"/>, then
/// "Other" for themes in no category. With <see cref="Query"/> on all categories every
/// section shows a couple of rows and "See all"; with it on one category only that section
/// shows, every theme of it, with "Show less". The page owns the category filter, so both
/// links ask it through <see cref="CategoryRequested"/>. Each section can be sorted A to Z or
/// shuffled, and keeps that order in both views. A theme in several categories shows in
/// each section, selected in all of them. Sections with nothing matching hide.</summary>
public sealed partial class ThemeCategoryRows : UserControl
{
    public static readonly DependencyProperty ThemesProperty = DependencyProperty.Register(
        nameof(Themes), typeof(IReadOnlyList<ThemeChoice>), typeof(ThemeCategoryRows), new PropertyMetadata(null, Rebuild));
    public static readonly DependencyProperty SelectedThemeProperty = DependencyProperty.Register(
        nameof(SelectedTheme), typeof(ThemeChoice), typeof(ThemeCategoryRows), new PropertyMetadata(null, SelectionUpdated));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(ThemeQuery), typeof(ThemeCategoryRows), new PropertyMetadata(ThemeQuery.All, Refilter));

    private const int OverviewRows = 2;

    // A die showing five: a rounded outline and its pips, filled even-odd so the face is hollow.
    private const string DiePath =
        "F0 M3.5,1 H12.5 A2.5,2.5 0 0 1 15,3.5 V12.5 A2.5,2.5 0 0 1 12.5,15 H3.5 A2.5,2.5 0 0 1 1,12.5 V3.5 A2.5,2.5 0 0 1 3.5,1 Z"
        + " M3.5,2.25 H12.5 A1.25,1.25 0 0 1 13.75,3.5 V12.5 A1.25,1.25 0 0 1 12.5,13.75 H3.5 A1.25,1.25 0 0 1 2.25,12.5 V3.5 A1.25,1.25 0 0 1 3.5,2.25 Z"
        + " M4.25,5.25 A1.25,1.25 0 1 1 6.75,5.25 A1.25,1.25 0 1 1 4.25,5.25 Z"
        + " M9.25,5.25 A1.25,1.25 0 1 1 11.75,5.25 A1.25,1.25 0 1 1 9.25,5.25 Z"
        + " M6.75,8 A1.25,1.25 0 1 1 9.25,8 A1.25,1.25 0 1 1 6.75,8 Z"
        + " M4.25,10.75 A1.25,1.25 0 1 1 6.75,10.75 A1.25,1.25 0 1 1 4.25,10.75 Z"
        + " M9.25,10.75 A1.25,1.25 0 1 1 11.75,10.75 A1.25,1.25 0 1 1 9.25,10.75 Z";

    private sealed record Section(
        ThemeCategory Category, bool Other, FrameworkElement Header, TextBlock Count,
        HyperlinkButton Toggle, ThemeGrid Grid, List<ThemeChoice> Themes)
    {
        public bool Shows(ThemeQuery query) =>
            Other ? query.Uncategorized : query.Category == Category && !query.Uncategorized;
    }

    private readonly StackPanel _panel = new();
    private readonly TextBlock _noMatch = new()
    {
        Text = "No themes match.",
        Margin = new Thickness(1, 12, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly List<Section> _sections = [];
    // The section whose link was clicked, brought into view once the other filter applies.
    private Section? _following;

    public ThemeCategoryRows()
    {
        _noMatch.Foreground = (Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"];
        Content = _panel;
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

    /// <summary>Name, light/dark and category filter; usually a <see cref="ThemeFilterBar"/>'s.</summary>
    public ThemeQuery Query
    {
        get => (ThemeQuery)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value ?? ThemeQuery.All);
    }

    /// <summary>The first visible section's list, for keyboard navigation from Settings.</summary>
    public Control FocusTarget =>
        _sections.FirstOrDefault(section => section.Header.Visibility == Visibility.Visible)?.Grid.FocusTarget ?? this;

    /// <summary>"See all" asks for one category (or, with the flag, the uncategorized themes);
    /// "Show less" asks for all of them, as <see cref="ThemeCategory.None"/> and false.</summary>
    public event Action<ThemeCategory, bool>? CategoryRequested;

    private static void Rebuild(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ThemeCategoryRows)sender).Build();

    private static void SelectionUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var rows = (ThemeCategoryRows)sender;
        foreach (var section in rows._sections)
            section.Grid.SelectedTheme = rows.SelectedTheme;
    }

    private static void Refilter(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ThemeCategoryRows)sender).ApplyQuery();

    private void Build()
    {
        _panel.Children.Clear();
        _sections.Clear();
        var themes = Themes ?? [];
        foreach (var (category, name) in ThemeCategories.All)
            AddSection(category, false, name, [.. themes.Where(theme => theme.Categories.HasFlag(category))]);
        AddSection(ThemeCategory.None, true, ThemeCategories.OtherName, [.. themes.Where(theme => theme.Categories == ThemeCategory.None)]);
        _panel.Children.Add(_noMatch);
        ApplyQuery();
    }

    private void AddSection(ThemeCategory category, bool other, string name, List<ThemeChoice> themes)
    {
        if (themes.Count == 0)
            return;
        var muted = (Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"];
        var title = new TextBlock { Text = name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        var count = new TextBlock { Foreground = muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(title);
        heading.Children.Add(count);

        var sort = HeaderButton($"Sort {name} themes A to Z", new TextBlock
        {
            Text = "A–Z",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Height = 16,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        });
        var shuffle = HeaderButton($"Shuffle {name} themes", new Viewbox
        {
            Width = 16,
            Height = 16,
            Child = new PathIcon { Data = PresentationValues.IconPathGeometry(DiePath) },
        });
        var toggle = new HyperlinkButton();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(sort);
        actions.Children.Add(shuffle);
        actions.Children.Add(toggle);

        // As wide as the cards below, so the links end over the last column.
        var header = new Grid { Margin = new Thickness(1, 24, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actions, 1);
        header.Children.Add(heading);
        header.Children.Add(actions);

        var grid = new ThemeGrid { Themes = themes, SelectedTheme = SelectedTheme, Rows = OverviewRows };
        AutomationProperties.SetName(grid, $"{name} themes");
        AutomationProperties.SetAutomationId(grid, "ThemeRow" + string.Concat(name.Where(char.IsLetter)));
        grid.SelectionChanged += (_, theme) => SelectedTheme = theme;
        grid.ColumnsChanged += (_, _) => header.Width = grid.RowWidth - 1;

        var section = new Section(category, other, header, count, toggle, grid, themes);
        sort.Click += (_, _) => grid.SortByName();
        shuffle.Click += (_, _) => grid.Shuffle();
        toggle.Click += (_, _) =>
        {
            _following = section;
            if (section.Shows(Query))
                CategoryRequested?.Invoke(ThemeCategory.None, false);
            else
                CategoryRequested?.Invoke(category, other);
        };
        _panel.Children.Add(header);
        _panel.Children.Add(grid);
        _sections.Add(section);
    }

    /// <summary>A quiet icon button for a section header, named by its tooltip.</summary>
    private static Button HeaderButton(string name, UIElement icon)
    {
        var button = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            Content = icon,
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private void ApplyQuery()
    {
        var oneCategory = Query.IsOneCategory;
        // Each grid holds one category's themes already; it filters by name and light only.
        var gridQuery = Query with { Category = ThemeCategory.None, Uncategorized = false };
        var anyShown = false;
        foreach (var section in _sections)
        {
            var focused = oneCategory && section.Shows(Query);
            var matching = section.Themes.Count(gridQuery.Matches);
            var visible = (!oneCategory || focused) && matching > 0;
            anyShown |= visible;
            section.Header.Visibility = section.Grid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            section.Count.Text = matching.ToString("N0");
            section.Toggle.Content = focused ? "Show less" : "See all";
            section.Grid.Rows = focused ? null : OverviewRows;
            section.Grid.Query = gridQuery;
        }
        _noMatch.Visibility = anyShown ? Visibility.Collapsed : Visibility.Visible;

        if (_following is { } following)
        {
            _following = null;
            // After the layout pass, so the section is where the new filter puts it.
            DispatcherQueue.TryPost(DispatcherQueuePriority.Low, () =>
            {
                if (following.Header.Visibility == Visibility.Visible)
                    following.Header.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
            });
        }
    }
}
