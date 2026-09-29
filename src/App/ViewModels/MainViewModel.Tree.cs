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
        foreach (var session in SessionsOf(SelectionOrCurrent(nodes)).ToList())
            Services.ConnectSession(session);
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelection))]
    private void OpenSelectionInNewWindow(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        Services.ConnectInNewWindow(SessionsOf(SelectionOrCurrent(nodes)).ToList());

    private bool CanOpenSelection(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SessionsOf(SelectionOrCurrent(nodes)).Any();

    /// <summary>Renames a folder or opens a session's editor, as the context menu does.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task EditSelectionAsync(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SelectionOrCurrent(nodes) switch
        {
            [{ Session: { IsLocal: true } profile }] => Services.EditLocalProfileAsync(profile, profile.FolderPath),
            [{ Session: { } session }] => Services.EditSessionAsync(session, session.FolderPath),
            [{ IsFolder: true, IsLocalRoot: false } folder] => RenameFolderAsync(folder),
            _ => Task.CompletedTask,
        };

    private bool CanEditSelection(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SelectionOrCurrent(nodes) is [{ Session: not null }] or [{ IsFolder: true, IsLocalRoot: false }];

    /// <summary>Deletes the selection. Each path confirms first; the Local root is never deleted.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelection),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task DeleteSelectionAsync(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SelectionOrCurrent(nodes) switch
        {
            [{ Session: { } session }] => DeleteSessionAsync(session),
            [{ IsFolder: true, IsLocalRoot: false } folder] => DeleteFolderAsync(folder),
            var items => DeleteNodesAsync(items),
        };

    private bool CanDeleteSelection(IReadOnlyList<TreeNodeViewModel>? nodes) =>
        SelectionOrCurrent(nodes) is { Count: > 0 } items && !items.All(node => node.IsLocalRoot);

    // ---- Folder and profile commands (context menu) ----

    /// <summary>A new session inside the folder: a local profile in the Local scope, SSH elsewhere.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task NewSessionInFolderAsync(TreeNodeViewModel folder) => folder.IsLocalScope
        ? Services.EditLocalProfileAsync(existing: null, defaultFolder: folder.FolderPath)
        : Services.EditSessionAsync(existing: null, defaultFolder: folder.FolderPath);

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private async Task NewSubfolderAsync(TreeNodeViewModel folder)
    {
        var location = folder.IsLocalRoot ? "under Local" : $"inside {folder.FolderPath}";
        var name = await Services.PromptAsync("New Folder", $"Folder name ({location})", "");
        if (!string.IsNullOrWhiteSpace(name))
            CreateFolder(FolderPaths.Combine(folder.FolderPath, name), KindOf(folder));
    }

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private async Task RenameFolderAsync(TreeNodeViewModel folder)
    {
        var name = await Services.PromptAsync("Rename Folder", "New name", folder.Name);
        if (string.IsNullOrWhiteSpace(name) || name == folder.Name)
            return;
        var newPath = FolderPaths.Combine(FolderPaths.Parent(folder.FolderPath), name);
        RenameFolder(folder.FolderPath, newPath, KindOf(folder));
    }

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
        var affected = SessionsOf(items).Count();

        var parts = new List<string>();
        if (folders.Count > 0)
            parts.Add($"{folders.Count} folder(s)");
        if (sessions.Count > 0)
            parts.Add($"{sessions.Count} session(s)");
        var message = $"Delete {string.Join(" and ", parts)}?"
            + (affected > 0 ? $" {affected} session(s) will be removed; their saved credentials are removed too." : "");
        if (!await Services.ConfirmAsync("Delete Selection", message))
            return;

        // Folders first; sessions already removed with a folder become harmless no-ops.
        foreach (var folder in folders)
            DeleteFolder(folder.FolderPath, KindOf(folder));
        foreach (var node in sessions)
            DeleteSession(node.Session!);
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
                return
                [
                    new("Connect in Tabs", OpenSelectionCommand, selection),
                    new("Open in new window", OpenSelectionInNewWindowCommand, selection),
                    MenuEntry.Separator,
                    new("Expand All", ExpandFolderCommand, folder),
                    new("Collapse All", CollapseFolderCommand, folder),
                    MenuEntry.Separator,
                    new(folder.IsLocalScope ? "New Local Profile…" : "New Session…", NewSessionInFolderCommand, folder),
                    new("New Folder…", NewSubfolderCommand, folder),
                    MenuEntry.Separator,
                    new("Rename…", RenameFolderCommand, folder),
                    new("Delete", DeleteSelectionCommand, selection),
                ];

            // Mixed selections get the shared subset.
            default:
                return
                [
                    new("Connect in Tabs", OpenSelectionCommand, selection),
                    new("Open in new window", OpenSelectionInNewWindowCommand, selection),
                    MenuEntry.Separator,
                    new("Delete", DeleteSelectionCommand, selection),
                ];
        }
    }
}
