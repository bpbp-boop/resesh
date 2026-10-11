using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>The user's answer to the persistent-session picker.</summary>
public abstract record TmuxChoice
{
    private TmuxChoice() { }
    public sealed record Resume(int Slot) : TmuxChoice;
    public sealed record StartNew : TmuxChoice;
    public sealed record EndDetachedAndStartNew : TmuxChoice;
}

/// <summary>The dialogs of the connect workflow. Several tabs can connect at once, so
/// each window shows them one at a time.</summary>
public static class ConnectDialogs
{

    /// <summary>Prompts for a password/passphrase. Returns (secret, save) or null on cancel.</summary>
    public static async Task<(string Secret, bool Save)?> PromptCredentialAsync(
        XamlRoot xamlRoot, string title, string prompt,
        string? saveLabel = "Save in Windows Credential Manager", string primaryText = "Connect")
    {
        var dialog = new CredentialDialog(title, prompt, saveLabel, primaryText) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary
            ? (dialog.Secret, dialog.Save)
            : null;
    }

    /// <summary>Asks for a username and password when no login applies to the session, and
    /// where to remember them. Returns null on cancel.</summary>
    public static async Task<LoginAnswer?> PromptLoginAsync(
        XamlRoot xamlRoot, Resesh.Core.Models.Session session, string username, bool canRemember, bool identityMissing)
    {
        var dialog = new LoginDialog(session, username, canRemember, identityMissing) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary ? dialog.Answer : null;
    }

    /// <summary>Shows each keyboard-interactive challenge and returns its explicit response.</summary>
    public static async Task<IReadOnlyList<string>?> PromptKeyboardInteractiveAsync(
        XamlRoot xamlRoot,
        string title,
        IReadOnlyList<KeyboardInteractivePrompt> prompts)
    {
        var dialog = new KeyboardInteractiveDialog(title, prompts) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary ? dialog.Responses : null;
    }

    /// <summary>Warns when a registered key path now contains a different public key.</summary>
    public static async Task<bool> ConfirmChangedPrivateKeyAsync(
        XamlRoot xamlRoot, SshKeyChangedException change) =>
        await new ChangedPrivateKeyDialog(change) { XamlRoot = xamlRoot }.ShowModalAsync() == ContentDialogResult.Primary;

    /// <summary>
    /// Lets the user resume one of the connection's running shells, start another, or clear
    /// out the detached ones and start fresh. Returns null on cancel.
    /// </summary>
    public static Task<TmuxChoice?> SelectTmuxSessionAsync(
        XamlRoot xamlRoot, string profileName, IReadOnlyList<TmuxSessionInfo> sessions) =>
        new TmuxSessionPickerDialog(profileName, sessions) { XamlRoot = xamlRoot }
            .ChooseAsync(dialog => dialog.ShowModalAsync());

    /// <summary>Host key confirmation: first connect, or a changed key (typed confirmation required).</summary>
    public static async Task<bool> ConfirmHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info) =>
        await new HostKeyDialog(info) { XamlRoot = xamlRoot }.ShowModalAsync() == ContentDialogResult.Primary;
}
