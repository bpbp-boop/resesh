using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;
using Windows.System;

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
    private ScrollViewer? _scroller;
    // Where the last button press is scrolling to, so presses during the animation add up.
    private double? _pageTarget;

    public ThemeGallery()
    {
        InitializeComponent();
        Loaded += (_, _) => ScrollToSelection();
        // A gallery in a collapsed section gets its template only when first shown.
        ThemeList.SizeChanged += (_, _) => AttachScroller();
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
        _pageTarget = null;
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

    /// <summary>Finds the ListView's own ScrollViewer and the element just inside it, which
    /// sees a wheel event before the ScrollViewer does.</summary>
    private void AttachScroller()
    {
        if (_scroller is not null)
            return;
        UIElement? inner = null;
        for (var node = ThemeList.ItemsPanelRoot as DependencyObject; node is not null && node != ThemeList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller && inner is not null)
            {
                _scroller = scroller;
                inner.PointerWheelChanged += Scroller_PointerWheelChanged;
                scroller.ViewChanged += Scroller_ViewChanged;
                scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => UpdatePageButtons());
                UpdatePageButtons();
                return;
            }
            inner = node as UIElement;
        }
    }

    /// <summary>A ScrollViewer that can only scroll sideways turns a plain wheel turn into a
    /// sideways scroll, so the page stops scrolling whenever the pointer is over a row. Turning
    /// the row's horizontal scrolling off for this one event leaves it unhandled; it bubbles to
    /// the page's ScrollViewer, which scrolls with the system's wheel settings and animation.
    /// Shift or a tilt wheel still scroll the row, as does the wheel over its scroll bar.</summary>
    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (_scroller is not { HorizontalScrollMode: not ScrollMode.Disabled } scroller
            || args.KeyModifiers != VirtualKeyModifiers.None
            || args.GetCurrentPoint(scroller).Properties.IsHorizontalMouseWheel
            || IsInScrollBar(args.OriginalSource as DependencyObject, scroller))
            return;
        var mode = scroller.HorizontalScrollMode;
        scroller.HorizontalScrollMode = ScrollMode.Disabled;
        DispatcherQueue.TryEnqueue(() => scroller.HorizontalScrollMode = mode);
    }

    private static bool IsInScrollBar(DependencyObject? node, ScrollViewer scroller)
    {
        for (; node is not null && node != scroller; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollBar)
                return true;
        return false;
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate)
            _pageTarget = null;
        UpdatePageButtons();
    }

    private void UpdatePageButtons()
    {
        var offset = _scroller?.HorizontalOffset ?? 0;
        ScrollLeftButton.IsEnabled = offset > 0.5;
        ScrollRightButton.IsEnabled = _scroller is { } scroller && offset < scroller.ScrollableWidth - 0.5;
    }

    private void ScrollLeftButton_Click(object sender, RoutedEventArgs args) => Page(-1);

    private void ScrollRightButton_Click(object sender, RoutedEventArgs args) => Page(1);

    /// <summary>Scrolls by the swatches that fit fully in view, ending on a swatch's left edge.</summary>
    private void Page(int direction)
    {
        if (_scroller is not { } scroller
            || ThemeList.ItemsPanelRoot is not ItemsStackPanel panel
            || ThemeList.ContainerFromIndex(Math.Max(panel.FirstVisibleIndex, 0)) is not FrameworkElement item)
            return;
        var stride = item.ActualWidth + item.Margin.Left + item.Margin.Right;
        if (stride <= 0)
            return;
        var whole = Math.Max(1, Math.Floor((scroller.ViewportWidth + item.Margin.Right) / stride));
        var from = _pageTarget ?? scroller.HorizontalOffset;
        var target = Math.Clamp((Math.Round(from / stride) + direction * whole) * stride, 0, scroller.ScrollableWidth);
        _pageTarget = target;
        scroller.ChangeView(target, null, null);
    }

    private static void UpdateSwatch(SelectorItem container)
    {
        if (container.ContentTemplateRoot is ThemeSwatch swatch)
            swatch.IsSelected = container.IsSelected;
    }
}
