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

    /// <summary>Where backups read and write the files no store owns (icons, workspaces):
    /// the folder the stores use, so --data-dir and demo mode back up their own data.</summary>
    private static string BackupDataDirectory => Program.DataDirectory;

    /// <summary>The window running a backup or import wizard, if any. One runs at a time
    /// across the app: they write the same stores, and each is a chain of dialogs and pickers
    /// that another wizard (or the same one from another window) would run beside.</summary>
    private static MainWindow? s_wizardWindow;

    /// <summary>Runs a wizard unless one is already running, in which case the window showing
    /// it comes forward, since its dialog or picker is what needs answering. A failure is
    /// shown in this window, or reported if the window has closed meanwhile.</summary>
    private async Task RunWizardAsync(string failureTitle, Func<Task> wizard)
    {
        if (_windowClosed)
            return;
        // A wizard whose window closed is abandoned: its dialogs return None and it ends.
        if (s_wizardWindow is { _windowClosed: false } running)
        {
            running.BringToFront();
            return;
        }
        s_wizardWindow = this;
        try
        {
            await wizard();
        }
        catch (Exception ex)
        {
            await ShowWizardErrorAsync(failureTitle, ex);
        }
        finally
        {
            if (ReferenceEquals(s_wizardWindow, this))
                s_wizardWindow = null;
        }
    }

    private async Task ShowWizardErrorAsync(string title, Exception exception)
    {
        if (_windowClosed || Root.XamlRoot is null)
        {
            App.ReportRecoverableError(exception);
            return;
        }
        try
        {
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, title, exception.Message);
        }
        catch (Exception displayError)
        {
            App.ReportRecoverableError(displayError);
        }
    }

    private Task ExportBackupAsync() => RunWizardAsync("Export failed", ExportBackupWizardAsync);

    private Task ImportBackupAsync() => RunWizardAsync("Import failed", ImportBackupWizardAsync);

    private Task ImportSessionsAsync() => RunWizardAsync("Import failed", ImportSessionsWizardAsync);

    private async Task ExportBackupWizardAsync()
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
        if (file is null || _windowClosed)
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
            options,
            App.Identities,
            App.Workspaces));

        if (!_windowClosed)
            await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Backup complete", options.IncludeSecrets
                ? "The encrypted backup was saved. Keep its passphrase in a safe place."
                : "The backup was saved. It does not contain passwords or key passphrases.");
    }

    private async Task ImportBackupWizardAsync()
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
        if (file is null || _windowClosed)
            return;

        string? passphrase = null;
        if (await Task.Run(() => SessionsBackup.IsEncrypted(file.Path)))
        {
            if (_windowClosed)
                return;
            passphrase = await TextPromptDialog.PromptPasswordAsync(
                Root.XamlRoot, "Encrypted backup", "Backup passphrase", "Continue");
            if (passphrase is null)
                return;
        }

        var package = await Task.Run(() => SessionsBackup.Read(file.Path, passphrase));
        if (_windowClosed)
            return;
        var conflicts = SessionsBackup.FindConflicts(App.Store, package);
        var preview = new ImportBackupDialog(package, conflicts) { XamlRoot = Root.XamlRoot };
        await preview.ShowModalAsync();
        if (preview.Resolutions is not { } resolutions)
            return;

        BackupImportResult result;
        try
        {
            result = await Task.Run(() => SessionsBackup.Import(
                package,
                BackupDataDirectory,
                App.Store,
                App.Settings,
                App.KnownHosts,
                App.Highlights,
                App.SshKeys,
                App.Credentials,
                resolutions,
                App.Identities,
                App.Workspaces));
        }
        finally
        {
            // A failed import may have saved some stores before the one that failed. Every
            // window follows each store it saved; icon files are read fresh only on request.
            App.Icons.InvalidateCustomIcons();
        }

        // The import has happened even if this window closed meanwhile; only the summary goes.
        if (_windowClosed)
            return;
        await MessageDialog.ShowMessageAsync(Root.XamlRoot, "Import complete",
            $"Added {result.Imported}, replaced {result.Replaced}, kept both for "
            + $"{result.Duplicated}, and kept {result.Kept} existing session(s)."
            + (result.SecretsImported > 0
                ? $" Imported {result.SecretsImported} saved secret(s)."
                : ""));
    }

    // ---- Session import ----

    private async Task ImportSessionsWizardAsync()
    {
        var puttyTask = Task.Run(() => DemoMode.IsEnabled ? DemoMode.EmptyImportScan() : Core.Import.PuttyRegistryImporter.Scan());
        var openSshTask = Task.Run(() =>
            DemoMode.IsEnabled ? DemoMode.EmptyImportScan() : Core.Import.OpenSshConfigImporter.Scan(Core.Import.OpenSshConfigImporter.DefaultConfigPath));
        var secureCrtTask = Task.Run(DemoMode.ScanSecureCrt);

        await Task.WhenAll(puttyTask, openSshTask, secureCrtTask);
        var puttyScan = await puttyTask;
        var openSshScan = await openSshTask;
        var secureCrtScan = await secureCrtTask;
        if (_windowClosed)
            return;

        var sourceDialog = new ImportSessionsDialog(puttyScan, openSshScan, secureCrtScan)
        {
            XamlRoot = Root.XamlRoot,
        };
        await sourceDialog.ShowModalAsync();
        if (sourceDialog.SelectedSource is not { } source)
            return;

        var scan = await GetSelectedImportScanAsync(source, puttyScan, openSshScan, secureCrtScan);
        if (scan is null || _windowClosed)
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

        // The review tab shows the result itself and rebuilds the tree when it imports.
        _ = ReviewImportAsync(scan, sourceName);
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
