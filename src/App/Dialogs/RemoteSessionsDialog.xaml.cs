using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Manage the persistent shells belonging to one saved connection.</summary>
public sealed partial class RemoteSessionsDialog : ContentDialog
{
    private readonly Guid _profileId;
    private readonly IReadOnlySet<int> _openSlots;
    private readonly Func<Task<SshCommandResult?>> _query;
    private string? _notice;
    private int? _selectedSlot;
    private bool _closed;
    private bool _loading;

    public ObservableCollection<TmuxSessionItem> Items { get; } = [];

    /// <summary>Detached shells not owned by a tab, as of the last refresh.</summary>
    public List<int> DetachedSlots { get; } = [];

    public bool EndAllRequested { get; private set; }
    public bool WasDoubleTapped { get; private set; }

    public TmuxSessionInfo? Selected => (SessionList.SelectedItem as TmuxSessionItem)?.Session;

    private RemoteSessionsDialog(string profileName, Guid profileId, IReadOnlySet<int> openSlots,
        Func<Task<SshCommandResult?>> query, string? notice, int? selectedSlot)
    {
        _profileId = profileId;
        _openSlots = openSlots;
        _query = query;
        _notice = notice;
        _selectedSlot = selectedSlot;
        InitializeComponent();
        Title = $"Remote Sessions — {profileName}";
    }

    /// <param name="openSlots">Slots owned by app tabs. They are labeled, and the bulk action
    /// leaves them alone so a disconnected tab can still resume its own shell.</param>
    /// <returns>The slot to resume, or null when closed.</returns>
    public static async Task<int?> ManageAsync(XamlRoot root, string profileName, Guid profileId,
        IReadOnlySet<int> openSlots, Func<Task<SshCommandResult?>> query,
        Func<IReadOnlyList<int>, Task<bool>> endSessions)
    {
        string? notice = null;
        int? selectedSlot = null;
        // Ending shells needs a confirmation dialog, and WinUI shows one dialog at a time,
        // so the list closes, asks, and reopens with the result as its status.
        while (true)
        {
            var dialog = new RemoteSessionsDialog(profileName, profileId, openSlots, query, notice, selectedSlot)
            {
                XamlRoot = root,
            };
            DialogSizing.FitContent(dialog, dialog.Body, root);
            var action = await dialog.ShowModalAsync();
            var selected = dialog.Selected;
            notice = null;

            if (dialog.EndAllRequested)
            {
                var slots = dialog.DetachedSlots.ToList();
                var one = slots.Count == 1;
                if (await ConfirmDialog.ConfirmAsync(root,
                        one ? "End Detached Session" : $"End {slots.Count} Detached Sessions",
                        $"End {(one ? "the detached shell" : $"all {slots.Count} detached shells")} for {profileName}? "
                        + "Anything running inside them is terminated. Shells open in a tab or attached somewhere else keep running.",
                        one ? "End Session" : "End Sessions"))
                    notice = await TryEndAsync(slots, one ? "Ended 1 detached session." : $"Ended {slots.Count} detached sessions.");
                continue;
            }
            if (selected is null) return null;
            if (action == ContentDialogResult.Primary || dialog.WasDoubleTapped) return selected.Slot;
            if (action != ContentDialogResult.Secondary) return null;
            selectedSlot = selected.Slot;

            if (await ConfirmDialog.ConfirmAsync(root, "End Remote Session",
                    $"End the shell in {selected.CurrentPath} ({selected.CurrentCommand})? Anything running inside it will be terminated, including work in attached clients.",
                    "End Session"))
                notice = await TryEndAsync([selected.Slot], "Remote session ended.");
        }

        async Task<string> TryEndAsync(IReadOnlyList<int> slots, string success)
        {
            try
            {
                return await endSessions(slots)
                    ? success
                    : "Could not end every session. Check the connection and refresh.";
            }
            catch (Exception exception)
            {
                return $"Could not end the remote session: {exception.Message}";
            }
        }
    }

    private static string Summary(int total, int detached) => total switch
    {
        0 => "No persistent sessions are running for this connection.",
        1 => detached == 1 ? "1 session, detached." : "1 session.",
        _ => detached == 0 ? $"{total} sessions." : $"{total} sessions, {detached} detached.",
    };

    private async Task RefreshAsync()
    {
        if (_loading || _closed) return;
        _selectedSlot = Selected?.Slot ?? _selectedSlot;
        _loading = true;
        RefreshButton.IsEnabled = EndDetachedButton.IsEnabled = false;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Loading remote sessions…";
        Items.Clear();
        DetachedSlots.Clear();
        try
        {
            var result = await _query();
            if (_closed) return;
            if (result is not { Success: true })
            {
                if (result is not null && TmuxPersistence.IsServerAbsent(result))
                {
                    StatusText.Text = _notice ?? Summary(0, 0);
                    _notice = null;
                    return;
                }
                StatusText.Text = "Could not list remote sessions. Check the connection and refresh. "
                    + result?.Error.Trim();
                return;
            }
            var sessions = TmuxPersistence.ParseManagedSessions(result.Output, _profileId);
            foreach (var session in sessions)
            {
                var open = _openSlots.Contains(session.Slot);
                var item = TmuxSessionItem.ForSession(session, open);
                Items.Add(item);
                if (session.Slot == _selectedSlot) SessionList.SelectedItem = item;
                if (!open && session.AttachedClients == 0) DetachedSlots.Add(session.Slot);
            }
            StatusText.Text = _notice ?? Summary(sessions.Count, DetachedSlots.Count);
            _notice = null;
        }
        catch (Exception exception)
        {
            if (!_closed) StatusText.Text = $"Could not list remote sessions: {exception.Message}";
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            EndDetachedButton.IsEnabled = DetachedSlots.Count > 0;
            Progress.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = Selected is not null;
        }
    }

    private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) => await RefreshAsync();

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => _closed = true;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void EndDetached_Click(object sender, RoutedEventArgs e)
    {
        EndAllRequested = true;
        Hide();
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = !_loading && Selected is not null;

    private void SessionList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_loading || Selected is null) return;
        WasDoubleTapped = true;
        Hide();
    }
}
