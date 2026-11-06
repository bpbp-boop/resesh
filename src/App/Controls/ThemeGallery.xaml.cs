using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>A horizontal, virtualized palette picker. Filtering never changes the saved choice.</summary>
public sealed partial class ThemeGallery : UserControl
{
    public static readonly DependencyProperty ThemesProperty = DependencyProperty.Register(
        nameof(Themes), typeof(IReadOnlyList<ThemeChoice>), typeof(ThemeGallery), new PropertyMetadata(null, Refilter));
    public static readonly DependencyProperty SelectedThemeProperty = DependencyProperty.Register(
        nameof(SelectedTheme), typeof(ThemeChoice), typeof(ThemeGallery), new PropertyMetadata(null, SelectionUpdated));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(ThemeQuery), typeof(ThemeGallery), new PropertyMetadata(ThemeQuery.All, Refilter));

    private bool _updating;
    private List<ThemeChoice> _shown = [];

    public ThemeGallery()
    {
        InitializeComponent();
        Loaded += (_, _) => ScrollToSelection();
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

    /// <summary>Which of <see cref="Themes"/> to show; usually a <see cref="ThemeFilterBar"/>'s.</summary>
    public ThemeQuery Query
    {
        get => (ThemeQuery)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value ?? ThemeQuery.All);
    }

    public Control FocusTarget => ThemeList;

    /// <summary>Only a user pick, not a source refresh or a filtered-out selection.</summary>
    public event EventHandler<ThemeChoice>? SelectionChanged;

    private static void Refilter(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ThemeGallery)sender).ApplyFilter();

    private static void SelectionUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ThemeGallery)sender).FollowSelection();

    private void ApplyFilter()
    {
        _shown = Themes?.Where(Query.Matches).ToList() ?? [];
        _updating = true;
        try
        {
            ThemeList.ItemsSource = _shown;
            ThemeList.SelectedItem = MatchingSelection();
        }
        finally { _updating = false; }
        NoMatchText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollToSelection();
    }

    private ThemeChoice? MatchingSelection() =>
        _shown.FirstOrDefault(theme => string.Equals(theme.Id, SelectedTheme?.Id, StringComparison.OrdinalIgnoreCase));

    private void FollowSelection()
    {
        _updating = true;
        try { ThemeList.SelectedItem = MatchingSelection(); }
        finally { _updating = false; }
        ScrollToSelection();
    }

    public void ScrollToSelection()
    {
        if (IsLoaded && ThemeList.SelectedItem is { } selected)
            ThemeList.ScrollIntoView(selected, ScrollIntoViewAlignment.Default);
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
