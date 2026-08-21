using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace Resesh.App.Dialogs;

/// <summary>Manages references to private keys. It never copies, moves, or deletes key files.</summary>
public static class SshKeyManagerDialog
{
    private sealed class KeyItem
    {
        public SshKeyReference Key { get; init; } = new();
        public string Label { get; init; } = "";
    }

    private static readonly Guid KeyPickerClientId = new("53039989-0672-4e4f-a98c-b0830762e3dc");

    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        SshKeyStore keyStore,
        SessionStore sessions,
        ICredentialService credentials)
    {
        var list = new ListView
        {
            MinWidth = 520,
            MaxHeight = 260,
            DisplayMemberPath = nameof(KeyItem.Label),
            SelectionMode = ListViewSelectionMode.Single,
        };
        var name = new TextBox { Header = "Name", IsEnabled = false };
        var path = new TextBox { Header = "Private-key file", IsReadOnly = true, IsEnabled = false };
        var fingerprint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var status = new InfoBar { IsOpen = false, IsClosable = true };
        var add = new Button { Content = "Add…" };
        var rename = new Button { Content = "Rename", IsEnabled = false };
        var locate = new Button { Content = "Locate…", IsEnabled = false };
        var copyPublic = new Button { Content = "Copy public key", IsEnabled = false };
        var remove = new Button { Content = "Remove", IsEnabled = false };

        KeyItem? Selected() => list.SelectedItem as KeyItem;

        void Refresh(Guid? selectId = null)
        {
            var items = keyStore.Keys.Select(key =>
            {
                var state = key.IsAvailable ? key.Algorithm ?? "SSH key" : "unavailable";
                var useCount = sessions.Sessions.Count(session => session.PrivateKeyId == key.Id);
                var usage = useCount == 1 ? "1 session" : $"{useCount} sessions";
                return new KeyItem { Key = key, Label = $"{key.Name} — {state} — {usage}" };
            }).ToList();
            list.ItemsSource = items;
            list.SelectedItem = items.FirstOrDefault(item => item.Key.Id == selectId) ?? items.FirstOrDefault();
        }

        void ShowError(string message)
        {
            status.Severity = InfoBarSeverity.Error;
            status.Message = message;
            status.IsOpen = true;
        }

        list.SelectionChanged += (_, _) =>
        {
            var selected = Selected()?.Key;
            name.IsEnabled = selected is not null;
            path.IsEnabled = selected is not null;
            rename.IsEnabled = selected is not null;
            locate.IsEnabled = selected is not null;
            remove.IsEnabled = selected is not null;
            copyPublic.IsEnabled = selected?.PublicKey is { Length: > 0 };
            name.Text = selected?.Name ?? "";
            path.Text = selected?.Path ?? "";
            fingerprint.Text = selected is null
                ? ""
                : $"{selected.Algorithm ?? "Unknown algorithm"} · {selected.Fingerprint ?? "Fingerprint unavailable"}"
                  + (selected.IsEncrypted == true ? " · Passphrase protected" : "");
        };

        add.Click += (_, _) =>
        {
            var selectedPath = PickKeyFile(xamlRoot, "Add SSH private key");
            if (selectedPath is null)
                return;
            try
            {
                var key = keyStore.RegisterExternal(selectedPath);
                status.IsOpen = false;
                Refresh(key.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or SshKeyChangedException)
            {
                ShowError(ex.Message);
            }
        };
        rename.Click += (_, _) =>
        {
            if (Selected() is not { } selected)
                return;
            try
            {
                keyStore.Rename(selected.Key.Id, name.Text);
                status.IsOpen = false;
                Refresh(selected.Key.Id);
            }
            catch (ArgumentException ex)
            {
                ShowError(ex.Message);
            }
        };
        locate.Click += (_, _) =>
        {
            if (Selected() is not { } selected)
                return;
            var selectedPath = PickKeyFile(xamlRoot, "Locate SSH private key");
            if (selectedPath is null)
                return;
            try
            {
                keyStore.Relocate(selected.Key.Id, selectedPath);
                status.IsOpen = false;
                Refresh(selected.Key.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or SshKeyChangedException)
            {
                ShowError(ex.Message);
            }
        };
        copyPublic.Click += (_, _) =>
        {
            if (Selected()?.Key.PublicKey is not { Length: > 0 } publicKey)
                return;
            var package = new DataPackage();
            package.SetText(publicKey);
            Clipboard.SetContent(package);
            status.Severity = InfoBarSeverity.Success;
            status.Message = "The public key was copied.";
            status.IsOpen = true;
        };
        remove.Click += (_, _) =>
        {
            if (Selected() is not { } selected)
                return;
            var useCount = sessions.Sessions.Count(session => session.PrivateKeyId == selected.Key.Id);
            if (useCount > 0)
            {
                ShowError(useCount == 1
                    ? "One session uses this key. Assign another authentication method first."
                    : $"{useCount} sessions use this key. Reassign them first.");
                return;
            }
            keyStore.Remove(selected.Key.Id);
            credentials.DeleteKey(selected.Key.Id);
            status.Severity = InfoBarSeverity.Informational;
            status.Message = "The key reference was removed. The private-key file was not changed.";
            status.IsOpen = true;
            Refresh();
        };

        Refresh();
        var dialog = new ContentDialog
        {
            Title = "SSH Keys",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Resesh records where each key is stored. It does not copy or move private-key files.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    status,
                    list,
                    name,
                    path,
                    fingerprint,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { add, rename, locate, copyPublic, remove },
                    },
                },
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static string? PickKeyFile(XamlRoot xamlRoot, string title)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId);
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return Interop.Win32FileDialog.PickFile(hwnd, KeyPickerClientId, sshDirectory, title);
    }
}
