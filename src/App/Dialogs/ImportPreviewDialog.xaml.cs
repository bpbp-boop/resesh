using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Import;

namespace Resesh.App.Dialogs;

public sealed class ImportCandidateVm : ObservableObject
{
    private bool _isSelected = true;

    public ImportCandidate Candidate { get; set; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Name => Candidate.Name;

    public string Detail
    {
        get
        {
            var target = Candidate.Port == 22 ? Candidate.Host : $"{Candidate.Host}:{Candidate.Port}";
            var user = Candidate.Username.Length > 0 ? $"{Candidate.Username}@" : "";
            var folder = Candidate.FolderPath.Length > 0 ? $"  →  {Candidate.FolderPath}" : "";
            return $"{user}{target}{folder}";
        }
    }
}

public sealed partial class ImportPreviewDialog : ContentDialog
{
    public List<ImportCandidateVm> Candidates { get; }

    /// <summary>Set when the user confirms; the candidates to import.</summary>
    public IReadOnlyList<ImportCandidate>? Confirmed { get; private set; }

    public ImportPreviewDialog(ImportScanResult scan)
    {
        Candidates = scan.Importable.Select(c => new ImportCandidateVm { Candidate = c }).ToList();
        InitializeComponent();

        SummaryText.Text = $"Found {scan.Importable.Count} SSH session(s)"
            + (scan.Skipped.Count > 0 ? $" ({scan.Skipped.Count} non-SSH skipped)." : ".")
            + " Sessions whose name, host, and port already exist will be skipped as duplicates.";

        if (scan.Skipped.Count > 0)
        {
            SkippedExpander.Visibility = Visibility.Visible;
            SkippedList.ItemsSource = scan.Skipped
                .Select(c => $"{c.RelativePath} — {(c.Protocol.Length > 0 ? c.Protocol : "unknown protocol")}")
                .ToList();
        }

        IsPrimaryButtonEnabled = Candidates.Count > 0;
        PrimaryButtonClick += (_, _) =>
            Confirmed = Candidates.Where(c => c.IsSelected).Select(c => c.Candidate).ToList();
    }
}
