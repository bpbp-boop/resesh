using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Dialogs;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace Resesh.App.Controls;

/// <summary>The Identities &amp; Keys section: identities with their usage, the default
/// identity for the tree root, and the registered SSH keys (formerly the SSH Keys dialog).</summary>
public sealed partial class SettingsPage
{
    private sealed class DefaultChoice
    {
        public Identity? Identity { get; init; }
        public string Label { get; init; } = "";
    }

    // Keeps the key picker's last-used folder separate from other pickers in the app.
    private static readonly Guid KeyPickerClientId = new("53039989-0672-4e4f-a98c-b0830762e3dc");

    private bool _refreshingIdentities;

    private void HookIdentityChanges()
    {
        void OnChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            if (IdentitiesSection.Visibility == Visibility.Visible)
                RefreshIdentities();
        });
        Loaded += (_, _) => App.Identities.Changed += OnChanged;
        Unloaded += (_, _) => App.Identities.Changed -= OnChanged;
    }

    private void RefreshIdentities()
    {
        _refreshingIdentities = true;
        try
        {
            var identities = App.Identities.Identities;
            var sessions = App.Store.Sessions;
            var folderSettings = App.Store.FolderSettings;

            var choices = new List<DefaultChoice> { new() { Label = "None (ask when connecting)" } };
            choices.AddRange(identities.Select(identity => new DefaultChoice { Identity = identity, Label = identity.Name }));
            DefaultIdentityBox.ItemsSource = choices;
            var rootId = App.Store.FolderIdentity("");
            DefaultIdentityBox.SelectedItem = choices.FirstOrDefault(c => c.Identity is not null && c.Identity.Id == rootId)
                ?? choices[0];

            IdentityRows.Children.Clear();
            if (identities.Count == 0)
            {
                IdentityRows.Children.Add(new SettingsCard
                {
                    Header = "No identities yet",
                    Description = "Connect to a session with no login and choose to remember it for the folder, "
                        + "or create one here.",
                });
            }
            foreach (var identity in identities)
                IdentityRows.Children.Add(IdentityRow(identity, sessions, folderSettings));

            KeyRows.Children.Clear();
            var keys = App.SshKeys.Keys;
            if (keys.Count == 0)
                KeyRows.Children.Add(new SettingsCard { Header = "No SSH keys registered" });
            foreach (var key in keys)
                KeyRows.Children.Add(KeyRow(key, sessions, identities));
        }
        finally
        {
            _refreshingIdentities = false;
        }
    }

    private SettingsCard IdentityRow(Identity identity, IReadOnlyList<Session> sessions, IReadOnlyList<FolderSettings> folderSettings)
    {
        var users = App.Logins.UsersOf(identity.Id, sessions);
        var folders = folderSettings
            .Where(f => f.IdentityId == identity.Id)
            .Select(f => f.Path.Length == 0 ? "default" : f.Path)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var usage = users.Count == 1 ? "1 session" : $"{users.Count} sessions";
        if (folders.Count > 0)
            usage += $" · {string.Join(", ", folders)}";

        var sessionsButton = new DropDownButton
        {
            Content = "Sessions",
            IsEnabled = users.Count > 0,
            Flyout = SessionsFlyout(users),
        };
        AutomationName(sessionsButton, $"Sessions using {identity.Name}");
        var edit = new Button { Content = "Edit…" };
        edit.Click += async (_, _) =>
        {
            if (await IdentityEditDialog.EditAsync(XamlRoot, identity) is not null)
                RefreshIdentities();
        };
        AutomationName(edit, $"Edit {identity.Name}");
        var delete = new Button { Content = "Delete" };
        delete.Click += async (_, _) => await DeleteIdentityAsync(identity, users.Count, folders.Count);
        AutomationName(delete, $"Delete {identity.Name}");

        return new SettingsCard
        {
            Header = identity.Name,
            Description = $"{identity.Username} · {SessionEditDialog.ProofText(identity)} · {usage}",
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = Buttons(sessionsButton, edit, delete),
        };
    }

    /// <summary>Raised when a session picked from an identity's session list should connect.</summary>
    public event Action<Session>? ConnectRequested;

    private MenuFlyout SessionsFlyout(IReadOnlyList<Session> users)
    {
        var flyout = new MenuFlyout();
        const int Shown = 25;
        foreach (var session in users.OrderBy(s => s.FolderPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(Shown))
        {
            var label = session.FolderPath.Length == 0 ? session.Name : $"{session.FolderPath}/{session.Name}";
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => ConnectRequested?.Invoke(session);
            flyout.Items.Add(item);
        }
        if (users.Count > Shown)
            flyout.Items.Add(new MenuFlyoutItem { Text = $"and {users.Count - Shown} more", IsEnabled = false });
        return flyout;
    }

    private async Task DeleteIdentityAsync(Identity identity, int users, int folders)
    {
        var scope = new List<string>();
        if (users > 0)
            scope.Add(users == 1 ? "1 session uses it" : $"{users} sessions use it");
        if (folders > 0)
            scope.Add(folders == 1 ? "1 folder has it attached" : $"{folders} folders have it attached");
        var message = scope.Count == 0
            ? $"Delete {identity.Name}? Nothing uses it."
            : $"Delete {identity.Name}? {string.Join(" and ", scope)}. Those sessions will ask for a login the next time they connect.";
        var confirm = new ConfirmDialog("Delete identity", message, "Delete") { XamlRoot = XamlRoot };
        if (!await confirm.ConfirmAsync())
            return;
        try
        {
            App.Store.DetachIdentity(identity.Id);
            App.Identities.Remove(identity.Id);
            App.Credentials.DeleteIdentity(identity.Id);
            App.AskedSecrets.Forget(identity.Id);
            ShowIdentityStatus(InfoBarSeverity.Informational, $"{identity.Name} was deleted.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowIdentityStatus(InfoBarSeverity.Error, exception.Message);
        }
        RefreshIdentities();
    }

    private async void NewIdentity_Click(object sender, RoutedEventArgs e)
    {
        if (await IdentityEditDialog.EditAsync(XamlRoot, null) is not null)
            RefreshIdentities();
    }

    private void DefaultIdentityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingIdentities || DefaultIdentityBox.SelectedItem is not DefaultChoice choice)
            return;
        try
        {
            App.Store.SetFolderIdentity("", choice.Identity?.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowIdentityStatus(InfoBarSeverity.Error, exception.Message);
        }
        RefreshIdentities();
    }

    // ---- SSH keys ----

    private SettingsCard KeyRow(SshKeyReference key, IReadOnlyList<Session> sessions, IReadOnlyList<Identity> identities)
    {
        var sessionUses = sessions.Count(s => s.Login == SessionLogin.Own && s.PrivateKeyId == key.Id);
        var identityUses = identities.Count(i => i.Proof == IdentityProof.Key && i.KeyId == key.Id);
        var usage = new List<string>();
        if (sessionUses > 0)
            usage.Add(sessionUses == 1 ? "1 session" : $"{sessionUses} sessions");
        if (identityUses > 0)
            usage.Add(identityUses == 1 ? "1 identity" : $"{identityUses} identities");
        var details = new List<string>
        {
            key.IsAvailable ? key.Algorithm ?? "SSH key" : "File not found",
            key.Fingerprint ?? "Fingerprint unavailable",
        };
        if (key.IsEncrypted == true)
            details.Add("passphrase protected");
        details.Add(usage.Count == 0 ? "unused" : string.Join(", ", usage));

        var copy = new Button { Content = "Copy public key", IsEnabled = key.PublicKey is { Length: > 0 } };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(key.PublicKey);
            Clipboard.SetContent(package);
            ShowIdentityStatus(InfoBarSeverity.Success, $"The public key of {key.Name} was copied.");
        };
        var more = new DropDownButton { Content = "More" };
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += async (_, _) => await RenameKeyAsync(key);
        var locate = new MenuFlyoutItem { Text = "Locate…" };
        locate.Click += (_, _) => LocateKey(key);
        var remove = new MenuFlyoutItem { Text = "Remove" };
        remove.Click += (_, _) => RemoveKey(key, sessionUses, identityUses);
        menu.Items.Add(rename);
        menu.Items.Add(locate);
        menu.Items.Add(remove);
        more.Flyout = menu;
        AutomationName(copy, $"Copy public key of {key.Name}");
        AutomationName(more, $"More actions for {key.Name}");

        return new SettingsCard
        {
            Header = key.Name,
            Description = $"{string.Join(" · ", details)}\n{key.Path}",
            HeaderIcon = new FontIcon { Glyph = key.IsAvailable ? "" : "" },
            Content = Buttons(copy, more),
        };
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (PickKeyFile("Add SSH private key") is not { } path)
            return;
        try
        {
            var key = App.SshKeys.RegisterExternal(path);
            ShowIdentityStatus(InfoBarSeverity.Success, $"{key.Name} was added.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or SshKeyChangedException)
        {
            ShowIdentityStatus(InfoBarSeverity.Error, ex.Message);
        }
        RefreshIdentities();
    }

    private async Task RenameKeyAsync(SshKeyReference key)
    {
        var name = await TextPromptDialog.PromptAsync(XamlRoot, "Rename SSH key", "Name", key.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == key.Name)
            return;
        try
        {
            App.SshKeys.Rename(key.Id, name);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowIdentityStatus(InfoBarSeverity.Error, ex.Message);
        }
        RefreshIdentities();
    }

    private void LocateKey(SshKeyReference key)
    {
        if (PickKeyFile($"Locate {key.Name}") is not { } path)
            return;
        try
        {
            App.SshKeys.Relocate(key.Id, path);
            ShowIdentityStatus(InfoBarSeverity.Success, $"{key.Name} now points to {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or SshKeyChangedException)
        {
            ShowIdentityStatus(InfoBarSeverity.Error, ex.Message);
        }
        RefreshIdentities();
    }

    private void RemoveKey(SshKeyReference key, int sessionUses, int identityUses)
    {
        if (sessionUses + identityUses > 0)
        {
            var users = new List<string>();
            if (sessionUses > 0)
                users.Add(sessionUses == 1 ? "1 session" : $"{sessionUses} sessions");
            if (identityUses > 0)
                users.Add(identityUses == 1 ? "1 identity" : $"{identityUses} identities");
            ShowIdentityStatus(InfoBarSeverity.Error,
                $"{string.Join(" and ", users)} use {key.Name}. Choose another way to sign in for them first.");
            return;
        }
        try
        {
            App.SshKeys.Remove(key.Id);
            App.Credentials.DeleteKey(key.Id);
            ShowIdentityStatus(InfoBarSeverity.Informational,
                $"{key.Name} was removed. The private-key file was not changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowIdentityStatus(InfoBarSeverity.Error, ex.Message);
        }
        RefreshIdentities();
    }

    private string? PickKeyFile(string title)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return Interop.Win32FileDialog.PickFile(hwnd, KeyPickerClientId, sshDirectory, title);
    }

    // ---- helpers ----

    private void ShowIdentityStatus(InfoBarSeverity severity, string message)
    {
        IdentityStatus.Severity = severity;
        IdentityStatus.Message = message;
        IdentityStatus.IsOpen = true;
    }

    private static StackPanel Buttons(params UIElement[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
            panel.Children.Add(button);
        return panel;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
