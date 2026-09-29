using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace Resesh.App.Dialogs;

/// <summary>One registered key in the list: name, state, and how many sessions use it.</summary>
public sealed class SshKeyItem(SshKeyReference key, int useCount)
{
    public SshKeyReference Key { get; } = key;

    public string Label { get; } =
        $"{key.Name} — {(key.IsAvailable ? key.Algorithm ?? "SSH key" : "unavailable")} — "
        + (useCount == 1 ? "1 session" : $"{useCount} sessions");

    public override string ToString() => Label;
}

/// <summary>Manages references to private keys. It never copies, moves, or deletes key files.</summary>
public sealed partial class SshKeyManagerDialog : ContentDialog
{
    private static readonly Guid KeyPickerClientId = new("53039989-0672-4e4f-a98c-b0830762e3dc");

    private readonly SshKeyStore _keyStore;
    private readonly SessionStore _sessions;
    private readonly ICredentialService _credentials;

    public ObservableCollection<SshKeyItem> Keys { get; } = [];

    private SshKeyReference? Selected => (KeyList.SelectedItem as SshKeyItem)?.Key;

    private SshKeyManagerDialog(SshKeyStore keyStore, SessionStore sessions, ICredentialService credentials)
    {
        _keyStore = keyStore;
        _sessions = sessions;
        _credentials = credentials;
        InitializeComponent();
        Bindings.Update(); // realize ItemsSource now, so the first key can be preselected
        Refresh();
    }

    public static async Task ManageAsync(
        XamlRoot xamlRoot,
        SshKeyStore keyStore,
        SessionStore sessions,
        ICredentialService credentials) =>
        await new SshKeyManagerDialog(keyStore, sessions, credentials) { XamlRoot = xamlRoot }.ShowModalAsync();

    private int UseCount(Guid keyId) => _sessions.Sessions.Count(session => session.PrivateKeyId == keyId);

    private void Refresh(Guid? selectId = null)
    {
        Keys.Clear();
        foreach (var key in _keyStore.Keys)
            Keys.Add(new SshKeyItem(key, UseCount(key.Id)));
        KeyList.SelectedItem = Keys.FirstOrDefault(item => item.Key.Id == selectId) ?? Keys.FirstOrDefault();
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    private void KeyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = Selected;
        NameBox.IsEnabled = PathBox.IsEnabled = selected is not null;
        RenameButton.IsEnabled = LocateButton.IsEnabled = RemoveButton.IsEnabled = selected is not null;
        CopyPublicButton.IsEnabled = selected?.PublicKey is { Length: > 0 };
        NameBox.Text = selected?.Name ?? "";
        PathBox.Text = selected?.Path ?? "";
        FingerprintText.Text = selected is null
            ? ""
            : $"{selected.Algorithm ?? "Unknown algorithm"} · {selected.Fingerprint ?? "Fingerprint unavailable"}"
              + (selected.IsEncrypted == true ? " · Passphrase protected" : "");
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (PickKeyFile("Add SSH private key") is not { } path)
            return;
        try
        {
            var key = _keyStore.RegisterExternal(path);
            Status.IsOpen = false;
            Refresh(key.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or SshKeyChangedException)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected)
            return;
        try
        {
            _keyStore.Rename(selected.Id, NameBox.Text);
            Status.IsOpen = false;
            Refresh(selected.Id);
        }
        catch (ArgumentException ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected || PickKeyFile("Locate SSH private key") is not { } path)
            return;
        try
        {
            _keyStore.Relocate(selected.Id, path);
            Status.IsOpen = false;
            Refresh(selected.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or SshKeyChangedException)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void CopyPublic_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.PublicKey is not { Length: > 0 } publicKey)
            return;
        var package = new DataPackage();
        package.SetText(publicKey);
        Clipboard.SetContent(package);
        ShowStatus(InfoBarSeverity.Success, "The public key was copied.");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected)
            return;
        var useCount = UseCount(selected.Id);
        if (useCount > 0)
        {
            ShowStatus(InfoBarSeverity.Error, useCount == 1
                ? "One session uses this key. Assign another authentication method first."
                : $"{useCount} sessions use this key. Reassign them first.");
            return;
        }
        _keyStore.Remove(selected.Id);
        _credentials.DeleteKey(selected.Id);
        ShowStatus(InfoBarSeverity.Informational, "The key reference was removed. The private-key file was not changed.");
        Refresh();
    }

    private string? PickKeyFile(string title)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return Interop.Win32FileDialog.PickFile(hwnd, KeyPickerClientId, sshDirectory, title);
    }
}
