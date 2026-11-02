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
/// each window shows them one at a time. Each takes the asking connection's token: once it
/// is cancelled the dialog is never shown, or is taken down, and the answer is "cancelled".</summary>
public static class ConnectDialogs
{

    /// <summary>Prompts for a password/passphrase. Returns (secret, save) or null on cancel.</summary>
    public static async Task<(string Secret, bool Save)?> PromptCredentialAsync(
        XamlRoot xamlRoot, string title, string prompt,
        string? saveLabel = "Save in Windows Credential Manager", string primaryText = "Connect",
        CancellationToken cancellationToken = default)
    {
        var dialog = new CredentialDialog(title, prompt, saveLabel, primaryText) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync(cancellationToken) == ContentDialogResult.Primary
            ? (dialog.Secret, dialog.Save)
            : null;
    }

    /// <summary>Asks for a username and password when no login applies to the session, and
    /// where to remember them. Returns null on cancel.</summary>
    public static async Task<LoginAnswer?> PromptLoginAsync(
        XamlRoot xamlRoot, Resesh.Core.Models.Session session, string username, bool canRemember, bool identityMissing,
        CancellationToken cancellationToken = default)
    {
        var dialog = new LoginDialog(session, username, canRemember, identityMissing) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync(cancellationToken) == ContentDialogResult.Primary ? dialog.Answer : null;
    }

    /// <summary>Shows each keyboard-interactive challenge and returns its explicit response.</summary>
    public static async Task<IReadOnlyList<string>?> PromptKeyboardInteractiveAsync(
        XamlRoot xamlRoot,
        string title,
        IReadOnlyList<KeyboardInteractivePrompt> prompts,
        CancellationToken cancellationToken = default)
    {
        var dialog = new KeyboardInteractiveDialog(title, prompts) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync(cancellationToken) == ContentDialogResult.Primary ? dialog.Responses : null;
    }

    /// <summary>Warns when a registered key path now contains a different public key.</summary>
    public static async Task<bool> ConfirmChangedPrivateKeyAsync(
        XamlRoot xamlRoot, SshKeyChangedException change, CancellationToken cancellationToken = default) =>
        await new ChangedPrivateKeyDialog(change) { XamlRoot = xamlRoot }.ShowModalAsync(cancellationToken) == ContentDialogResult.Primary;

    /// <summary>
    /// Lets the user resume one of the connection's running shells, start another, or clear
    /// out the detached ones and start fresh. Returns null on cancel.
    /// </summary>
    public static Task<TmuxChoice?> SelectTmuxSessionAsync(
        XamlRoot xamlRoot, string profileName, IReadOnlyList<TmuxSessionInfo> sessions,
        CancellationToken cancellationToken = default) =>
        new TmuxSessionPickerDialog(profileName, sessions) { XamlRoot = xamlRoot }
            .ChooseAsync(dialog => dialog.ShowModalAsync(cancellationToken));

    /// <summary>Host key confirmation: first connect, or a changed key (typed confirmation required).</summary>
    /// <param name="routeStep">Which jump host on the route is presenting the key, when it
    /// is not the session's own host.</param>
    public static async Task<bool> ConfirmHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info, string? routeStep = null,
        CancellationToken cancellationToken = default) =>
        await new HostKeyDialog(info, routeStep) { XamlRoot = xamlRoot }.ShowModalAsync(cancellationToken) == ContentDialogResult.Primary;
}
