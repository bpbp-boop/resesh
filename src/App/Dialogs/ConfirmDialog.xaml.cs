using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Resesh.App.Dialogs;

/// <summary>Asks before an action. Cancel is the default button unless the action is
/// harmless, so Enter never confirms something destructive by accident.</summary>
public sealed partial class ConfirmDialog : ContentDialog
{
    public string Message { get; }
    public string OptionText { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Lets Y confirm, for close prompts that are answered many times a day.
    /// Only this dialog hears the key, so it never acts as a global shortcut.</summary>
    public bool AcceptsY { get; set; }

    public bool HasOption => OptionText.Length > 0;
    public bool HasNote => Note.Length > 0;

    /// <summary>The optional check box's answer, read after the dialog closes.</summary>
    public bool IsOptionChecked => OptionBox.IsChecked == true;

    public ConfirmDialog(string title, string message, string primaryText, bool defaultToPrimary = false)
    {
        Message = message;
        InitializeComponent();
        Title = title;
        PrimaryButtonText = primaryText;
        if (defaultToPrimary)
            DefaultButton = ContentDialogButton.Primary;
    }

    /// <summary>Shows the dialog; true when the user confirmed.</summary>
    public async Task<bool> ConfirmAsync()
    {
        var confirmedByKeyboard = false;
        if (AcceptsY)
        {
            AddHandler(
                PreviewKeyDownEvent,
                new KeyEventHandler((_, args) =>
                {
                    if (args.Key != VirtualKey.Y)
                        return;

                    args.Handled = true;
                    confirmedByKeyboard = true;
                    Hide();
                }),
                handledEventsToo: true);
        }

        var result = await this.ShowModalAsync();
        return confirmedByKeyboard || result == ContentDialogResult.Primary;
    }

    public static Task<bool> ConfirmAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        string primaryText = "Delete",
        bool acceptY = false,
        bool defaultToPrimary = false) =>
        new ConfirmDialog(title, message, primaryText, defaultToPrimary)
        {
            XamlRoot = xamlRoot,
            AcceptsY = acceptY,
        }.ConfirmAsync();
}
