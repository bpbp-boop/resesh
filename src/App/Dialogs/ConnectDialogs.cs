using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;
using System.Runtime.CompilerServices;

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
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var xamlRoot = dialog.XamlRoot
            ?? throw new InvalidOperationException("Connect dialogs require a XamlRoot.");
        var dialogGate = DialogGates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        await dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowModalAsync();
        }
        finally
        {
            dialogGate.Release();
        }
    }

    /// <summary>Prompts for a password/passphrase. Returns (secret, save) or null on cancel.</summary>
    public static async Task<(string Secret, bool Save)?> PromptCredentialAsync(
        XamlRoot xamlRoot, string title, string prompt)
    {
        var dialog = new CredentialDialog(title, prompt) { XamlRoot = xamlRoot };
        return await ShowAsync(dialog) == ContentDialogResult.Primary
            ? (dialog.Secret, dialog.Save)
            : null;
    }

    /// <summary>Shows each keyboard-interactive challenge and returns its explicit response.</summary>
    public static async Task<IReadOnlyList<string>?> PromptKeyboardInteractiveAsync(
        XamlRoot xamlRoot,
        string title,
        IReadOnlyList<KeyboardInteractivePrompt> prompts)
    {
        var dialog = new KeyboardInteractiveDialog(title, prompts) { XamlRoot = xamlRoot };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? dialog.Responses : null;
    }

    /// <summary>Warns when a registered key path now contains a different public key.</summary>
    public static async Task<bool> ConfirmChangedPrivateKeyAsync(
        XamlRoot xamlRoot, SshKeyChangedException change) =>
        await ShowAsync(new ChangedPrivateKeyDialog(change) { XamlRoot = xamlRoot }) == ContentDialogResult.Primary;

    /// <summary>
    /// Lets the user resume one of the connection's running shells, start another, or clear
    /// out the detached ones and start fresh. Returns null on cancel.
    /// </summary>
    public static Task<TmuxChoice?> SelectTmuxSessionAsync(
        XamlRoot xamlRoot, string profileName, IReadOnlyList<TmuxSessionInfo> sessions) =>
        new TmuxSessionPickerDialog(profileName, sessions) { XamlRoot = xamlRoot }.ChooseAsync(ShowAsync);

    /// <summary>Host key confirmation: first connect, or a changed key (typed confirmation required).</summary>
    public static async Task<bool> ConfirmHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info) =>
        await ShowAsync(new HostKeyDialog(info) { XamlRoot = xamlRoot }) == ContentDialogResult.Primary;
}
