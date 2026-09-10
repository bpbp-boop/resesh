using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Manage the persistent shells belonging to one saved connection.</summary>
public static class RemoteSessionsDialog
{
    public static async Task<int?> ShowAsync(XamlRoot root, string profileName, Guid profileId,
        Func<Task<SshCommandResult?>> query, Func<int, Task<bool>> endSession)
    {
        string? notice = null;
        int? selectedSlot = null;
        while (true)
        {
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var list = new ListView { Height = 300, SelectionMode = ListViewSelectionMode.Single };
            AutomationProperties.SetName(list, "Remote persistent sessions");
            AutomationProperties.SetAutomationId(list, "RemoteSessionsList");
            var refresh = new Button { Content = "Refresh" };
            AutomationProperties.SetAutomationId(refresh, "RefreshRemoteSessions");
            var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
            var dialog = new ContentDialog
            {
                Title = $"Remote Sessions — {profileName}",
                XamlRoot = root,
                PrimaryButtonText = "Resume",
                SecondaryButtonText = "End Session…",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false,
                Content = new StackPanel
                {
                    MinWidth = 460, Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Persistent shells for this saved connection.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        refresh, progress, status, list,
                    },
                },
            };
            var closed = false;
            var loading = false;
            TmuxSessionInfo? Selected() => (list.SelectedItem as ListViewItem)?.Tag as TmuxSessionInfo;
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = !loading && Selected() is not null;
                dialog.IsSecondaryButtonEnabled = !loading && Selected() is not null;
            };
            async Task RefreshAsync()
            {
                if (loading || closed) return;
                selectedSlot = Selected()?.Slot ?? selectedSlot;
                loading = true;
                refresh.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
                progress.Visibility = Visibility.Visible;
                status.Text = "Loading remote sessions…";
                list.Items.Clear();
                try
                {
                    var result = await query();
                    if (closed) return;
                    if (result is not { Success: true })
                    {
                        if (result is not null && TmuxPersistence.IsServerAbsent(result))
                        {
                            status.Text = notice ?? "No persistent sessions found for this connection.";
                            notice = null;
                            return;
                        }
                        status.Text = "Could not list remote sessions. Check the connection and refresh. "
                            + result?.Error.Trim();
                        return;
                    }
                    var sessions = TmuxPersistence.ParseManagedSessions(result.Output, profileId);
                    foreach (var session in sessions)
                    {
                        var row = TmuxSessionRow.Create(session);
                        list.Items.Add(row);
                        if (session.Slot == selectedSlot) list.SelectedItem = row;
                    }
                    status.Text = notice ?? (sessions.Count == 0 ? "No persistent sessions found for this connection." : $"{sessions.Count} remote session(s)");
                    notice = null;
                }
                catch (Exception exception)
                {
                    if (!closed) status.Text = $"Could not list remote sessions: {exception.Message}";
                }
                finally
                {
                    loading = false;
                    refresh.IsEnabled = true;
                    progress.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = Selected() is not null;
                }
            }
            refresh.Click += async (_, _) => await RefreshAsync();
            dialog.Opened += async (_, _) => await RefreshAsync();
            dialog.Closed += (_, _) => closed = true;
            var action = await dialog.ShowModalAsync();
            var selected = Selected();
            if (action == ContentDialogResult.None || selected is null) return null;
            if (action == ContentDialogResult.Primary) return selected.Slot;
            selectedSlot = selected.Slot;

            var confirm = new ContentDialog
            {
                Title = "End Remote Session",
                Content = $"End {selected.Name} ({selected.CurrentCommand}) in {selected.CurrentPath}? Anything running inside this session will be terminated, including work in attached clients.",
                PrimaryButtonText = "End Session",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = root,
            };
            if (await confirm.ShowModalAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    notice = await endSession(selected.Slot)
                        ? "Remote session ended."
                        : "Could not end the remote session. Check the connection and refresh.";
                }
                catch (Exception exception)
                {
                    notice = $"Could not end the remote session: {exception.Message}";
                }
            }
        }
    }

}
