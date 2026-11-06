using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>Search and category filters for <see cref="ThemeGallery"/>s: hand its
/// <see cref="Query"/> to each gallery on <see cref="QueryChanged"/>.</summary>
public sealed partial class ThemeFilterBar : UserControl
{
    private ThemeQuery _query = ThemeQuery.All;

    public ThemeFilterBar()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = $"Search {ThemeCatalog.All.Count:N0} themes";
        foreach (var (name, light) in new (string, bool?)[] { ("Light & dark", null), ("Light", true), ("Dark", false) })
            ModeBar.Items.Add(Item(name, light));
        CategoryBar.Items.Add(Item("All", ThemeCategory.None));
        foreach (var (category, name) in ThemeCategories.All)
            CategoryBar.Items.Add(Item(name, category));
        ModeBar.SelectedItem = ModeBar.Items[0];
        CategoryBar.SelectedItem = CategoryBar.Items[0];
    }

    /// <summary>Adds the light/dark choice, for a gallery that lists both.</summary>
    public bool ShowModes
    {
        get => ModeBar.Visibility == Visibility.Visible;
        set
        {
            ModeBar.Visibility = ModeDivider.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value)
                ModeBar.SelectedItem = ModeBar.Items[0];
        }
    }

    /// <summary>False where the categories are laid out as rows instead (Settings).</summary>
    public bool ShowCategories
    {
        get => CategoryBar.Visibility == Visibility.Visible;
        set
        {
            CategoryBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ModeDivider.Opacity = value ? 1 : 0;
            if (!value)
                CategoryBar.SelectedItem = CategoryBar.Items[0];
        }
    }

    public ThemeQuery Query => _query;

    public Control FocusTarget => SearchBox;

    public event EventHandler? QueryChanged;

    private static SelectorBarItem Item(string text, object tag)
    {
        var item = new SelectorBarItem { Text = text, Tag = tag };
        AutomationProperties.SetAutomationId(item, "ThemeFilter" + string.Concat(text.Where(char.IsLetter)));
        return item;
    }

    private static SelectorBarItem Item(string text, bool? light) => Item(text, (object?)light ?? "any");

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args) => Update();

    private void Bar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Update();

    private void Update()
    {
        var query = new ThemeQuery(
            SearchBox.Text,
            CategoryBar.SelectedItem?.Tag is ThemeCategory category ? category : ThemeCategory.None,
            ModeBar.SelectedItem?.Tag is bool light ? light : null);
        if (query == _query)
            return;
        _query = query;
        QueryChanged?.Invoke(this, EventArgs.Empty);
    }
}
