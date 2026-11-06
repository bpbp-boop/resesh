using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>Themes laid out one category per row, each row its own <see cref="ThemeGallery"/>,
/// then "Other" for themes in no category. A theme in several categories shows in each row,
/// selected in all of them. Rows with nothing matching <see cref="Query"/> hide.</summary>
public sealed partial class ThemeCategoryRows : UserControl
{
    public static readonly DependencyProperty ThemesProperty = DependencyProperty.Register(
        nameof(Themes), typeof(IReadOnlyList<ThemeChoice>), typeof(ThemeCategoryRows), new PropertyMetadata(null, Rebuild));
    public static readonly DependencyProperty SelectedThemeProperty = DependencyProperty.Register(
        nameof(SelectedTheme), typeof(ThemeChoice), typeof(ThemeCategoryRows), new PropertyMetadata(null, SelectionUpdated));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(ThemeQuery), typeof(ThemeCategoryRows), new PropertyMetadata(ThemeQuery.All, Refilter));

    private readonly StackPanel _panel = new();
    private readonly List<(FrameworkElement Header, ThemeGallery Gallery, List<ThemeChoice> Themes)> _rows = [];

    public ThemeCategoryRows() => Content = _panel;

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

    /// <summary>Name and light/dark filter; the rows are the categories.</summary>
    public ThemeQuery Query
    {
        get => (ThemeQuery)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value ?? ThemeQuery.All);
    }

    /// <summary>The first visible row's list, for keyboard navigation from Settings.</summary>
    public Control FocusTarget =>
        _rows.FirstOrDefault(row => row.Header.Visibility == Visibility.Visible).Gallery?.FocusTarget ?? this;

    public void ScrollToSelection()
    {
        foreach (var row in _rows)
            row.Gallery.ScrollToSelection();
    }

    private static void Rebuild(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ThemeCategoryRows)sender).Build();

    private static void SelectionUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var rows = (ThemeCategoryRows)sender;
        foreach (var row in rows._rows)
            row.Gallery.SelectedTheme = rows.SelectedTheme;
    }

    private static void Refilter(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ThemeCategoryRows)sender).ApplyQuery();

    private void Build()
    {
        _panel.Children.Clear();
        _rows.Clear();
        var themes = Themes ?? [];
        foreach (var (category, name) in ThemeCategories.All)
            AddRow(name, [.. themes.Where(theme => theme.Categories.HasFlag(category))]);
        AddRow("Other", [.. themes.Where(theme => theme.Categories == ThemeCategory.None)]);
        ApplyQuery();
    }

    private void AddRow(string name, List<ThemeChoice> themes)
    {
        if (themes.Count == 0)
            return;
        var header = new TextBlock
        {
            Text = name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(1, 16, 0, 4),
        };
        var gallery = new ThemeGallery { Themes = themes, SelectedTheme = SelectedTheme };
        AutomationProperties.SetName(gallery, $"{name} themes");
        AutomationProperties.SetAutomationId(gallery, "ThemeRow" + string.Concat(name.Where(char.IsLetter)));
        gallery.SelectionChanged += (_, theme) => SelectedTheme = theme;
        _panel.Children.Add(header);
        _panel.Children.Add(gallery);
        _rows.Add((header, gallery, themes));
    }

    private void ApplyQuery()
    {
        foreach (var (header, gallery, themes) in _rows)
        {
            gallery.Query = Query;
            header.Visibility = gallery.Visibility = themes.Any(Query.Matches) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
