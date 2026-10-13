using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Identities;
using Resesh.Core.Models;
using Resesh.Core.Search;
using Resesh.Core.Storage;
using Windows.System;

namespace Resesh.App.Controls;

/// <summary>One saved session in the manager's table.</summary>
public sealed class SessionRow
{
    public SessionRow(Session session, ResolvedLogin? login)
    {
        Session = session;
        ResolvedLogin = login;
        Type = session.Kind switch
        {
            SessionKind.Local => "Local",
            SessionKind.Telnet => "Telnet",
            _ => "SSH",
        };
        Folder = session.IsLocal
            ? FolderPaths.Combine("Local", session.FolderPath)
            : session.FolderPath.Length == 0 ? "—" : session.FolderPath;
        Host = session.IsLocal
            ? Path.GetFileName(session.Local?.Executable ?? "")
            : session.Port == (session.IsTelnet ? 23 : 22) ? session.Host : $"{session.Host}:{session.Port}";
        Login = login switch
        {
            null => "",
            { Source: LoginSource.Session } => string.Join(" · ", new[]
            {
                login.Username,
                login.AuthMethod switch
                {
                    AuthMethod.PrivateKey => "own key",
                    AuthMethod.None => "no password",
                    _ => "own password",
                },
            }.Where(part => part.Length > 0)),
            { Source: LoginSource.SessionIdentity, Identity: { } identity } => identity.Name,
            { Source: LoginSource.Folder, Identity: { } identity } =>
                $"{identity.Name} · from {(login.FolderPath is { Length: > 0 } folder ? folder : "default")}",
            { IdentityMissing: true } => "Identity was deleted",
            _ => "Asks when connecting",
        };
    }

    public Session Session { get; }

    /// <summary>How the session logs in; null for telnet sessions and local profiles.</summary>
    public ResolvedLogin? ResolvedLogin { get; }

    public string Name => Session.Name;
    public string? ColorTag => Session.ColorTag;
    public string Folder { get; }
    public string Host { get; }
    public string Login { get; }
    public string Type { get; }

    public string AccessibleName =>
        string.Join(", ", new[] { Name, Type, Folder, Host, Login }.Where(part => part.Length > 0 && part != "—"));
}

/// <summary>
/// Settings &gt; Sessions: every saved session in one sortable, filterable table, with
/// actions on the selection (edit, move, set identity, duplicate, delete). Changes save as
/// they are made, through the view model of the window showing the page.
/// </summary>
public sealed partial class SessionsManagerView : UserControl
{
    private enum LoginFilterKind { Any, Own, Asks, Identity }

    private sealed class LoginFilter
    {
        public LoginFilterKind Kind { get; init; }
        public Guid? IdentityId { get; init; }
        public string Label { get; init; } = "";
    }

    private List<SessionRow> _rows = [];
    private string _sortColumn = "Name";
    private bool _descending;
    private bool _refreshing;

    /// <summary>The view model of the window showing this page; it saves every change.</summary>
    public Func<MainViewModel?>? Owner { get; set; }

    /// <summary>Opens the full editor for one session and saves the result.</summary>
    public Func<Session, Task>? EditSession { get; set; }

    public event Action<Session>? ConnectRequested;

    public SessionsManagerView()
    {
        InitializeComponent();
        KindBox.SelectedIndex = 0;
        void OnChanged() => DispatcherQueue.TryEnqueue(Refresh);
        Loaded += (_, _) =>
        {
            App.SessionsChanged += OnChanged;
            App.Identities.Changed += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            App.SessionsChanged -= OnChanged;
            App.Identities.Changed -= OnChanged;
        };
    }

    public void FocusFilter() => FilterBox.Focus(FocusState.Programmatic);

    // ---- Rows: filter, sort, selection ----

    /// <summary>Re-reads the stores and rebuilds the table, keeping the selection by session id.</summary>
    public void Refresh()
    {
        var sessions = Owner?.Invoke()?.VisibleSessions.ToList() ?? App.Store.Sessions;
        _rows = sessions
            .Select(session => new SessionRow(session, session.Kind == SessionKind.Ssh ? App.Logins.Resolve(session) : null))
            .ToList();
        RefreshLoginFilter();
        ShowRows(SelectedSessions().Select(session => session.Id).ToHashSet());
    }

