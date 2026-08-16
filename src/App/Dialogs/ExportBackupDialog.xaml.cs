using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.Core.Backup;
using Sessions.Core.Models;

namespace Sessions.App.Dialogs;

public sealed class BackupScopeChoice
{
    public string Label { get; set; } = "";
    public BackupScope? Scope { get; set; }

    public BackupScopeChoice() { }

    public BackupScopeChoice(string label, BackupScope? scope)
    {
        Label = label;
        Scope = scope;
    }
}

public sealed partial class ExportBackupDialog : ContentDialog
{
    public List<BackupScopeChoice> Scopes { get; }
    public BackupExportOptions? Options { get; private set; }

    public ExportBackupDialog(IEnumerable<string> sshFolders, IEnumerable<string> localFolders)
    {
        Scopes = [new BackupScopeChoice("All sessions", null)];
        Scopes.AddRange(sshFolders.Select(folder =>
            new BackupScopeChoice($"SSH folder: {folder}", new BackupScope(SessionKind.Ssh, folder))));
        Scopes.AddRange(localFolders.Select(folder =>
            new BackupScopeChoice($"Local folder: {folder}", new BackupScope(SessionKind.Local, folder))));

        InitializeComponent();
        ScopeBox.SelectedIndex = 0;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void IncludeSecrets_Click(object sender, RoutedEventArgs e)
    {
        PassphrasePanel.Visibility = IncludeSecretsCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var includeSecrets = IncludeSecretsCheck.IsChecked == true;
        if (includeSecrets && PassphraseBox.Password.Length < 8)
        {
            ShowValidation("Use a passphrase with at least 8 characters.");
            args.Cancel = true;
            return;
        }
        if (includeSecrets && PassphraseBox.Password != ConfirmPassphraseBox.Password)
        {
            ShowValidation("The passphrases do not match.");
            args.Cancel = true;
            return;
        }

        Options = new BackupExportOptions
        {
            Scope = (ScopeBox.SelectedItem as BackupScopeChoice)?.Scope,
            IncludeSecrets = includeSecrets,
            Passphrase = includeSecrets ? PassphraseBox.Password : null,
        };
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
