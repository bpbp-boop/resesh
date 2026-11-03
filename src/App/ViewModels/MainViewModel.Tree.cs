using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>One item of a context menu the view materializes: a command, or a separator.</summary>
public sealed record MenuEntry(string Text, ICommand? Command = null, object? Parameter = null)
{
    public static readonly MenuEntry Separator = new("");

    public bool IsSeparator => Command is null;
}

// Session tree selection and the actions on it. The view owns pointer, focus, and drag
// mechanics; everything that decides what an action does lives here.
public sealed partial class MainViewModel
{
    /// <summary>Explorer-style tree selection (click, Ctrl+click toggle, Shift+click range).</summary>
    public OrderedSelection<TreeNodeViewModel> TreeSelection { get; }

    /// <summary>Nodes in display order, skipping children of collapsed folders.</summary>
    public IEnumerable<TreeNodeViewModel> VisibleNodes()
    {
        static IEnumerable<TreeNodeViewModel> Walk(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                if (node.IsFolder && node.IsExpanded)
                    foreach (var child in Walk(node.Children))
                        yield return child;
            }
        }
        return Walk(RootNodes);
    }

    /// <summary>Drops selected nodes that a rebuild removed from the tree.</summary>
    public void PruneTreeSelection() => TreeSelection.RemoveWhere(node => !IsInTree(node));

    /// <summary>All sessions under a folder node, in display order (subfolders first, recursively).</summary>
    private static IEnumerable<Session> SessionsUnder(TreeNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            if (child.Session is { } session)
                yield return session;
            else
                foreach (var nested in SessionsUnder(child))
                    yield return nested;
        }
    }

    /// <summary>Sessions of the selection: selected sessions plus everything under selected folders, deduplicated.</summary>
    public static IEnumerable<Session> SessionsOf(IEnumerable<TreeNodeViewModel> nodes)
    {
        var seen = new HashSet<Guid>();
        foreach (var node in nodes)
        {
            if (node.Session is { } session)
            {
                if (seen.Add(session.Id))
                    yield return session;
            }
            else
            {
                foreach (var nested in SessionsUnder(node))
                    if (seen.Add(nested.Id))
                        yield return nested;
            }
        }
    }

    private static SessionKind KindOf(TreeNodeViewModel node) =>
        node.IsLocalScope ? SessionKind.Local : SessionKind.Ssh;

    /// <summary>The given nodes, or a snapshot of the selection: the live list mutates
    /// before a menu item's Click fires.</summary>
    private IReadOnlyList<TreeNodeViewModel> SelectionOrCurrent(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        nodes ?? TreeSelection.Items.ToList();

    // ---- Selection commands (Enter, F2, Delete, and the context menu) ----

    [RelayCommand(CanExecute = nameof(CanOpenSelection))]
    private void OpenSelection(IReadOnlyList<TreeNodeViewModel>? nodes)
    {
        foreach (var session in SessionsToOpen(nodes))
            Services.ConnectSession(session);
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelection))]
    private void OpenSelectionInNewWindow(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        Services.ConnectInNewWindow(SessionsToOpen(nodes));

    /// <summary>Opens each session in a new pane of its own.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelection))]
    private void OpenSelectionInNewSplits(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        Services.ConnectInNewSplits(SessionsToOpen(nodes));

    /// <summary>Opens the sessions together, as the tabs of one new pane.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelection))]
    private void OpenSelectionInOneNewSplit(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        Services.ConnectInNewSplit(SessionsToOpen(nodes));

    /// <summary>Deals the sessions across the panes already open, one each in pane order,
    /// starting over at the first pane when there are more sessions than panes.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSelectionInExistingSplits))]
    private void OpenSelectionInExistingSplits(IReadOnlyList<TreeNodeViewModel>? nodes)
    {
        var groups = Groups.ToList();
        var sessions = SessionsToOpen(nodes);
        for (var index = 0; index < sessions.Count; index++)
            Services.ConnectSession(sessions[index], groups[index % groups.Count]);
    }

    private bool CanOpenSelection(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SessionsOf(SelectionOrCurrent(nodes)).Any();

    /// <summary>The saved records of the selection's sessions: nodes hold copies, and a copy
    /// must never be what connects.</summary>
    private List<Session> SessionsToOpen(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SessionsOf(SelectionOrCurrent(nodes)).Select(session => CurrentSession(session.Id)).OfType<Session>().ToList();

    private bool CanOpenSelectionInExistingSplits(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        IsSplit && CanOpenSelection(nodes);

    /// <summary>Renames a folder or opens a session's editor, as the context menu does.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task EditSelectionAsync(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        ReportingStoreFailures(() => SelectionOrCurrent(nodes) switch
        {
            // The editor starts from the saved record, not the node's copy of it.
            [{ Session: { } node }] => CurrentSession(node.Id) switch
            {
                { IsLocal: true } profile => Services.EditLocalProfileAsync(profile, profile.FolderPath),
                { } session => Services.EditSessionAsync(session, session.FolderPath),
                null => Task.CompletedTask,
            },
            [{ IsFolder: true, IsLocalRoot: false } folder] => RenameFolderAsync(folder),
            _ => Task.CompletedTask,
        });

    private bool CanEditSelection(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SelectionOrCurrent(nodes) is [{ Session: not null }] or [{ IsFolder: true, IsLocalRoot: false }];

    /// <summary>Deletes the selection. Each path confirms first; the Local root is never deleted.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelection),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task DeleteSelectionAsync(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        ReportingStoreFailures(() => SelectionOrCurrent(nodes) switch
        {
            [{ Session: { } session }] => DeleteSessionAsync(session),
            [{ IsFolder: true, IsLocalRoot: false } folder] => DeleteFolderAsync(folder),
            var items => DeleteNodesAsync(items),
        });

    private bool CanDeleteSelection(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SelectionOrCurrent(nodes) is { Count: > 0 } items && !items.All(node => node.IsLocalRoot);

    // ---- Folder and profile commands (context menu) ----

    /// <summary>A new session inside the folder: a local profile in the Local scope, SSH elsewhere.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task NewSessionInFolderAsync(TreeNodeViewModel folder) => ReportingStoreFailures(() => folder.IsLocalScope
        ? Services.EditLocalProfileAsync(existing: null, defaultFolder: folder.FolderPath)
        : Services.EditSessionAsync(existing: null, defaultFolder: folder.FolderPath));

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task NewSubfolderAsync(TreeNodeViewModel folder) => ReportingStoreFailures(async () =>
    {
        var location = folder.IsLocalRoot ? "under Local" : $"inside {folder.FolderPath}";
        var name = await Services.PromptAsync("New Folder", $"Folder name ({location})", "");
        if (!string.IsNullOrWhiteSpace(name))
            CreateFolder(FolderPaths.Combine(folder.FolderPath, name), KindOf(folder));
    });

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task RenameFolderAsync(TreeNodeViewModel folder) => ReportingStoreFailures(async () =>
    {
        if (await PromptFolderNameAsync(folder.Name) is not { } name || name == folder.Name)
            return;
        var newPath = FolderPaths.Combine(FolderPaths.Parent(folder.FolderPath), name);
        RenameFolder(folder.FolderPath, newPath, KindOf(folder));
    });

    /// <summary>Asks for a folder's new name, trimmed; null when cancelled or left empty. A
    /// separator would move the folder instead of renaming it (the path nests), so a name
    /// with one is refused and asked for again.</summary>
    private async Task<string?> PromptFolderNameAsync(string current)
    {
        var placeholder = "New name";
        var initial = current;
        while (true)
        {
            var name = (await Services.PromptAsync("Rename Folder", placeholder, initial))?.Trim();
            if (string.IsNullOrEmpty(name))
                return null;
            if (name.IndexOfAny(['/', '\\']) < 0)
                return name;
            placeholder = "A folder name cannot contain / or \\";
            initial = name;
        }
    }

    /// <summary>Runs a tree command that writes the session store. The tree's keys, the
    /// context menu, forwarded shortcuts and the palette all run these through the command,
    /// so a save that fails (sessions.json locked or read-only) is reported here, the way a
    /// failed drag-move is, instead of escaping as an unobserved task exception.</summary>
    private async Task ReportingStoreFailures(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _environment.ReportError(exception);
        }
    }

    /// <summary>Attaches an identity to a remote folder; local folders have no logins.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task SetFolderIdentityAsync(TreeNodeViewModel folder) =>
        ReportingStoreFailures(() => Services.SetFolderIdentityAsync(folder.FolderPath));

    [RelayCommand]
    private void SetDefaultLocalProfile(Session profile) => _environment.SetDefaultLocalProfile(profile.Id);

    [RelayCommand]
    private void ExpandFolder(TreeNodeViewModel folder)
    {
        SetExpansionUnder(folder, true);
        Services.SyncTreeExpansion(); // nested containers realize lazily; push state as they appear
    }

    [RelayCommand]
    private void CollapseFolder(TreeNodeViewModel folder)
    {
        SetExpansionUnder(folder, false);
        Services.SyncTreeExpansion();
    }

    private async Task DeleteFolderAsync(TreeNodeViewModel folder)
    {
        var count = CountSessionsUnder(folder.FolderPath, KindOf(folder));
        var what = folder.IsLocalScope ? "profile(s)" : "session(s)";
        var confirmed = await Services.ConfirmAsync(
            "Delete Folder",
            count == 0
                ? $"Delete the folder \"{folder.Name}\"?"
                : $"Delete the folder \"{folder.Name}\" and the {count} {what} inside it?"
                    + (folder.IsLocalScope ? "" : " Their saved credentials are removed too."));
        if (confirmed)
            DeleteFolder(folder.FolderPath, KindOf(folder));
    }

    private async Task DeleteSessionAsync(Session session)
    {
        var confirmed = await Services.ConfirmAsync(
            "Delete Session",
            session.IsLocal
                ? $"Delete the local profile \"{session.Name}\"?"
                    + (session.BuiltIn ? " (It returns with default settings after an app restart while its shell is installed.)" : "")
                : session.IsSerial
                    ? $"Delete \"{session.Name}\" ({session.Serial?.PortName})?"
                : session.IsTelnet
                    ? $"Delete \"{session.Name}\" ({session.Host})?"
                    : $"Delete \"{session.Name}\" ({session.Host})? Its saved credential is removed too.");
        if (confirmed)
            DeleteSession(session);
    }

    private async Task DeleteNodesAsync(IReadOnlyList<TreeNodeViewModel> items)
    {
        // The virtual Local root is never deletable, even inside a multi-selection.
        items = items.Where(n => !n.IsLocalRoot).ToList();
        var folders = items.Where(n => n.IsFolder).ToList();
        var sessions = items.Where(n => !n.IsFolder).ToList();
        // Counted from the store with the deletes' own rules: a folder takes every session
        // under it, including ones a filter hides from the tree.
        var affected = folders
            .SelectMany(folder => _store.SessionsInFolder(folder.FolderPath, KindOf(folder)))
            .Concat(sessions.Select(node => _store.Find(node.Session!.Id)).OfType<Session>())
            .Select(session => session.Id)
            .Distinct()
            .Count();

        var parts = new List<string>();
        if (folders.Count > 0)
            parts.Add($"{folders.Count} folder(s)");
        if (sessions.Count > 0)
            parts.Add($"{sessions.Count} session(s)");
        var message = $"Delete {string.Join(" and ", parts)}?"
            + (affected > 0 ? $" {affected} session(s) will be removed; their saved credentials are removed too." : "");
        if (!await Services.ConfirmAsync("Delete Selection", message))
            return;

        // One write for the whole selection, as Settings > Sessions deletes: a failed save
        // leaves everything in place instead of only what came after the failure.
        DeleteFoldersAndSessions(
            folders.Select(folder => (folder.FolderPath, KindOf(folder))).ToList(),
            sessions.Select(node => node.Session!.Id));
    }

    // ---- Context menu ----

    /// <summary>The context menu for the current selection: session, local profile, the
    /// Local root, one folder, or a mixed selection. Empty when nothing is selected.</summary>
    public IReadOnlyList<MenuEntry> BuildSelectionMenu()
    {
        var selection = TreeSelection.Items.ToList();
        switch (selection)
        {
            case []:
                return [];

            // Single local profile: process verbs, local editor, default-profile toggle.
            case [{ Session: { IsLocal: true } profile }]:
            {
                var menu = new List<MenuEntry>
                {
                    new("Open", OpenSelectionCommand, selection),
                    new("Open in new window", OpenSelectionInNewWindowCommand, selection),
                    new("Open in new split", OpenSelectionInNewSplitsCommand, selection),
                    MenuEntry.Separator,
                    new("Edit…", EditSelectionCommand, selection),
                };
                if (_environment.DefaultLocalProfileId() != profile.Id)
                    menu.Add(new("Set as Default", SetDefaultLocalProfileCommand, profile));
                menu.Add(new("Delete", DeleteSelectionCommand, selection));
                return menu;
            }

            case [{ Session: not null }]:
                return
                [
                    new("Connect", OpenSelectionCommand, selection),
                    new("Connect in new window", OpenSelectionInNewWindowCommand, selection),
                    new("Connect in new split", OpenSelectionInNewSplitsCommand, selection),
                    MenuEntry.Separator,
                    new("Edit…", EditSelectionCommand, selection),
                    new("Delete", DeleteSelectionCommand, selection),
                ];

            // The permanent Local root: creators and expansion only — never rename/delete/move.
            case [{ IsLocalRoot: true } localRoot]:
                return
                [
                    new("New Local Profile…", NewSessionInFolderCommand, localRoot),
                    new("New Folder…", NewSubfolderCommand, localRoot),
                    MenuEntry.Separator,
                    new("Expand All", ExpandFolderCommand, localRoot),
                    new("Collapse All", CollapseFolderCommand, localRoot),
                ];

            case [{ IsFolder: true } folder]:
            {
                var menu = new List<MenuEntry>
                {
                    new("Connect in Tabs", OpenSelectionCommand, selection),
                    new("Open in new window", OpenSelectionInNewWindowCommand, selection),
                    new("Open in one new split", OpenSelectionInOneNewSplitCommand, selection),
                    new("Open in new splits", OpenSelectionInNewSplitsCommand, selection),
                    new("Open in existing splits", OpenSelectionInExistingSplitsCommand, selection),
                    MenuEntry.Separator,
                    new("Expand All", ExpandFolderCommand, folder),
                    new("Collapse All", CollapseFolderCommand, folder),
                    MenuEntry.Separator,
                    new(folder.IsLocalScope ? "New Local Profile…" : "New Session…", NewSessionInFolderCommand, folder),
                    new("New Folder…", NewSubfolderCommand, folder),
                    MenuEntry.Separator,
                };
                if (!folder.IsLocalScope)
                    menu.Add(new("Set Identity…", SetFolderIdentityCommand, folder));
                menu.Add(new("Rename…", RenameFolderCommand, folder));
                menu.Add(new("Delete", DeleteSelectionCommand, selection));
                return menu;
            }

            // Mixed selections get the shared subset.
            default:
                return
                [
                    new("Connect in Tabs", OpenSelectionCommand, selection),
                    new("Open in new window", OpenSelectionInNewWindowCommand, selection),
                    new("Open in one new split", OpenSelectionInOneNewSplitCommand, selection),
                    new("Open in new splits", OpenSelectionInNewSplitsCommand, selection),
                    new("Open in existing splits", OpenSelectionInExistingSplitsCommand, selection),
                    MenuEntry.Separator,
                    new("Delete", DeleteSelectionCommand, selection),
                ];
        }
    }
}
