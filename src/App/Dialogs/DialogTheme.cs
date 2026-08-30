using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>
/// Repaints a <see cref="ContentDialog"/> with the live session palette. Dialogs open in
/// their own popup root and resolve Fluent's stock brushes, so without this they stay the
/// system grey while the window behind them follows the theme. Every value is one of the
/// app's mutable brush instances, so a theme change reaches an already-open dialog.
/// </summary>
internal static class DialogTheme
{
    public static void Apply(ContentDialog dialog, string? theme = null)
    {
        SetRequestedTheme(dialog, theme ?? App.Settings.Current.Theme);
        var shell = Brush("SessionShellBrush");
        var input = Brush("SessionInputBrush");
        var frame = Brush("SessionChromeFrameBrush");
        var text = Brush("SessionTreeForegroundBrush");
        var muted = Brush("SessionTreeMutedForegroundBrush");
        var selection = Brush("SessionTreeSelectionBrush");
        var selectionText = Brush("SessionTreeSelectionForegroundBrush");
        var accent = Brush("SessionAccentBrush");

        // The dialog surface, and the flyouts and drop-downs it opens.
        Set(dialog, shell,
            "ContentDialogBackground",
            "ComboBoxDropDownBackground",
            "FlyoutPresenterBackground");
        Set(dialog, frame,
            "ContentDialogBorderBrush",
            "ContentDialogSeparatorBorderBrush",
            "ComboBoxDropDownBorderBrush",
            "FlyoutBorderThemeBrush");
        Set(dialog, text,
            "ContentDialogForeground",
            "ComboBoxDropDownForeground",
            "TextFillColorPrimaryBrush");
        // The captions under each field ask for the secondary text color by name.
        Set(dialog, muted, "TextFillColorSecondaryBrush");

        // Entry fields sit on the recessed surface, exactly like the sidebar's filter box.
        Set(dialog, input,
            "TextControlBackground",
            "TextControlBackgroundPointerOver",
            "TextControlBackgroundFocused",
            "TextControlBackgroundDisabled",
            "ComboBoxBackground",
            "ComboBoxBackgroundPointerOver",
            "ComboBoxBackgroundPressed",
            "ComboBoxBackgroundFocused",
            "ComboBoxBackgroundUnfocused",
            "ButtonBackground",
            "ButtonBackgroundPointerOver",
            "ButtonBackgroundPressed");
        Set(dialog, frame,
            "TextControlBorderBrush",
            "TextControlBorderBrushPointerOver",
            "TextControlBorderBrushDisabled",
            "ComboBoxBorderBrush",
            "ComboBoxBorderBrushPointerOver",
            "ComboBoxBorderBrushPressed",
            "ButtonBorderBrush",
            "ButtonBorderBrushPointerOver",
            "ButtonBorderBrushPressed");
        Set(dialog, text,
            "TextControlForeground",
            "TextControlForegroundPointerOver",
            "TextControlForegroundFocused",
            "TextControlHeaderForeground",
            "ComboBoxForeground",
            "ComboBoxForegroundPointerOver",
            "ComboBoxForegroundPressed",
            "ComboBoxForegroundFocused",
            "ComboBoxHeaderForeground",
            "ButtonForeground",
            "ButtonForegroundPointerOver",
            "ButtonForegroundPressed",
            "ToggleSwitchHeaderForeground",
            "ToggleSwitchContentForeground",
            "CheckBoxForegroundUnchecked",
            "CheckBoxForegroundChecked");
        Set(dialog, muted,
            "TextControlPlaceholderForeground",
            "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundFocused",
            "ComboBoxPlaceHolderForeground");

        // Selection follows the session tree; focus and "on" states spend the accent.
        Set(dialog, selection,
            "ComboBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundSelectedPointerOver",
            "ComboBoxItemBackgroundSelectedPressed");
        Set(dialog, selectionText, "ComboBoxItemForegroundSelected");
        Set(dialog, accent,
            "TextControlBorderBrushFocused",
            "TextControlSelectionHighlightColor",
            "ComboBoxBorderBrushFocused",
            "AccentButtonBackground",
            "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed",
            "ToggleSwitchFillOn",
            "ToggleSwitchFillOnPointerOver",
            "ToggleSwitchFillOnPressed",
            "CheckBoxCheckBackgroundFillChecked",
            "CheckBoxCheckBackgroundFillCheckedPointerOver",
            // Caught by name rather than by control: the section selector's pill, focus
            // rings, and the spin buttons all ask for the system accent directly.
            "AccentFillColorDefaultBrush",
            "AccentFillColorSecondaryBrush",
            "AccentFillColorTertiaryBrush");
    }

    public static void SetRequestedTheme(ContentDialog dialog, string theme)
    {
        dialog.RequestedTheme = ThemeCatalog.IsLight(App.ResolveTheme(theme))
            ? ElementTheme.Light
            : ElementTheme.Dark;
    }

    private static void Set(ContentDialog dialog, Brush brush, params string[] keys)
    {
        foreach (var key in keys)
            dialog.Resources[key] = brush;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
