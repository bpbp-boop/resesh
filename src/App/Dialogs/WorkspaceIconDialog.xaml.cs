using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Controls;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>The icon a workspace chose (null means its initials), its own initials (null means
/// the ones from its name), and whether the initials label the icon.</summary>
public sealed record WorkspaceIconSelection(string? Icon, string? Initials, bool LabelIcon);

/// <summary>Chooses the icon on a workspace's rail button from the workspace icon set, by
/// section, or its initials, which can be the workspace's own.</summary>
public sealed partial class WorkspaceIconDialog : ContentDialog
{
    private const double PreviewBrandSize = 24;

    private readonly string _nameInitials;
    private IconPickerChoice _initials;
    private bool _committed;

    public WorkspaceIconDialog(Workspace workspace)
    {
        InitializeComponent();
        PreviewName.Text = workspace.Name;
        _nameInitials = WorkspaceAppearance.Monogram(workspace.Name);
        InitialsBox.PlaceholderText = _nameInitials;
        InitialsBox.Text = WorkspaceAppearance.NormalizeInitials(workspace.Initials) ?? "";
        LabelIconBox.IsChecked = workspace.LabelIcon;

        _initials = InitialsChoice();
        var choices = new List<IconPickerChoice> { _initials };
        choices.AddRange(WorkspaceAppearance.Icons.Select(icon => new IconPickerChoice(
            icon.Key,
            icon.Name,
            icon.Group,
            icon.Keywords,
            glyph: icon.Glyph,
            path: icon.Path,
            brandKey: icon.IsBrand ? icon.Key : "")));
        // A key this build does not know draws the initials, so the picker starts there too.
        var current = WorkspaceAppearance.Find(workspace.Icon)?.Key;
        Picker.SetChoices(choices, choices.First(choice => choice.Key == current));
        Picker.Loaded += (_, _) => ShowPreview();
        ShowPreview();
    }

    private string? CustomInitials => WorkspaceAppearance.NormalizeInitials(InitialsBox.Text);

    private string CurrentInitials => CustomInitials ?? _nameInitials;

    private IconPickerChoice Selected => Picker.Selected ?? _initials;

    private IconPickerChoice InitialsChoice() =>
        new(null, "Initials", "Initials", "letters monogram", text: CurrentInitials);

    /// <summary>Shows the dialog; the choice on Save, or null when cancelled.</summary>
    public static async Task<WorkspaceIconSelection?> EditAsync(XamlRoot xamlRoot, Workspace workspace)
    {
        var dialog = new WorkspaceIconDialog(workspace) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary || dialog._committed
            ? new WorkspaceIconSelection(dialog.Selected.Key, dialog.CustomInitials, dialog.LabelIconBox.IsChecked == true)
            : null;
    }

    private void Picker_SelectionChanged(object? sender, IconPickerChoice choice) => ShowPreview();

    /// <summary>Enter in the filter saves the best match, so typing "mysql" and Enter picks MySQL.</summary>
    private void Picker_Committed(object? sender, IconPickerChoice choice)
    {
        _committed = true;
        Hide();
    }

    /// <summary>Typed initials show at once in the Initials cell and the preview. They pick
    /// Initials unless they label the chosen icon, since otherwise nothing would draw them.</summary>
    private void InitialsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initials is null)
            return; // setting the saved initials during construction
        var replacement = InitialsChoice();
        Picker.Replace(_initials, replacement);
        _initials = replacement;
        if (Selected.Key is not null && CustomInitials is not null && LabelIconBox.IsChecked != true)
            Picker.Select(_initials);
        ShowPreview();
    }

    private void LabelIconBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initials is not null)
            ShowPreview();
    }

    private void ShowPreview()
    {
        var selected = Selected;
        var isIcon = selected.Key is not null;
        PreviewGlyph.Glyph = selected.Glyph;
        PreviewPath.Data = PresentationValues.IconPathGeometry(selected.Path);
        PreviewBrand.Source = selected.BrandKey.Length > 0 && Picker.IsLoaded
            ? App.Brands.Get(selected.BrandKey, PreviewBrandSize, Picker.MarkColor)
            : null;
        PreviewMonogram.Text = CurrentInitials;
        PreviewMonogram.Visibility = PresentationValues.Visible(!isIcon);
        PreviewIcon.Visibility = PresentationValues.Visible(isIcon);
        PreviewCaption.Text = CurrentInitials;
        PreviewCaption.Visibility = PresentationValues.Visible(LabelIconBox.IsChecked == true);
        // The label only applies to an icon; with Initials chosen they are the button already.
        LabelIconBox.IsEnabled = isIcon;
        PreviewIconName.Text = selected.Name;
    }
}
