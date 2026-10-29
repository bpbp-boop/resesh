using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Models;
using Windows.System;
using Windows.UI;

namespace Resesh.App.Controls;

/// <summary>One cell in an <see cref="IconPicker"/>. It draws exactly one of: a font glyph, a
/// 16×16 path, a built-in one-colour mark (<see cref="BrandKey"/>), a custom image, or text.
/// The key is what the caller saves; null and other sentinels are the caller's to define.</summary>
public sealed class IconPickerChoice(
    string? key,
    string name,
    string group,
    string keywords = "",
    string glyph = "",
    string path = "",
    string brandKey = "",
    ImageSource? customImage = null,
    string text = "")
{
    public string? Key { get; } = key;
    public string Name { get; } = name;
    public string Group { get; } = group;
    public string Keywords { get; } = keywords;
    public string Glyph { get; } = glyph;
    public string Path { get; } = path;
    public string BrandKey { get; } = brandKey;
    public string Text { get; } = text;
    public ImageSource? CustomImage { get; } = customImage;

    /// <summary>The image the cell draws: the custom image, or the brand mark the picker
    /// renders in its theme's colour.</summary>
    public ImageSource? Image { get; internal set; } = customImage;

    public bool Matches(string filter) => SessionIcons.Matches(filter, Name, Key ?? "", Keywords);

    public int MatchRank(string filter) => SessionIcons.MatchRank(filter, Name, Key ?? "");
}

/// <summary>A titled section of the picker. Being a list lets the grid group by it.</summary>
public sealed partial class IconPickerGroup(string name, IEnumerable<IconPickerChoice> choices)
    : List<IconPickerChoice>(choices)
{
    public string Name { get; } = name;
}

/// <summary>
/// The icon picker shared by the workspace and session editors: a filter over icons grouped
/// in titled sections, in catalog order. Typing narrows by name and search words, and Enter
/// takes the best match (the icon the text names before one that only lists it as a search
/// word). The choice survives being filtered out. Built-in marks are drawn sharp in the
/// theme's text colour, and redrawn when the theme changes.
/// </summary>
public sealed partial class IconPicker : UserControl
{
    private const double BrandSize = 24;

    private readonly CollectionViewSource _groups = new() { IsSourceGrouped = true };
    private List<IconPickerChoice> _choices = [];
    private bool _refilling;

    public IconPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => DrawBrandMarks();
        ActualThemeChanged += (_, _) => DrawBrandMarks();
    }

    /// <summary>The user picked a cell.</summary>
    public event EventHandler<IconPickerChoice>? SelectionChanged;

    /// <summary>The user pressed Enter in the filter; the best match is now selected.</summary>
    public event EventHandler<IconPickerChoice>? Committed;

    public IconPickerChoice? Selected { get; private set; }

    /// <summary>A control shown beside the filter, such as an option for the chosen icon.</summary>
    public object? HeaderContent
    {
        get => HeaderHost.Content;
        set => HeaderHost.Content = value;
    }

    /// <summary>The colour built-in marks are drawn in: the theme's primary text colour.</summary>
    public Color MarkColor => ActualTheme == ElementTheme.Light
        ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)
        : Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

    public void SetChoices(IReadOnlyList<IconPickerChoice> choices, IconPickerChoice? selected)
    {
        _choices = [.. choices];
        Selected = selected;
        if (IsLoaded)
            DrawBrandMarks();
        else
            ApplyFilter(FilterBox.Text);
    }

    /// <summary>Swaps one cell for another (an Initials cell whose letters changed, say),
    /// keeping its place and, when it was chosen, the choice.</summary>
    public void Replace(IconPickerChoice old, IconPickerChoice replacement)
    {
        var index = _choices.IndexOf(old);
        if (index < 0)
            return;
        replacement.Image ??= old.Image;
        _choices[index] = replacement;
        if (ReferenceEquals(Selected, old))
            Selected = replacement;
        ApplyFilter(FilterBox.Text);
    }

    /// <summary>Chooses a cell from code, without raising <see cref="SelectionChanged"/>.</summary>
    public void Select(IconPickerChoice choice)
    {
        Selected = choice;
        ApplyFilter(FilterBox.Text);
    }

    private void DrawBrandMarks()
    {
        var color = MarkColor;
        foreach (var choice in _choices.Where(choice => choice.BrandKey.Length > 0))
            choice.Image = App.Brands.Get(choice.BrandKey, BrandSize, color);
        ApplyFilter(FilterBox.Text);
    }

    private void ApplyFilter(string filter)
    {
        var matches = _choices.Where(choice => choice.Matches(filter)).ToList();
        _refilling = true;
        _groups.Source = matches
            .GroupBy(choice => choice.Group)
            .Select(group => new IconPickerGroup(group.Key, group))
            .ToList();
        // A new source makes a new view, so the grid takes it each time.
        IconGrid.ItemsSource = _groups.View;
        IconGrid.SelectedItem = Selected is not null && matches.Contains(Selected) ? Selected : null;
        _refilling = false;
        NoMatchText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(FilterBox.Text);

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        var best = _choices
            .Where(choice => choice.Matches(FilterBox.Text))
            .OrderBy(choice => choice.MatchRank(FilterBox.Text))
            .FirstOrDefault();
        if (best is null)
            return;
        e.Handled = true;
        Selected = best;
        ApplyFilter(FilterBox.Text);
        Committed?.Invoke(this, best);
    }

    private void IconGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refilling || IconGrid.SelectedItem is not IconPickerChoice choice)
            return;
        Selected = choice;
        SelectionChanged?.Invoke(this, choice);
    }
}
