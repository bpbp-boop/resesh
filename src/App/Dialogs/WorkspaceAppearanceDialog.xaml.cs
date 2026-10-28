using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Icons;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>The icon and colour a workspace chose; null for either means the default.</summary>
public sealed record WorkspaceAppearanceChoice(string? Icon, string? ColorTag);

/// <summary>Chooses how a workspace's rail button looks: a session icon or its initials,
/// on an automatic or chosen colour.</summary>
public sealed partial class WorkspaceAppearanceDialog : ContentDialog
{
    private readonly Workspace _workspace;
    private string? _selectedIcon;

    public WorkspaceAppearanceDialog(Workspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        PreviewName.Text = workspace.Name;
        PreviewMonogram.Text = WorkspaceAppearance.Monogram(workspace.Name);

        ColorBox.Items.Add("Automatic");
        foreach (var (name, _) in WorkspaceAppearance.Palette)
            ColorBox.Items.Add(name);
        var colorIndex = WorkspaceAppearance.Palette.ToList()
            .FindIndex(entry => string.Equals(entry.Hex, workspace.ColorTag, StringComparison.OrdinalIgnoreCase));
        ColorBox.SelectedIndex = colorIndex + 1;

        PopulateIconPicker(workspace.Icon);
    }

    private string? SelectedColor =>
        ColorBox.SelectedIndex > 0 ? WorkspaceAppearance.Palette[ColorBox.SelectedIndex - 1].Hex : null;

    /// <summary>Shows the dialog; the choice on Save, or null when cancelled.</summary>
    public static async Task<WorkspaceAppearanceChoice?> EditAsync(XamlRoot xamlRoot, Workspace workspace)
    {
        var dialog = new WorkspaceAppearanceDialog(workspace) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary
            ? new WorkspaceAppearanceChoice(dialog._selectedIcon, dialog.SelectedColor)
            : null;
    }

    private void PopulateIconPicker(string? currentKey)
    {
        // A workspace has no server to suggest from and nothing to hide: "no icon" is the
        // initials, which take the picker's first place.
        var entries = App.Icons.PickerEntries()
            .Where(entry => entry.Key is not null && entry.Key != SessionIcons.None)
            .Prepend(new IconChoice(null, "Initials", null, ""))
            .ToList();
        if (!string.IsNullOrEmpty(currentKey)
            && !entries.Any(entry => string.Equals(entry.Key, currentKey, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new IconChoice(currentKey, $"{currentKey} (missing)", null, ""));
        }
        IconGrid.ItemsSource = entries;
        _selectedIcon = string.IsNullOrEmpty(currentKey) ? null : currentKey;
        UpdateIconButton(entries.First(entry => string.Equals(entry.Key, _selectedIcon, StringComparison.OrdinalIgnoreCase)));
    }

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        var choice = (IconChoice)e.ClickedItem;
        _selectedIcon = choice.Key;
        UpdateIconButton(choice);
        IconFlyout.Hide();
    }

    private void UpdateIconButton(IconChoice choice)
    {
        var image = App.Icons.GetImage(choice.Key, SessionIconCatalog.ListIconSize);
        IconButtonImage.Source = image;
        IconButtonImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        IconButtonText.Text = choice.Name;
        UpdatePreview();
    }

    private void ColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewTile is null)
            return;
        var iconKey = _selectedIcon ?? "";
        var colorTag = WorkspaceAppearance.Color(_workspace with { ColorTag = SelectedColor });
        PreviewTile.Background = PresentationValues.WorkspaceTileBrush(colorTag, iconKey);
        PreviewMonogram.Foreground = PresentationValues.WorkspaceTileForeground(colorTag);
        PreviewMonogram.Visibility = PresentationValues.WorkspaceMonogramVisibility(iconKey, isHome: false);
        PreviewIcon.Source = App.Icons.GetImage(_selectedIcon, 24);
        PreviewIcon.Visibility = PresentationValues.WorkspaceIconVisibility(iconKey);
    }
}
