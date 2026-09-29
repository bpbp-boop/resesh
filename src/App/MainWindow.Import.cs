using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Backup;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Storage;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Resesh.App;

// Backup export/import and importing sessions from other clients.
public sealed partial class MainWindow
{
    // ---- Backup export / import ----

    private static string BackupDataDirectory => Path.GetDirectoryName(SessionStore.DefaultPath)!;

    private async Task ExportBackupAsync()
    {
        try
        {
            var optionsDialog = new ExportBackupDialog(
                App.Store.Folders, App.Store.FoldersOf(SessionKind.Local))
            {
                XamlRoot = Root.XamlRoot,
            };
            await optionsDialog.ShowModalAsync();
            if (optionsDialog.Options is not { } options)
                return;

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"resesh backup {DateTime.Now:yyyy-MM-dd}",
            };
            picker.FileTypeChoices.Add("resesh backup", [".reseshbackup"]);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await Task.Run(() => SessionsBackup.Export(
                file.Path,
                BackupDataDirectory,
                App.Store,
                App.Settings,
                App.KnownHosts,
                App.Highlights,
                App.SshKeys,
                App.Credentials,
                options));

            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Backup complete", options.IncludeSecrets
                ? "The encrypted backup was saved. Keep its passphrase in a safe place."
                : "The backup was saved. It does not contain passwords or key passphrases.");
        }
        catch (Exception ex)
        {
            await ShowBackupErrorAsync("Export failed", ex);
        }
    }

    private async Task ImportBackupAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".reseshbackup");
            picker.FileTypeFilter.Add(".sessionsbackup"); // pre-rename backups still import
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            string? passphrase = null;
            if (await Task.Run(() => SessionsBackup.IsEncrypted(file.Path)))
            {
                passphrase = await TextPromptDialog.PromptPasswordAsync(
                    Root.XamlRoot, "Encrypted backup", "Backup passphrase", "Continue");
                if (passphrase is null)
                    return;
            }

            var package = await Task.Run(() => SessionsBackup.Read(file.Path, passphrase));
            var conflicts = SessionsBackup.FindConflicts(App.Store, package);
            var preview = new ImportBackupDialog(package, conflicts) { XamlRoot = Root.XamlRoot };
            await preview.ShowModalAsync();
            if (preview.Resolutions is not { } resolutions)
                return;

            var result = await Task.Run(() => SessionsBackup.Import(
                package,
                BackupDataDirectory,
                App.Store,
                App.Settings,
                App.KnownHosts,
                App.Highlights,
                App.SshKeys,
                App.Credentials,
                resolutions));

            App.Icons.InvalidateCustomIcons();
            App.Workspaces.Load();
            App.RefreshWorkspaceMenus();
            ViewModel.RebuildTree();
            App.ApplySettingsToAllWindows();

            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Import complete",
                $"Added {result.Imported}, replaced {result.Replaced}, kept both for "
                + $"{result.Duplicated}, and kept {result.Kept} existing session(s)."
                + (result.SecretsImported > 0
                    ? $" Imported {result.SecretsImported} saved secret(s)."
                    : ""));
        }
        catch (Exception ex)
        {
            await ShowBackupErrorAsync("Import failed", ex);
        }
    }

    private Task ShowBackupErrorAsync(string title, Exception exception) =>
        MessageDialog.ShowMessageAsync(Root.XamlRoot, title, exception.Message);

    // ---- Session import ----

    private async Task ImportSessionsAsync()
    {
        try
        {
            var puttyTask = Task.Run(() => DemoMode.IsEnabled ? DemoMode.EmptyImportScan() : Core.Import.PuttyRegistryImporter.Scan());
            var openSshTask = Task.Run(() =>
                DemoMode.IsEnabled ? DemoMode.EmptyImportScan() : Core.Import.OpenSshConfigImporter.Scan(Core.Import.OpenSshConfigImporter.DefaultConfigPath));
            var secureCrtTask = Task.Run(DemoMode.ScanSecureCrt);

            await Task.WhenAll(puttyTask, openSshTask, secureCrtTask);
            var puttyScan = await puttyTask;
            var openSshScan = await openSshTask;
            var secureCrtScan = await secureCrtTask;

            var sourceDialog = new ImportSessionsDialog(puttyScan, openSshScan, secureCrtScan)
            {
                XamlRoot = Root.XamlRoot,
            };
            await sourceDialog.ShowModalAsync();
            if (sourceDialog.SelectedSource is not { } source)
                return;

            var scan = await GetSelectedImportScanAsync(source, puttyScan, openSshScan, secureCrtScan);
            if (scan is null)
                return;

            var sourceName = source switch
            {
                SessionImportSource.Putty => "PuTTY",
                SessionImportSource.OpenSsh => "OpenSSH",
                SessionImportSource.SecureCrt => "SecureCRT",
                _ => throw new ArgumentOutOfRangeException(nameof(source)),
            };

            if (scan.Importable.Count == 0 && scan.Skipped.Count == 0)
            {
                await MessageDialog.ShowMessageAsync(Root.XamlRoot, $"Import from {sourceName}",
                    "No importable SSH or telnet sessions were found.");
                return;
            }

            var preview = new ImportPreviewDialog(scan, sourceName) { XamlRoot = Root.XamlRoot };
            await preview.ShowModalAsync();
            if (preview.Confirmed is not { Count: > 0 } confirmed)
                return;

            var (imported, duplicates) = Core.Import.SecureCrtImporter.Commit(App.Store, confirmed, App.SshKeys);
            ViewModel.RebuildTree();
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Import complete", duplicates == 0
                ? $"Imported {imported} session(s)."
                : $"Imported {imported} session(s); skipped {duplicates} duplicate(s).");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Import failed", ex.Message);
        }
    }

    private async Task<Core.Import.ImportScanResult?> GetSelectedImportScanAsync(
        SessionImportSource source,
        Core.Import.ImportScanResult puttyScan,
        Core.Import.ImportScanResult openSshScan,
        Core.Import.ImportScanResult secureCrtScan)
    {
        if (source == SessionImportSource.Putty)
            return puttyScan;

        if (source == SessionImportSource.OpenSsh)
        {
            if (openSshScan.Importable.Count > 0)
                return openSshScan;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            return file is null
                ? null
                : await Task.Run(() => Core.Import.OpenSshConfigImporter.Scan(file.Path));
        }

        if (secureCrtScan.Importable.Count > 0)
            return secureCrtScan;

        var folderPicker = new Windows.Storage.Pickers.FolderPicker();
        folderPicker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            folderPicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await folderPicker.PickSingleFolderAsync();
        return folder is null
            ? null
            : await Task.Run(() => Core.Import.SecureCrtImporter.Scan(folder.Path));
    }
}
