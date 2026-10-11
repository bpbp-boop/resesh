using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Models;

namespace Resesh.App.Dialogs;

/// <summary>Creates or edits one identity and saves it, with its password, when confirmed.</summary>
public sealed partial class IdentityEditDialog : ContentDialog
{
    private readonly Identity? _existing;

    /// <summary>The saved identity, or null when cancelled.</summary>
    public Identity? Result { get; private set; }

    private IdentityEditDialog(Identity? existing)
    {
        _existing = existing;
        InitializeComponent();
        DialogTheme.Apply(this);
        Title = existing is null ? "New identity" : "Edit identity";
        Form.Load(existing, existing is not null && App.Credentials.ReadIdentity(existing.Id) is { Length: > 0 });
        Form.ErrorRaised += message =>
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        };
        if (existing is not null)
        {
            var users = App.Logins.UsersOf(existing.Id, App.Store.Sessions).Count;
            UsageText.Text = users == 1
                ? "1 session uses this identity. Changes apply the next time it connects."
                : $"{users} sessions use this identity. Changes apply the next time each connects.";
        }
        Opened += (_, _) => Form.FocusFirstField();
    }

    /// <summary>Shows the editor; returns the saved identity, or null when cancelled.</summary>
    public static async Task<Identity?> EditAsync(XamlRoot xamlRoot, Identity? existing)
    {
        var dialog = new IdentityEditDialog(existing) { XamlRoot = xamlRoot };
        await dialog.ShowModalAsync();
        return dialog.Result;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Form.TryBuild() is not var (identity, password))
        {
            args.Cancel = true;
            return;
        }
        try
        {
            Result = Save(_existing, identity, password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            args.Cancel = true;
        }
    }

    /// <summary>Stores an identity and its secret. Leaving the saved-password proof deletes
    /// the stored password; leaving ask-once forgets the remembered one.</summary>
    internal static Identity Save(Identity? existing, Identity identity, string? password)
    {
        var saved = existing is null ? App.Identities.Add(identity) : App.Identities.Update(identity);
        if (saved.Proof == IdentityProof.Password && password is not null)
            App.Credentials.WriteIdentity(saved.Id, password);
        if (saved.Proof != IdentityProof.Password)
            App.Credentials.DeleteIdentity(saved.Id);
        if (saved.Proof != IdentityProof.Ask || existing?.Username != saved.Username)
            App.AskedSecrets.Forget(saved.Id);
        return saved;
    }
}