    private void RefreshLoginFilter()
    {
        var current = LoginBox.SelectedItem as LoginFilter;
        var filters = new List<LoginFilter>
        {
            new() { Kind = LoginFilterKind.Any, Label = "Any login" },
            new() { Kind = LoginFilterKind.Own, Label = "Own login" },
            new() { Kind = LoginFilterKind.Asks, Label = "Asks when connecting" },
        };
        filters.AddRange(App.Identities.Identities.Select(identity => new LoginFilter
        {
            Kind = LoginFilterKind.Identity,
            IdentityId = identity.Id,
            Label = identity.Name,
        }));
        _refreshing = true;
        try
        {
            LoginBox.ItemsSource = filters;
            LoginBox.SelectedItem = filters.FirstOrDefault(f => f.Kind == current?.Kind && f.IdentityId == current.IdentityId)
                ?? filters[0];
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool Matches(SessionRow row, string query, LoginFilter login)
    {
        var kind = KindBox.SelectedIndex switch
        {
            1 => SessionKind.Ssh,
            2 => SessionKind.Telnet,
            3 => SessionKind.Local,
            _ => (SessionKind?)null,
        };
        if (kind is not null && row.Session.Kind != kind)
            return false;
        var loginMatches = login.Kind switch
        {
            LoginFilterKind.Own => row.ResolvedLogin is { Source: LoginSource.Session },
            LoginFilterKind.Asks => row.ResolvedLogin is { Source: LoginSource.None },
            LoginFilterKind.Identity => row.ResolvedLogin?.Identity?.Id == login.IdentityId,
            _ => true,
        };
        // Every term must match a session field or the Login column (identity name included).
        return loginMatches && query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            SessionSearch.Matches(row.Session, term)
            || row.Login.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowRows(HashSet<Guid> selectedIds)
    {
        var login = LoginBox.SelectedItem as LoginFilter ?? new LoginFilter();
        var query = FilterBox.Text.Trim();
        Func<SessionRow, string> key = _sortColumn switch
        {
            "Folder" => row => row.Folder,
            "Host" => row => row.Host,
            "Login" => row => row.Login,
            "Type" => row => row.Type,
            _ => row => row.Name,
        };
        var matching = _rows.Where(row => Matches(row, query, login));
        var shown = (_descending
                ? matching.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
                : matching.OrderBy(key, StringComparer.OrdinalIgnoreCase))
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _refreshing = true;
        try
        {
            SessionList.ItemsSource = shown;
            foreach (var row in shown.Where(row => selectedIds.Contains(row.Session.Id)))
                SessionList.SelectedItems.Add(row);
        }
        finally
        {
            _refreshing = false;
        }

        EmptyText.Text = _rows.Count == 0 ? "No saved sessions yet." : "No sessions match the filters.";
        EmptyText.Visibility = PresentationValues.Visible(shown.Count == 0);
        foreach (var header in new[] { NameHeader, FolderHeader, HostHeader, LoginHeader, TypeHeader })
        {
            var column = (string)header.Tag;
            var sorted = column == _sortColumn;
            header.Content = sorted ? $"{column} {(_descending ? "↓" : "↑")}" : column;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(header,
                sorted ? $"{column}, sorted {(_descending ? "descending" : "ascending")}" : $"Sort by {column}");
        }
        UpdateSelectionState();
    }

    private List<Session> SelectedSessions() =>
        SessionList.SelectedItems.OfType<SessionRow>().Select(row => row.Session).ToList();

    private void UpdateSelectionState()
    {
        var selected = SessionList.SelectedItems.Count;
        var shown = (SessionList.ItemsSource as List<SessionRow>)?.Count ?? 0;
        CountText.Text = selected > 0 ? $"{selected} of {shown} selected"
            : shown == _rows.Count ? Count(shown, "session")
            : $"{shown} of {Count(_rows.Count, "session")}";
        EditButton.IsEnabled = selected > 0;
        MoveButton.IsEnabled = selected > 0;
        IdentityButton.IsEnabled = selected > 0;
        DuplicateButton.IsEnabled = selected > 0;
        DeleteButton.IsEnabled = selected > 0;
    }

    private void ReshowRows()
    {
        if (!_refreshing)
            ShowRows(SelectedSessions().Select(session => session.Id).ToHashSet());
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ReshowRows();

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the list exists.
        if (SessionList is not null)
            ReshowRows();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var column = (string)((Button)sender).Tag;
        _descending = column == _sortColumn && !_descending;
        _sortColumn = column;
        ReshowRows();
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing)
            UpdateSelectionState();
    }

    private static SessionRow? RowOf(object? source) => source switch
    {
        ListViewItem { Content: SessionRow row } => row,
        FrameworkElement { DataContext: SessionRow row } => row,
        _ => null,
    };

    private async void SessionList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row)
            await EditOneAsync(row.Session);
    }

