using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Backup;

namespace Resesh.App.Dialogs;

public sealed class BackupResolutionChoice
{
    public string Label { get; set; } = "";
    public BackupConflictResolution Resolution { get; set; }

    public BackupResolutionChoice() { }

    public BackupResolutionChoice(string label, BackupConflictResolution resolution)
    {
        Label = label;
        Resolution = resolution;
    }
}

public sealed class BackupConflictViewModel : ObservableObject
{
    private BackupResolutionChoice _selectedChoice;

    public BackupConflict Conflict { get; }
    public IReadOnlyList<BackupResolutionChoice> Choices { get; }

    public BackupConflictViewModel(BackupConflict conflict)
    {
        Conflict = conflict;
        Choices =
        [
            new BackupResolutionChoice("Keep existing", BackupConflictResolution.Keep),
            new BackupResolutionChoice("Replace", BackupConflictResolution.Replace),
            new BackupResolutionChoice("Keep both", BackupConflictResolution.Duplicate),
        ];
        _selectedChoice = Choices[0];
    }

    public BackupResolutionChoice SelectedChoice
    {
        get => _selectedChoice;
        set => SetProperty(ref _selectedChoice, value);
    }

    public string Name => Conflict.Imported.Name;

    public string Detail
    {
        get
        {
            var reason = Conflict.Match == BackupConflictMatch.SessionId
                ? "same session id"
                : "same host, port, and username";
            return $"Backup: {Endpoint(Conflict.Imported)}. Existing: {Conflict.Existing.Name} ({Endpoint(Conflict.Existing)}). Match: {reason}.";
        }
    }

    private static string Endpoint(Core.Models.Session session) => session.IsLocal
        ? session.Local?.Executable ?? "local profile"
        : $"{session.Username}@{session.Host}:{session.Port}";
}

public sealed partial class ImportBackupDialog : ContentDialog
{
    public List<BackupConflictViewModel> Conflicts { get; }
    public IReadOnlyDictionary<Guid, BackupConflictResolution>? Resolutions { get; private set; }

    public ImportBackupDialog(BackupPackage package, IReadOnlyList<BackupConflict> conflicts)
    {
        Conflicts = conflicts.Select(c => new BackupConflictViewModel(c)).ToList();
        InitializeComponent();

        SummaryText.Text = $"Backup from {package.Manifest.CreatedUtc.ToLocalTime():g}: "
            + $"{package.Sessions.Count} session(s), {package.Icons.Count} custom icon(s)"
            + (package.Manifest.IncludesSecrets ? $", {package.Secrets.Count} saved secret(s)." : ".");
        if (Conflicts.Count > 0)
        {
            ConflictHeader.Visibility = Visibility.Visible;
            ConflictList.Visibility = Visibility.Visible;
        }

        PrimaryButtonClick += (_, _) =>
            Resolutions = Conflicts.ToDictionary(c => c.Conflict.Imported.Id, c => c.SelectedChoice.Resolution);
    }
}
