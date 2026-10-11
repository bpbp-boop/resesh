using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>Where the login prompt's answer is remembered.</summary>
public enum LoginRemember
{
    /// <summary>A new identity attached to the session's folder.</summary>
    Folder,

    /// <summary>The session's own username and password.</summary>
    Session,

    /// <summary>Used for this connection only.</summary>
    Nowhere,
}

public sealed record LoginAnswer(string Username, string Password, LoginRemember Remember, bool SavePassword);

/// <summary>
/// Asks who to log in as when nothing applies to a session. The default remembers the
/// answer for every session in the folder, so the next session there needs nothing.
/// </summary>
public sealed partial class LoginDialog : ContentDialog
{
    private readonly string _folderPath;
    private readonly bool _canRemember;

    public LoginAnswer? Answer { get; private set; }

    public LoginDialog(Session session, string username, bool canRemember, bool identityMissing)
    {
        _folderPath = FolderPaths.Normalize(session.FolderPath);
        _canRemember = canRemember;
        InitializeComponent();
        DialogTheme.Apply(this);
        Title = $"Log in to {session.Name}";
        MissingBar.IsOpen = identityMissing;
        UsernameBox.Text = username;
        UsernameBox.PlaceholderText = "admin";

        if (canRemember)
        {
            RememberBox.Items.Add(FolderChoiceText(_folderPath));
            RememberBox.Items.Add("For this session only");
        }
        RememberBox.Items.Add("Don't remember");
        RememberBox.SelectedIndex = 0;
        RememberBox.IsEnabled = canRemember;
        RememberBox.SelectionChanged += (_, _) => UpdateState();
        SaveBox.Checked += (_, _) => UpdateState();
        SaveBox.Unchecked += (_, _) => UpdateState();
        UsernameBox.TextChanged += (_, _) => UpdateState();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Opened += (_, _) =>
            (username.Length > 0 ? (Control)PasswordBox : UsernameBox).Focus(FocusState.Programmatic);
        UpdateState();
    }

    /// <summary>A readable default name for an identity created from this prompt.</summary>
    public static string SuggestedIdentityName(string username, string folderPath)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        return folderPath.Length == 0 ? username : $"{username} ({FolderPaths.Name(folderPath)})";
    }

    private static string FolderChoiceText(string folderPath) => folderPath.Length == 0
        ? "For every session without its own login"
        : $"For every session in {folderPath}";

    private LoginRemember Remember => !_canRemember
        ? LoginRemember.Nowhere
        : (LoginRemember)Math.Max(0, RememberBox.SelectedIndex);

    private void UpdateState()
    {
        IsPrimaryButtonEnabled = UsernameBox.Text.Trim().Length > 0;
        var remember = Remember;
        SaveBox.IsEnabled = remember != LoginRemember.Nowhere;
        var save = SaveBox.IsChecked == true;
        RememberHint.Text = remember switch
        {
            LoginRemember.Folder when save =>
                "Creates an identity for this folder. Sessions added here later use it too, and one "
                + "password change updates all of them. Manage it in Settings > Identities & Keys.",
            LoginRemember.Folder =>
                "Creates an identity for this folder that remembers the username. The password is "
                + "asked once and kept in memory until resesh closes.",
            LoginRemember.Session when save => "Saved with this session only.",
            LoginRemember.Session => "Remembers the username for this session; the password is asked each time.",
            _ => "Used for this connection only.",
        };
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var username = UsernameBox.Text.Trim();
        if (username.Length == 0)
        {
            args.Cancel = true;
            UsernameBox.Focus(FocusState.Programmatic);
            return;
        }
        var remember = Remember;
        Answer = new LoginAnswer(username, PasswordBox.Password, remember,
            remember != LoginRemember.Nowhere && SaveBox.IsChecked == true && PasswordBox.Password.Length > 0);
    }
}
