using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>Tree rebuilds patch the bound collections in place so the TreeView keeps its
/// realized containers (and scroll position) instead of being cleared and repopulated.</summary>
public sealed class SessionTreeReconcileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-tree-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    private MainViewModel Window() => new(
        new SessionStore(Path.Combine(_directory, Guid.NewGuid() + ".json")), new Credentials(),
        new ViewModelEnvironment
        {
            CurrentTheme = () => "dark",
            ResolveTheme = theme => theme,
            ShowAgentIcons = () => true,
            IsSessionVisible = _ => true,
            ApplySessionSettings = _ => { },
            ReportError = error => throw error,
        });

    [Fact]
    public void AddingSession_KeepsExistingNodesAndInsertsInOrder()
    {
        var window = Window();
        window.AddSession(new Session { Name = "alpha", FolderPath = "prod" }, null);
        window.AddSession(new Session { Name = "gamma", FolderPath = "prod" }, null);
        var before = AllNodes(window.RootNodes);
        var resets = CountResets(window.RootNodes);

        window.AddSession(new Session { Name = "beta", FolderPath = "prod" }, null);

        Assert.All(before, node => Assert.Contains(node, AllNodes(window.RootNodes)));
        Assert.Equal(0, resets());
        Assert.Equal(["Local", "prod/", "  alpha", "  beta", "  gamma"], Describe(window.RootNodes));
    }

    [Fact]
    public void AddingSessionToNewFolder_CreatesFolderAmongExistingOnes()
    {
        var window = Window();
        window.AddSession(new Session { Name = "a", FolderPath = "a" }, null);
        window.AddSession(new Session { Name = "c", FolderPath = "c" }, null);
        window.AddSession(new Session { Name = "root" }, null);

        window.AddSession(new Session { Name = "b", FolderPath = "b/inner" }, null);

        Assert.Equal(
            ["Local", "a/", "  a", "b/", "  inner/", "    b", "c/", "  c", "root"],
            Describe(window.RootNodes));
    }

    [Fact]
    public void DeletingSession_RemovesOnlyThatNode()
    {
        var window = Window();
        var doomed = new Session { Name = "b" };
        window.AddSession(new Session { Name = "a" }, null);
        window.AddSession(doomed, null);
        window.AddSession(new Session { Name = "c" }, null);
        var survivors = window.RootNodes.Where(n => n.Session?.Id != doomed.Id).ToList();

        window.DeleteSession(doomed);

        Assert.Equal(survivors, window.RootNodes.ToList());
    }

    [Fact]
    public void MovingSessions_RelocatesThemAndPreservesUnrelatedNodes()
    {
        var window = Window();
        var moved = new Session { Name = "web" };
        window.AddSession(moved, null);
        window.AddSession(new Session { Name = "db", FolderPath = "prod" }, null);
        var prod = window.RootNodes.Single(n => n.FolderPath == "prod" && n.IsFolder);
        var db = prod.Children.Single();

        window.MoveSessionsToFolder([moved.Id], "prod");

        Assert.Same(prod, window.RootNodes.Single(n => n.FolderPath == "prod" && n.IsFolder));
        Assert.Same(db, prod.Children[0]);
        Assert.Equal(["Local", "prod/", "  db", "  web"], Describe(window.RootNodes));
        Assert.Equal("prod", prod.Children[1].FolderPath);
    }

    [Fact]
    public void Rebuild_UndoesNodesTheTreeViewMovedDuringADrag()
    {
        var window = Window();
        window.AddSession(new Session { Name = "a" }, null);
        window.AddSession(new Session { Name = "b" }, null);
        window.CreateFolder("empty");
        var a = window.RootNodes.Single(n => n.Name == "a");
        var b = window.RootNodes.Single(n => n.Name == "b");
        // Model WinUI dropping "a" onto session "b" without the move being accepted.
        window.RootNodes.Remove(a);
        b.Children.Add(a);

        window.RebuildTree();

        Assert.Empty(b.Children);
        Assert.Equal(["Local", "empty/", "a", "b"], Describe(window.RootNodes));
        Assert.Same(a, window.RootNodes.Single(n => n.Name == "a"));
    }

    [Fact]
    public void Filtering_ReusesNodesAndUpdatesHighlight()
    {
        var window = Window();
        window.AddSession(new Session { Name = "web", FolderPath = "prod" }, null);
        window.AddSession(new Session { Name = "db", FolderPath = "prod" }, null);
        var prod = window.RootNodes.Single(n => n.FolderPath == "prod" && n.IsFolder);
        var web = prod.Children.Single(n => n.Name == "web");

        window.SearchText = "we";
        Assert.Equal(["prod/", "  web"], Describe(window.RootNodes));
        Assert.Same(web, prod.Children.Single());
        Assert.Equal("we", web.HighlightQuery);

        window.SearchText = "";
        Assert.Equal(["Local", "prod/", "  db", "  web"], Describe(window.RootNodes));
        Assert.Equal("", web.HighlightQuery);
        Assert.True(window.IsInTree(web));
    }

    [Fact]
    public void RemovedNodes_LeaveTheTree()
    {
        var window = Window();
        var session = new Session { Name = "a" };
        window.AddSession(session, null);
        var node = window.RootNodes.Single(n => n.Name == "a");

        window.DeleteSession(session);

        Assert.False(window.IsInTree(node));
    }

    private static List<string> Describe(IEnumerable<TreeNodeViewModel> nodes, int depth = 0) =>
        nodes.SelectMany(node => (IEnumerable<string>)[
            new string(' ', depth * 2) + node.Name + (node.IsFolder && !node.IsLocalRoot ? "/" : ""),
            .. node.IsLocalRoot ? [] : Describe(node.Children, depth + 1),
        ]).ToList();

    private static List<TreeNodeViewModel> AllNodes(IEnumerable<TreeNodeViewModel> nodes) =>
        nodes.SelectMany(node => AllNodes(node.Children).Prepend(node)).ToList();

    private static Func<int> CountResets(ObservableCollection<TreeNodeViewModel> collection)
    {
        var resets = 0;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                resets++;
        };
        return () => resets;
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }
}
