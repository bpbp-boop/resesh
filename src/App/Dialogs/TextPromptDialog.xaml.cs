using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.App.Dialogs;

/// <summary>Asks for one value: a name (the initial text arrives selected, ready to
/// replace) or a password that is never echoed.</summary>
public sealed partial class TextPromptDialog : ContentDialog
{
    public string? FieldHeader { get; set; }
    public string Placeholder { get; set; } = "";
    public bool IsPassword { get; }
    public bool IsText => !IsPassword;

    /// <summary>The entered text or password, read after the dialog closes.</summary>
    public string Value => IsPassword ? PasswordField.Password : TextField.Text;

    public TextPromptDialog(string title, string primaryText = "OK", bool isPassword = false, string initial = "")
    {
        IsPassword = isPassword;
        InitializeComponent();
        Title = title;
        PrimaryButtonText = primaryText;
        TextField.Text = initial;
        Opened += (_, _) =>
        {
            if (IsPassword)
            {
                PasswordField.Focus(FocusState.Programmatic);
            }
            else
            {
                TextField.Focus(FocusState.Programmatic);
                TextField.SelectAll();
            }
        };
    }

    /// <summary>Shows the dialog; the value on confirm, or null when cancelled.</summary>
    public async Task<string?> PromptAsync() =>
        await this.ShowModalAsync() == ContentDialogResult.Primary ? Value : null;

    public static Task<string?> PromptAsync(
        XamlRoot xamlRoot, string title, string placeholder, string initial, string primaryText = "OK") =>
        new TextPromptDialog(title, primaryText, initial: initial)
        {
            XamlRoot = xamlRoot,
            Placeholder = placeholder,
        }.PromptAsync();

    /// <summary>The entered password, or null when cancelled or left empty.</summary>
    public static async Task<string?> PromptPasswordAsync(
        XamlRoot xamlRoot, string title, string header, string primaryText) =>
        await new TextPromptDialog(title, primaryText, isPassword: true)
        {
            XamlRoot = xamlRoot,
            FieldHeader = header,
        }.PromptAsync() is { Length: > 0 } password
            ? password
            : null;
}
