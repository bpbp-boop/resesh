using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>Manage the persistent shells belonging to one saved connection.</summary>
public static class RemoteSessionsDialog
{
    /// <param name="openSlots">Slots owned by app tabs. They are labeled, and the bulk action
    /// leaves them alone so a disconnected tab can still resume its own shell.</param>
    public static async Task<int?> ShowAsync(XamlRoot root, string profileName, Guid profileId,
        IReadOnlySet<int> openSlots, Func<Task<SshCommandResult?>> query,
        Func<IReadOnlyList<int>, Task<bool>> endSessions)
    {
        string? notice = null;
        int? selectedSlot = null;
        while (true)
        {
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var list = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.Single };
            AutomationProperties.SetName(list, "Remote persistent sessions");
            AutomationProperties.SetAutomationId(list, "RemoteSessionsList");
            var refresh = new Button { Content = "Refresh" };
            AutomationProperties.SetAutomationId(refresh, "RefreshRemoteSessions");
            var endDetached = new Button { Content = "End All Detached…", IsEnabled = false };
            AutomationProperties.SetAutomationId(endDetached, "EndDetachedRemoteSessions");
            var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
            var toolbar = new Grid { ColumnSpacing = 8 };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.Children.Add(status);
            Grid.SetColumn(endDetached, 1);
            toolbar.Children.Add(endDetached);
            Grid.SetColumn(refresh, 2);
            toolbar.Children.Add(refresh);
            var content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Persistent shells for this saved connection. Closing a tab only detaches its shell; end the ones you no longer need.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    toolbar, progress, list,
                },
            };
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
                Content = content,
            };
            TmuxSessionRow.SizeDialog(dialog, content, root);
            var closed = false;
            var loading = false;
            var detachedSlots = new List<int>();
            var endAllRequested = false;
            var doubleTapped = false;
            TmuxSessionInfo? Selected() => (list.SelectedItem as ListViewItem)?.Tag as TmuxSessionInfo;
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = !loading && Selected() is not null;
                dialog.IsSecondaryButtonEnabled = !loading && Selected() is not null;
            };
            list.DoubleTapped += (_, _) =>
            {
                if (loading || Selected() is null) return;
                doubleTapped = true;
                dialog.Hide();
            };
            async Task RefreshAsync()
            {
                if (loading || closed) return;
                selectedSlot = Selected()?.Slot ?? selectedSlot;
                loading = true;
                refresh.IsEnabled = endDetached.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
                progress.Visibility = Visibility.Visible;
                status.Text = "Loading remote sessions…";
                list.Items.Clear();
                detachedSlots.Clear();
                try
                {
                    var result = await query();
                    if (closed) return;
                    if (result is not { Success: true })
                    {
                        if (result is not null && TmuxPersistence.IsServerAbsent(result))
                        {
                            status.Text = notice ?? Summary(0, 0);
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
                        var open = openSlots.Contains(session.Slot);
                        var row = TmuxSessionRow.Create(session, open);
                        list.Items.Add(row);
                        if (session.Slot == selectedSlot) list.SelectedItem = row;
                        if (!open && session.AttachedClients == 0) detachedSlots.Add(session.Slot);
                    }
                    status.Text = notice ?? Summary(sessions.Count, detachedSlots.Count);
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
                    endDetached.IsEnabled = detachedSlots.Count > 0;
                    progress.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = Selected() is not null;
                }
            }
            refresh.Click += async (_, _) => await RefreshAsync();
            endDetached.Click += (_, _) =>
            {
                endAllRequested = true;
                dialog.Hide();
            };
            dialog.Opened += async (_, _) => await RefreshAsync();
            dialog.Closed += (_, _) => closed = true;
            var action = await dialog.ShowModalAsync();
            var selected = Selected();

            if (endAllRequested)
            {
                var slots = detachedSlots.ToList();
                var one = slots.Count == 1;
                if (await ConfirmEndAsync(root,
                        one ? "End Detached Session" : $"End {slots.Count} Detached Sessions",
                        $"End {(one ? "the detached shell" : $"all {slots.Count} detached shells")} for {profileName}? "
                        + "Anything running inside them is terminated. Shells open in a tab or attached somewhere else keep running.",
                        one ? "End Session" : "End Sessions"))
                    notice = await TryEndAsync(slots, one ? "Ended 1 detached session." : $"Ended {slots.Count} detached sessions.");
                continue;
            }
            if (selected is null) return null;
            if (action == ContentDialogResult.Primary || doubleTapped) return selected.Slot;
            if (action != ContentDialogResult.Secondary) return null;
            selectedSlot = selected.Slot;

            if (await ConfirmEndAsync(root, "End Remote Session",
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

    private static async Task<bool> ConfirmEndAsync(XamlRoot root, string title, string message, string primary)
    {
        var confirm = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await confirm.ShowModalAsync() == ContentDialogResult.Primary;
    }
}
