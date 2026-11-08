using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Identities;

namespace Resesh.App.Dialogs;

/// <summary>
/// The tidy-up assistant: proposes identities for saved SSH sessions that still keep their
/// own login (<see cref="LoginTidyUp"/>). Each proposal is a checkbox; logins that probably
/// don't share one password start unticked, as in the import review.
/// </summary>
public sealed partial class TidyUpLoginsDialog : ContentDialog
{
    private readonly HashSet<string> _declined = [];
    private LoginTidyUpPlan _plan;

    private TidyUpLoginsDialog()
    {
        InitializeComponent();
        DialogTheme.Apply(this);
        var all = Plan();
        foreach (var suggestion in all.Plan.Suggestions.Where(s => !s.LikelyShared))
            _declined.Add(suggestion.Login.Key);
        _plan = Plan();

        Intro.Text = all.Plan.Suggestions.Count == 0
            ? "No login repeats across sessions that keep their own login, so there is nothing to group."
            : "Each ticked login becomes one identity that its sessions share. Sessions in the listed folders "
              + "inherit it, and so will sessions you add there later. Saved passwords move to the identity.";
        foreach (var suggestion in all.Plan.Suggestions)
            SuggestionRows.Children.Add(SuggestionCard(suggestion));
        UpdateSummary();
    }

    /// <summary>Shows the assistant and applies the choice. True when something was saved.</summary>
    public static async Task<bool> RunAsync(XamlRoot xamlRoot)
    {
        var dialog = new TidyUpLoginsDialog { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary;
    }

    private LoginTidyUpPlan Plan() =>
        LoginTidyUp.Plan(App.Store.Sessions, App.Identities.Identities, App.Store.FolderIdentity,
            id => App.SshKeys.Find(id)?.Path, App.Credentials.Read, _declined);

    private void Replan()
    {
        _plan = Plan();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var changed = _plan.ChangedSessions;
        var creating = _plan.Plan.Suggestions.Count(s => s.Existing is null);
        SummaryText.Text = changed == 0
            ? "Every session keeps its own login."
            : $"{Count(changed, "session")} will change"
              + (creating > 0 ? $"; creates {(creating == 1 ? "1 identity" : $"{creating} identities")}." : ".");
        IsPrimaryButtonEnabled = changed > 0;
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private UIElement SuggestionCard(IdentitySuggestion suggestion)
    {
        var key = suggestion.Login.Key;
        var proof = suggestion.Existing is not null ? "existing identity"
            : suggestion.Login.UsesKey ? $"key {Path.GetFileName(suggestion.Login.KeyPath)}"
            : suggestion.Login.SharesSavedPassword ? "same saved password"
            : "no saved password";
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new TextBlock
        {
            Text = suggestion.Existing is not null ? $"Use {suggestion.Name}" : suggestion.Name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var user = suggestion.Name.Equals(suggestion.Login.Username, StringComparison.OrdinalIgnoreCase)
            ? "" : $"{suggestion.Login.Username} · ";
        lines.Children.Add(Muted($"{user}{proof} · {Count(suggestion.SessionCount, "session")}"));
        var folders = suggestion.Folders.Count > 0
            ? Muted($"Inherited in {string.Join(", ", suggestion.Folders.Take(5))}"
                + (suggestion.Folders.Count > 5 ? $" and {suggestion.Folders.Count - 5} more" : ""))
            : Muted("Chosen by each session");
        ToolTipService.SetToolTip(folders, string.Join("\n", suggestion.Folders));
        lines.Children.Add(folders);
        if (!suggestion.LikelyShared)
        {
            var caution = Muted("Tick only if this account has the same password on every host.");
            caution.TextWrapping = TextWrapping.Wrap;
            caution.FontStyle = Windows.UI.Text.FontStyle.Italic;
            lines.Children.Add(caution);
        }

        var box = new CheckBox { IsChecked = !_declined.Contains(key), Content = lines, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Share one login for {suggestion.Login.Username}");
        box.Checked += (_, _) => { _declined.Remove(key); Replan(); };
        box.Unchecked += (_, _) => { _declined.Add(key); Replan(); };
        return box;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            // Replan against the stores as they are now; another window may have changed them.
            LoginTidyUp.Apply(Plan(), App.Store, App.Identities, App.SshKeys, App.Credentials);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            args.Cancel = true;
        }
    }
}
