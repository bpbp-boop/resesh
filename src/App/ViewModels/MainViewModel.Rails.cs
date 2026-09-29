using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Resesh.App.ViewModels;

// The Recent sessions and Recordings panes of the sessions rail.
public sealed partial class MainViewModel
{
    private const int RecentSessionLimit = 12;
    private const int RecordingLimit = 200;

    private int _recordingsLoadVersion;
    private string _recordingsStatusText = "No recordings found in the configured folder.";
    private bool _showRecordingsStatus = true;

    public ObservableCollection<TreeNodeViewModel> RecentSessions { get; } = [];

    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = [];

    public bool HasNoRecentSessions => RecentSessions.Count == 0;

    /// <summary>Loading, empty, or error text for the Recordings pane.</summary>
    public string RecordingsStatusText
    {
        get => _recordingsStatusText;
        private set => SetProperty(ref _recordingsStatusText, value);
    }

    public bool ShowRecordingsStatus
    {
        get => _showRecordingsStatus;
        private set => SetProperty(ref _showRecordingsStatus, value);
    }

    /// <summary>Lists recordings in a folder. Replaceable so tests can control timing.</summary>
    internal Func<string, IReadOnlyList<RecordingItemViewModel>> RecordingLoader { get; set; } = LoadRecordings;

    private static IReadOnlyList<RecordingItemViewModel> LoadRecordings(string directory)
    {
        var folder = new DirectoryInfo(directory);
        if (!folder.Exists)
            return [];
        return folder.EnumerateFiles("*.cast", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(RecordingLimit)
            .Select(RecordingItemViewModel.FromFile)
            .ToList();
    }

    /// <summary>Rebuilds the Recent pane from the persisted list, dropping sessions that
    /// were deleted and hiding ones that are not visible right now.</summary>
    public void RefreshRecentSessions()
    {
        var saved = _environment.RecentSessionIds();
        var persisted = saved
            .Where(id => _store.Find(id) is not null)
            .Distinct()
            .Take(RecentSessionLimit)
            .ToList();
        var visible = VisibleSessions.ToDictionary(session => session.Id);

        RecentSessions.Clear();
        foreach (var id in persisted)
        {
            if (visible.TryGetValue(id, out var session))
                RecentSessions.Add(TreeNodeViewModel.ForSession(session));
        }
        OnPropertyChanged(nameof(HasNoRecentSessions));

        if (!persisted.SequenceEqual(saved))
            _environment.SaveRecentSessionIds(persisted);
    }

    [RelayCommand]
    private void ConnectRecent(TreeNodeViewModel? node)
    {
        if (node?.Session is { } session)
            Services.ConnectSession(session);
    }

    /// <summary>Lists the recording folder. A refresh that finishes after a newer one
    /// started is discarded, so a slow folder cannot overwrite a newer result.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task RefreshRecordingsAsync()
    {
        var loadVersion = ++_recordingsLoadVersion;
        RecordingsStatusText = "Loading recordings…";
        ShowRecordingsStatus = Recordings.Count == 0;

        try
        {
            var directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_environment.RecordingDirectory()));
            var loader = RecordingLoader;
            var items = await Task.Run(() => loader(directory));
            if (loadVersion != _recordingsLoadVersion)
                return;

            Recordings.Clear();
            foreach (var item in items)
                Recordings.Add(item);
            RecordingsStatusText = "No recordings found in the configured folder.";
            ShowRecordingsStatus = Recordings.Count == 0;
        }
        catch (Exception exception)
        {
            if (loadVersion != _recordingsLoadVersion)
                return;
            Recordings.Clear();
            RecordingsStatusText = $"Recordings could not be listed.\n{exception.Message}";
            ShowRecordingsStatus = true;
        }
    }
}
