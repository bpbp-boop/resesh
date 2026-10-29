using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Resesh.Core.Storage;
using Windows.System;

namespace Resesh.App.Dialogs;

/// <summary>The icon a workspace chose (null means its initials) and its own initials (null
/// means the ones from its name).</summary>
public sealed record WorkspaceIconSelection(string? Icon, string? Initials);

/// <summary>One cell in the picker: a catalog icon, or the workspace's initials as text.</summary>
public sealed class WorkspaceIconChoice(WorkspaceIcon? icon, string initials)
{
    public WorkspaceIcon? Icon { get; } = icon;
    public string? Key => Icon?.Key;
    public string Name => Icon?.Name ?? "Initials";
    public string Glyph => Icon?.Glyph ?? "";
    public string Path => Icon?.Path ?? "";
    public string Text => Icon is null ? initials : "";

    public bool Matches(string filter) =>
        Icon?.Matches(filter) ?? (string.IsNullOrWhiteSpace(filter) || Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Chooses the icon on a workspace's rail button from the workspace icon set, or its
/// initials, which can be the workspace's own. A filter narrows the set by name and keywords.</summary>
public sealed partial class WorkspaceIconDialog : ContentDialog
{
    private readonly string _nameInitials;
    private readonly List<WorkspaceIconChoice> _choices;
    private WorkspaceIconChoice _selected;
    private bool _refilling;
    private bool _committed;

    public WorkspaceIconDialog(Workspace workspace)
    {
        InitializeComponent();
        PreviewName.Text = workspace.Name;
        _nameInitials = WorkspaceAppearance.Monogram(workspace.Name);
        InitialsBox.PlaceholderText = _nameInitials;
        InitialsBox.Text = WorkspaceAppearance.NormalizeInitials(workspace.Initials) ?? "";

        _choices =
        [
            new WorkspaceIconChoice(null, CurrentInitials),
            .. WorkspaceAppearance.Icons.Select(icon => new WorkspaceIconChoice(icon, "")),
        ];
        // A key this build does not know draws the initials, so the picker starts there too.
        var current = WorkspaceAppearance.Find(workspace.Icon);
        _selected = _choices.First(choice => choice.Icon == current);
        ApplyFilter("");
        ShowPreview();
    }

    private string? CustomInitials => WorkspaceAppearance.NormalizeInitials(InitialsBox.Text);

    private string CurrentInitials => CustomInitials ?? _nameInitials;

    /// <summary>Shows the dialog; the choice on Save, or null when cancelled.</summary>
    public static async Task<WorkspaceIconSelection?> EditAsync(XamlRoot xamlRoot, Workspace workspace)
    {
        var dialog = new WorkspaceIconDialog(workspace) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary || dialog._committed
            ? new WorkspaceIconSelection(dialog._selected.Key, dialog.CustomInitials)
            : null;
    }

    /// <summary>Shows the matching icons. The choice survives being filtered out, so typing a
    /// filter and clearing it again never loses it.</summary>
    private void ApplyFilter(string filter)
    {
        var matches = _choices.Where(choice => choice.Matches(filter)).ToList();
        _refilling = true;
        IconGrid.ItemsSource = matches;
        IconGrid.SelectedItem = matches.Contains(_selected) ? _selected : null;
        _refilling = false;
        NoMatchText.Visibility = PresentationValues.Visible(matches.Count == 0);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(FilterBox.Text);

    /// <summary>Enter saves the first match, so typing "sql" and Enter picks Database.</summary>
    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || IconGrid.Items.FirstOrDefault() is not WorkspaceIconChoice first)
            return;
        _selected = first;
        _committed = true;
        e.Handled = true;
        Hide();
    }

    /// <summary>Typed initials show at once in the Initials cell and the preview, and pick
    /// Initials, since that is the only choice that draws them.</summary>
    private void InitialsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_choices is null)
            return; // setting the saved initials during construction
        _choices[0] = new WorkspaceIconChoice(null, CurrentInitials);
        if (_selected.Icon is null || CustomInitials is not null)
            _selected = _choices[0];
        ApplyFilter(FilterBox.Text);
        ShowPreview();
    }

    private void IconGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refilling || IconGrid.SelectedItem is not WorkspaceIconChoice choice)
            return;
        _selected = choice;
        ShowPreview();
    }

    private void ShowPreview()
    {
        PreviewGlyph.Glyph = _selected.Glyph;
        PreviewPath.Data = PresentationValues.IconPathGeometry(_selected.Path);
        PreviewMonogram.Text = CurrentInitials;
        PreviewMonogram.Visibility = PresentationValues.Visible(_selected.Icon is null);
        PreviewIconName.Text = _selected.Name;
    }
}
