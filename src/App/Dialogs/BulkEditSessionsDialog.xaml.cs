using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>
/// Edits the fields that make sense across many sessions. Every field starts on
/// "No change"; the dialog only builds the edit, the caller saves it.
/// </summary>
public sealed partial class BulkEditSessionsDialog : ContentDialog
{
    /// <summary>A choice in one of the pickers; <see cref="Change"/> is false for "No change".</summary>
    // DisplayMemberPath reads properties by name; this generates that lookup for AOT.
    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial class Choice
    {
        public bool Change { get; init; }
        public string Label { get; init; } = "";
        public string? Value { get; init; }
        public SessionLogin Login { get; init; }
        public Guid? IdentityId { get; init; }
        public Guid? JumpHostId { get; init; }
    }

    private static readonly Choice NoChange = new() { Label = "No change" };

    private readonly IReadOnlyList<Session> _sessions;

    /// <summary>The edit to apply, or null when cancelled or nothing was chosen.</summary>
    public SessionBulkEdit? Result { get; private set; }

    private BulkEditSessionsDialog(IReadOnlyList<Session> sessions)
    {
        _sessions = sessions;
        InitializeComponent();
        DialogTheme.Apply(this);
        Title = $"Edit {sessions.Count} sessions";
        Description.Text = "Fields left on No change keep what each session has. A field that a session's type "
            + "doesn't have is skipped for it.";

        var logins = new List<Choice>
        {
            NoChange,
            new() { Change = true, Login = SessionLogin.Inherit, Label = "Inherit from folder" },
        };
        logins.AddRange(App.Identities.Identities.Select(identity => new Choice
        {
            Change = true,
            Login = SessionLogin.Identity,
            IdentityId = identity.Id,
            Label = $"{identity.Name} ({identity.Username}, {SessionEditDialog.ProofText(identity)})",
        }));
        LoginBox.ItemsSource = logins;
        LoginBox.SelectedIndex = 0;

        // A selected session cannot be the jump for the rest of the selection, itself included.
        var selected = sessions.Select(session => session.Id).ToHashSet();
        var jumps = new List<Choice> { NoChange, new() { Change = true, Label = "None (connect directly or through the session's proxy)" } };
        jumps.AddRange(App.Store.Sessions
            .Where(candidate => candidate.Kind == SessionKind.Ssh && !selected.Contains(candidate.Id))
            .OrderBy(candidate => candidate.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new Choice
            {
                Change = true,
                JumpHostId = candidate.Id,
                Label = (candidate.FolderPath.Length > 0 ? $"{candidate.FolderPath}/" : "") + $"{candidate.Name} ({candidate.Host})",
            }));
        JumpBox.ItemsSource = jumps;
        JumpBox.SelectedIndex = 0;

        ColorBox.Items.Add("No change");
        foreach (var (name, _) in SessionEditDialog.ColorChoices)
            ColorBox.Items.Add(name);
        ColorBox.SelectedIndex = 0;

        var icons = new List<Choice> { NoChange };
        icons.Add(new Choice { Change = true, Value = null, Label = "Auto-detect" });
        icons.Add(new Choice { Change = true, Value = SessionIcons.None, Label = "No icon" });
        icons.AddRange(SessionIcons.BuiltIn.Select(icon => new Choice { Change = true, Value = icon.Key, Label = icon.Name }));
        icons.AddRange(App.Icons.CustomIcons().Select(icon => new Choice { Change = true, Value = icon.Key, Label = icon.Name }));
        IconBox.ItemsSource = icons;
        IconBox.SelectedIndex = 0;

        var themes = new List<Choice> { NoChange, new() { Change = true, Label = "Use app setting" } };
        themes.AddRange(ThemeCatalog.All.Select(theme => new Choice { Change = true, Value = theme.Id, Label = theme.Name }));
        ThemeBox.ItemsSource = themes;
        ThemeBox.SelectedIndex = 0;

        PersistentBox.SelectedIndex = 0;
        PersistenceBackendBox.SelectedIndex = 0;
        UpdateState();
    }

    /// <summary>Shows the dialog; the chosen edit, or null when cancelled.</summary>
    public static async Task<SessionBulkEdit?> EditAsync(XamlRoot xamlRoot, IReadOnlyList<Session> sessions)
    {
        var dialog = new BulkEditSessionsDialog(sessions) { XamlRoot = xamlRoot };
        return await dialog.ShowModalAsync() == ContentDialogResult.Primary ? dialog.Result : null;
    }

    private SessionBulkEdit BuildEdit()
    {
        var login = LoginBox.SelectedItem as Choice ?? NoChange;
        var icon = IconBox.SelectedItem as Choice ?? NoChange;
        var theme = ThemeBox.SelectedItem as Choice ?? NoChange;
        var jump = JumpBox?.SelectedItem as Choice ?? NoChange;
        return new SessionBulkEdit
        {
            Login = login.Change ? login.Login : null,
            IdentityId = login.IdentityId,
            KeepUsernames = KeepUsernamesBox.IsChecked == true,
            Port = double.IsNaN(PortBox.Value) ? null : (int)PortBox.Value,
            SetJumpHost = jump.Change,
            JumpHostId = jump.JumpHostId,
            SetColor = ColorBox.SelectedIndex > 0,
            ColorTag = ColorBox.SelectedIndex > 0 ? SessionEditDialog.ColorChoices[ColorBox.SelectedIndex - 1].Hex : null,
            SetIcon = icon.Change,
            Icon = icon.Value,
            SetTheme = theme.Change,
            Theme = theme.Value,
            Persistent = PersistentBox.SelectedIndex switch
            {
                1 => true,
                2 => false,
                _ => null,
            },
            SessionAgent = PersistenceBackendBox.SelectedIndex switch
            {
                1 => false,
                2 => true,
                _ => null,
            },
        };
    }

    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void KeepUsernames_Changed(object sender, RoutedEventArgs e) => UpdateState();

    private void PortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateState();

    private void UpdateState()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (ImpactText is null || PersistentBox is null || PersistenceBackendBox is null || KeepUsernamesBox is null)
            return;
        var edit = BuildEdit();
        KeepUsernamesBox.Visibility = edit.Login is null ? Visibility.Collapsed : Visibility.Visible;
        if (edit.IsEmpty)
        {
            ImpactText.Text = "";
            return;
        }

        var changed = edit.ApplyTo(_sessions);
        var parts = new List<string>
        {
            changed.Count == 1 ? "1 session will change." : $"{changed.Count} of {_sessions.Count} sessions will change.",
        };
        if (edit.Login is not null)
        {
            var ownLogins = _sessions.Count(s => s.Kind == SessionKind.Ssh && s.Login == SessionLogin.Own);
            if (ownLogins > 0)
            {
                parts.Add((ownLogins == 1 ? "1 session has its own login" : $"{ownLogins} sessions have their own login")
                    + (edit.KeepUsernames
                        ? "; any saved password is removed."
                        : "; the username and any saved password are removed."));
            }
        }
        ImpactText.Text = string.Join(" ", parts);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var edit = BuildEdit();
        Result = edit.IsEmpty ? null : edit;
    }
}
