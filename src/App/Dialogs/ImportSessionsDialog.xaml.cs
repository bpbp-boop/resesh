using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Import;

namespace Resesh.App.Dialogs;

public enum SessionImportSource
{
    Putty,
    OpenSsh,
    SecureCrt,
}

public sealed partial class ImportSessionsDialog : ContentDialog
{
    public SessionImportSource? SelectedSource { get; private set; }

    public ImportSessionsDialog(
        ImportScanResult puttyScan,
        ImportScanResult openSshScan,
        ImportScanResult secureCrtScan)
    {
        InitializeComponent();

        PuttyDetail.Text = Describe(puttyScan, "No saved PuTTY sessions found.");
        OpenSshDetail.Text = Describe(
            openSshScan,
            "No sessions found in ~/.ssh/config. You can choose another config file.");
        SecureCrtDetail.Text = Describe(
            secureCrtScan,
            "No sessions found in the default location. You can choose another folder.");

        PuttyOption.IsEnabled = puttyScan.Importable.Count > 0;
        SourceOptions.SelectedIndex = puttyScan.Importable.Count > 0
            ? 0
            : openSshScan.Importable.Count > 0
                ? 1
                : secureCrtScan.Importable.Count > 0
                    ? 2
                    : 1;

        PrimaryButtonClick += (_, _) =>
            SelectedSource = SourceOptions.SelectedIndex switch
            {
                0 => SessionImportSource.Putty,
                1 => SessionImportSource.OpenSsh,
                2 => SessionImportSource.SecureCrt,
                _ => null,
            };
    }

    private static string Describe(ImportScanResult scan, string emptyMessage)
    {
        if (scan.Importable.Count == 0)
            return scan.Skipped.Count == 0
                ? emptyMessage
                : $"No SSH sessions found; {scan.Skipped.Count} unsupported session(s) ignored.";

        return scan.Skipped.Count == 0
            ? $"{scan.Importable.Count} SSH session(s) found."
            : $"{scan.Importable.Count} SSH session(s) found; {scan.Skipped.Count} unsupported session(s) ignored.";
    }
}
