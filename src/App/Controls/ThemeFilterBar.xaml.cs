using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>Search, light or dark, and category filters for theme galleries: hand its
/// <see cref="Query"/> to each gallery on <see cref="QueryChanged"/>.</summary>
public sealed partial class ThemeFilterBar : UserControl
{
    private const string OtherTag = "other";

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
        CategoryBar.Items.Add(Item(ThemeCategories.OtherName, OtherTag));
        ModeBar.SelectedItem = ModeBar.Items[0];
        CategoryBar.SelectedItem = CategoryBar.Items[0];
    }

    /// <summary>Adds the light/dark choice, for a gallery that lists both.</summary>
    public bool ShowModes
    {
        get => ModeBar.Visibility == Visibility.Visible;
        set
        {
            ModeBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value)
                ModeBar.SelectedItem = ModeBar.Items[0];
        }
    }

    public ThemeQuery Query => _query;

    public Control FocusTarget => SearchBox;

    public event EventHandler? QueryChanged;

    /// <summary>Selects the category tab for <paramref name="category"/>, the uncategorized
    /// themes' tab when <paramref name="uncategorized"/>, or All for neither.</summary>
    public void ShowCategory(ThemeCategory category, bool uncategorized)
    {
        object tag = uncategorized ? OtherTag : category;
        CategoryBar.SelectedItem = CategoryBar.Items.FirstOrDefault(item => Equals(item.Tag, tag)) ?? CategoryBar.Items[0];
    }

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
        var tag = CategoryBar.SelectedItem?.Tag;
        var query = new ThemeQuery(
            SearchBox.Text,
            tag is ThemeCategory category ? category : ThemeCategory.None,
            ModeBar.SelectedItem?.Tag is bool light ? light : null,
            Equals(tag, OtherTag));
        if (query == _query)
            return;
        _query = query;
        QueryChanged?.Invoke(this, EventArgs.Empty);
    }
}
