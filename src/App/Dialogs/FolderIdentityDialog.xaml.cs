using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Identities;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>
/// Attaches an identity to a folder of the remote tree (or the tree root), or detaches it.
/// Sessions inside that inherit their login use it; sessions with their own login keep it.
/// </summary>
public sealed partial class FolderIdentityDialog : ContentDialog
{
    private enum ChoiceKind { None, Existing, New }

    // DisplayMemberPath reads properties by name; this generates that lookup for AOT.
    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial class Choice
    {
        public ChoiceKind Kind { get; init; }
        public Identity? Identity { get; init; }
        public string Label { get; init; } = "";
    }

    private readonly string _folderPath;

    private FolderIdentityDialog(string folderPath)
    {
        _folderPath = FolderPaths.Normalize(folderPath);
        InitializeComponent();
        DialogTheme.Apply(this);
        var root = _folderPath.Length == 0;
        Title = root ? "Default identity" : $"Identity for {_folderPath}";
        Description.Text = root
            ? "Sessions that inherit their login use this identity when no folder above them has one."
            : "Sessions in this folder and its subfolders that inherit their login use this identity. "
              + "Sessions with their own login are not changed.";

        var parent = root ? null : App.Logins.InheritedAt(FolderPaths.Parent(_folderPath));
        var choices = new List<Choice>
        {
            new()
            {
                Kind = ChoiceKind.None,
                Label = parent is { } inherited
                    ? $"None (inherit {inherited.Identity.Name} from {(inherited.FolderPath.Length == 0 ? "the default" : inherited.FolderPath)})"
                    : "None (ask when connecting)",
            },
        };
        choices.AddRange(App.Identities.Identities.Select(identity => new Choice
        {
            Kind = ChoiceKind.Existing,
            Identity = identity,
            Label = $"{identity.Name} ({identity.Username}, {SessionEditDialog.ProofText(identity)})",
        }));
        choices.Add(new Choice { Kind = ChoiceKind.New, Label = "New identity…" });
        ChoiceBox.ItemsSource = choices;
        var current = App.Store.FolderIdentity(_folderPath);
        ChoiceBox.SelectedItem = choices.FirstOrDefault(c => c.Identity?.Id == current && current is not null)
            ?? (App.Identities.Identities.Count == 0 && current is null ? choices[^1] : choices[0]);

        Form.ErrorRaised += message =>
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        };
        UpdateState();
    }

    /// <summary>Shows the dialog and saves the choice. True when something was saved.</summary>
    public static async Task<bool> EditAsync(XamlRoot xamlRoot, string folderPath)
    {
        var dialog = new FolderIdentityDialog(folderPath) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary;
    }

    private Choice Selected => ChoiceBox.SelectedItem as Choice ?? new Choice { Kind = ChoiceKind.None };

    private void ChoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (Form is null || ImpactText is null)
            return;
        var choice = Selected;
        Form.Visibility = choice.Kind == ChoiceKind.New ? Visibility.Visible : Visibility.Collapsed;
        if (choice.Kind == ChoiceKind.New)
            Form.FocusFirstField();

        // Resolve every session under the folder as if the choice were already saved.
        var proposed = choice.Kind == ChoiceKind.None ? (Guid?)null : choice.Identity?.Id ?? Guid.Empty;
        var resolver = new LoginResolver(
            path => FolderPaths.Normalize(path).Equals(_folderPath, StringComparison.OrdinalIgnoreCase)
                ? proposed
                : App.Store.FolderIdentity(path),
            id => id == Guid.Empty ? new Identity { Id = Guid.Empty } : App.Identities.Find(id));
        var under = App.Store.Sessions
            .Where(s => s.Kind == SessionKind.Ssh
                && (_folderPath.Length == 0 || FolderPaths.IsSelfOrDescendant(s.FolderPath, _folderPath)))
            .ToList();
        var willUse = proposed is null ? 0 : under.Count(s => resolver.Resolve(s) is { Source: LoginSource.Folder } login
            && login.FolderPath!.Equals(_folderPath, StringComparison.OrdinalIgnoreCase));
        var own = under.Count(s => s.Login != SessionLogin.Inherit);
        var parts = new List<string>();
        if (proposed is not null)
            parts.Add(willUse == 1 ? "1 session will use this identity." : $"{willUse} sessions will use this identity.");
        if (own > 0)
            parts.Add(own == 1 ? "1 session here keeps its own login." : $"{own} sessions here keep their own login.");
        ImpactText.Text = string.Join(" ", parts);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var choice = Selected;
        try
        {
            Guid? identityId = choice.Identity?.Id;
            if (choice.Kind == ChoiceKind.New)
            {
                if (Form.TryBuild() is not var (identity, password))
                {
                    args.Cancel = true;
                    return;
                }
                identityId = IdentityEditDialog.Save(null, identity, password).Id;
            }
            App.Store.SetFolderIdentity(_folderPath, choice.Kind == ChoiceKind.None ? null : identityId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            args.Cancel = true;
        }
    }
}