    private async void SessionList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SessionList.SelectedItems.Count == 0)
            return;
        if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            await DeleteSelectedAsync();
        }
        else if (e.Key is VirtualKey.Enter or VirtualKey.F2)
        {
            e.Handled = true;
            await EditSelectedAsync();
        }
    }

    private void SessionList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowOf(args.OriginalSource) is not { } row)
            return;
        args.Handled = true;
        // Right-click outside the selection retargets it, as in Explorer.
        if (!SessionList.SelectedItems.Contains(row))
            SessionList.SelectedItem = row;

        var selected = SelectedSessions();
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(selected.Count == 1 ? (selected[0].IsLocal ? "Open" : "Connect") : "Connect in Tabs", () =>
        {
            foreach (var session in selected)
                ConnectRequested?.Invoke(session);
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Edit…", () => _ = EditSelectedAsync());
        var move = new MenuFlyoutSubItem { Text = "Move to" };
        FillMoveItems(move.Items);
        menu.Items.Add(move);
        var identity = new MenuFlyoutSubItem { Text = "Set identity" };
        FillIdentityItems(identity.Items);
        menu.Items.Add(identity);
        Add("Duplicate", DuplicateSelected);
        Add("Delete", () => _ = DeleteSelectedAsync());

        if (args.TryGetPosition(SessionList, out var point))
            menu.ShowAt(SessionList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else
            menu.ShowAt((FrameworkElement)args.OriginalSource);
    }

    // ---- Actions ----

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;

        // Results dismiss themselves; an error stays until it is closed.
        _statusTimer ??= CreateStatusTimer();
        _statusTimer.Stop();
        if (severity != InfoBarSeverity.Error)
            _statusTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateStatusTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Status.IsOpen = false;
        return timer;
    }

    /// <summary>Runs a change against the owning window's view model and reports a failed save.</summary>
    private bool Save(Action<MainViewModel> change)
    {
        if (Owner?.Invoke() is not { } owner)
            return false;
        try
        {
            change(owner);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            ShowStatus(InfoBarSeverity.Error, exception.Message);
            Refresh();
            return false;
        }
    }

    private void SelectSessions(IEnumerable<Guid> ids)
    {
        var wanted = ids.ToHashSet();
        SessionList.SelectedItems.Clear();
        foreach (var row in (SessionList.ItemsSource as List<SessionRow> ?? []).Where(row => wanted.Contains(row.Session.Id)))
            SessionList.SelectedItems.Add(row);
        if (SessionList.SelectedItems.Count > 0)
            SessionList.ScrollIntoView(SessionList.SelectedItems[0]);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();

    private async Task EditOneAsync(Session session)
    {
        if (EditSession is null)
            return;
        try
        {
            await EditSession(session);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, exception.Message);
        }
        Refresh();
    }

    private async Task EditSelectedAsync()
    {
        var selected = SelectedSessions();
        if (selected.Count == 0)
            return;
        if (selected.Count == 1)
        {
            await EditOneAsync(selected[0]);
            return;
        }
        if (await BulkEditSessionsDialog.EditAsync(XamlRoot, selected) is not { } edit)
            return;
        var changed = edit.ApplyTo(selected);
        if (Save(owner => owner.UpdateSessions(changed)))
        {
            ShowStatus(InfoBarSeverity.Success, changed.Count == 0
                ? "The selected sessions already had those settings."
                : $"{Count(changed.Count, "session")} changed.");
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void DuplicateSelected()
    {
        var selected = SelectedSessions();
        IReadOnlyList<Session> copies = [];
        if (selected.Count == 0 || !Save(owner => copies = owner.DuplicateSessions(selected)))
            return;
        Refresh();
        SelectSessions(copies.Select(copy => copy.Id));
        ShowStatus(InfoBarSeverity.Success,
            copies.Count == 1 ? $"\"{copies[0].Name}\" added." : $"{copies.Count} copies added.");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedSessions();
        if (selected.Count == 0)
            return;
        var message = selected is [var only]
            ? $"Delete \"{only.Name}\"?" + (only.Kind == SessionKind.Ssh ? " Its saved credential is removed too." : "")
            : $"Delete {selected.Count} sessions? Their saved credentials are removed too.";
        if (selected.Any(session => session.BuiltIn))
            message += " Built-in local profiles return with default settings after an app restart while their shell is installed.";
        var confirm = new ConfirmDialog(selected.Count == 1 ? "Delete Session" : "Delete Sessions", message, "Delete")
        {
            XamlRoot = XamlRoot,
        };
        if (await confirm.ConfirmAsync() && Save(owner => owner.DeleteSessions(selected)))
            ShowStatus(InfoBarSeverity.Informational, $"{Count(selected.Count, "session")} deleted.");
    }

    // ---- Move ----

    private void MoveFlyout_Opening(object? sender, object e)
    {
        var flyout = (MenuFlyout)sender!;
        flyout.Items.Clear();
        FillMoveItems(flyout.Items);
    }

    private void FillMoveItems(IList<MenuFlyoutItemBase> items)
    {
        var selected = SelectedSessions();
        var scopes = selected.Select(session => session.FolderScope).Distinct().ToList();
        if (scopes.Count != 1)
        {
            // Local profiles and remote sessions live in separate folder trees.
            items.Add(new MenuFlyoutItem
            {
                Text = "Select only local profiles or only remote sessions to move them",
                IsEnabled = false,
            });
            return;
        }

        var scope = scopes[0];
        void Add(string text, string folder)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => MoveSelected(selected, folder, scope);
            items.Add(item);
        }
        Add(scope == SessionKind.Local ? "Local (top level)" : "Top level", "");
        foreach (var folder in App.Store.FoldersOf(scope))
            Add(folder, folder);
        items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "New folder…" };
        create.Click += async (_, _) =>
        {
            var path = await TextPromptDialog.PromptAsync(
                XamlRoot, "Move to New Folder", "Folder path, for example Datacenter/Rack 4", "", "Move");
            if (FolderPaths.Normalize(path ?? "") is { Length: > 0 } folder)
                MoveSelected(selected, folder, scope);
        };
        items.Add(create);
    }

    private void MoveSelected(IReadOnlyList<Session> selected, string folder, SessionKind scope)
    {
        var ids = selected.Select(session => session.Id).ToList();
        Dictionary<Guid, Guid?> Logins() => ids.Select(App.Store.Find)
            .Where(session => session is { Kind: SessionKind.Ssh })
            .ToDictionary(session => session!.Id, session => App.Logins.Resolve(session!).Identity?.Id);

        var before = Logins();
        if (!Save(owner => owner.MoveSessionsToFolder(ids, folder)))
            return;
        // Moving changes the folder a session inherits its login from; say so.
        var relogged = Logins().Count(pair => before.GetValueOrDefault(pair.Key) != pair.Value);
        var target = folder.Length > 0 ? folder : scope == SessionKind.Local ? "Local" : "the top level";
        ShowStatus(InfoBarSeverity.Success, $"{Count(selected.Count, "session")} moved to {target}."
            + (relogged > 0 ? $" The inherited login changed for {Count(relogged, "session")}." : ""));
    }

    // ---- Identity ----

    private void IdentityFlyout_Opening(object? sender, object e)
    {
        var flyout = (MenuFlyout)sender!;
        flyout.Items.Clear();
        FillIdentityItems(flyout.Items);
    }

    private void FillIdentityItems(IList<MenuFlyoutItemBase> items)
    {
        void Add(string text, SessionBulkEdit edit, string outcome)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => _ = SetLoginAsync(edit, outcome);
            items.Add(item);
        }
        Add("Inherit from folder", new SessionBulkEdit { Login = SessionLogin.Inherit },
            "inherit the folder login");
        var identities = App.Identities.Identities;
        if (identities.Count > 0)
            items.Add(new MenuFlyoutSeparator());
        foreach (var identity in identities)
        {
            Add($"{identity.Name} ({identity.Username}, {SessionEditDialog.ProofText(identity)})",
                new SessionBulkEdit { Login = SessionLogin.Identity, IdentityId = identity.Id },
                $"log in as {identity.Name}");
        }
    }

    private async Task SetLoginAsync(SessionBulkEdit edit, string outcome)
    {
        var selected = SelectedSessions();
        var changed = edit.ApplyTo(selected);
        var skipped = selected.Count(session => session.Kind != SessionKind.Ssh);
        var skippedNote = skipped > 0
            ? $" {Count(skipped, "local or telnet session")} skipped: they have no login."
            : "";
        if (changed.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational,
                skipped == selected.Count
                    ? "Only SSH sessions have a login."
                    : $"The selected sessions are already set to {outcome}.");
            return;
        }

        var ownLogins = selected.Count(session => session.Kind == SessionKind.Ssh && session.Login == SessionLogin.Own);
        if (ownLogins > 0)
        {
            var confirm = new ConfirmDialog(
                "Change Login",
                (ownLogins == 1 ? "1 selected session has its own login." : $"{ownLogins} selected sessions have their own login.")
                    + $" Their usernames and saved passwords are removed so they can {outcome}.",
                "Change Login")
            {
                XamlRoot = XamlRoot,
            };
            if (!await confirm.ConfirmAsync())
                return;
        }
        if (Save(owner => owner.UpdateSessions(changed)))
            ShowStatus(InfoBarSeverity.Success, $"{Count(changed.Count, "session")} set to {outcome}.{skippedNote}");
    }
}
