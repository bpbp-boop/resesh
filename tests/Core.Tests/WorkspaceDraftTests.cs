using Resesh.Core.Layout;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class WorkspaceDraftTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("workspace-draft-test");
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static WorkspaceLayout Layout(params Guid[][] groups) => new()
    {
        Groups = groups.Select(tabs => new WorkspaceGroup
        {
            Tabs = tabs.Select(id => new WorkspaceTabReference { SessionId = id }).ToList(),
        }).ToList(),
        Layout = groups.Length < 2 ? null : new WorkspaceLayoutNode
        {
            Orientation = SplitOrientation.Columns,
            Children = groups.Select((_, index) => new WorkspaceLayoutNode { GroupIndex = index }).ToList(),
            Sizes = groups.Select(_ => 300.0).ToList(),
        },
    };

    private WorkspaceStore NewStore(string name)
    {
        var store = new WorkspaceStore(Path.Combine(_dir.FullName, name));
        store.Load();
        return store;
    }

    [Fact]
    public void HasChanges_CountsTabsPinsAndSplitsButNotSizesOrSelection()
    {
        var store = NewStore("changes.json");
        var lab = store.SaveAs("Lab", Layout([A, B]));

        Assert.False(store.HasChanges(lab.Id, Layout([A, B])));
        Assert.False(store.HasChanges(lab.Id, Layout([A, B]) with
        {
            Groups = [new WorkspaceGroup { Tabs = Layout([A, B]).Groups[0].Tabs, ActiveTabIndex = 1 }],
        }));
        Assert.True(store.HasChanges(lab.Id, Layout([B, A])));
        Assert.True(store.HasChanges(lab.Id, Layout([A])));
        Assert.True(store.HasChanges(lab.Id, Layout([A], [B])));
        Assert.True(store.HasChanges(lab.Id, new WorkspaceLayout
        {
            Groups = [new WorkspaceGroup { Tabs = [new() { SessionId = A, Pinned = true }, new() { SessionId = B }] }],
        }));
        // Nothing saved is open: nothing to save, so no change.
        Assert.False(store.HasChanges(lab.Id, new WorkspaceLayout { Groups = [new WorkspaceGroup()] }));
        Assert.False(store.HasChanges(Guid.NewGuid(), Layout([A])));
    }

    [Fact]
    public void HasChanges_IgnoresPaneSizes()
    {
        var store = NewStore("sizes.json");
        var lab = store.SaveAs("Lab", Layout([A], [B]));
        var resized = Layout([A], [B]);
        resized.Layout!.Sizes = [120, 480];

        Assert.False(store.HasChanges(lab.Id, resized));
    }

    [Fact]
    public void Drafts_AreKeptAcrossLoadsAndNeverTouchTheSavedLayout()
    {
        var path = "drafts.json";
        var store = NewStore(path);
        var lab = store.SaveAs("Lab", Layout([A]));

        store.SetDraft(lab.Id, Layout([A, B]));
        Assert.True(store.HasDraft(lab.Id));
        Assert.Single(Assert.Single(store.Workspaces).Groups[0].Tabs);

        var loaded = NewStore(path);
        Assert.Equal(2, loaded.DraftOf(lab.Id)!.Groups[0].Tabs.Count);
        Assert.Single(loaded.Workspaces[0].Groups[0].Tabs);

        // A layout back to the saved one drops the draft.
        loaded.SetDraft(lab.Id, Layout([A]));
        Assert.False(loaded.HasDraft(lab.Id));
        Assert.False(NewStore(path).HasDraft(lab.Id));
    }

    [Fact]
    public void Drafts_GoWhenSavedDiscardedOrDeleted()
    {
        var store = NewStore("lifecycle.json");
        var lab = store.SaveAs("Lab", Layout([A]));
        var prod = store.SaveAs("Prod", Layout([A]));

        store.SetDraft(lab.Id, Layout([A, B]));
        store.Update(lab.Id, Layout([A, B]));
        Assert.False(store.HasDraft(lab.Id));
        Assert.Equal(2, store.Workspaces[0].Groups[0].Tabs.Count);

        store.SetDraft(lab.Id, Layout([B]));
        store.SetDraft(lab.Id, null);
        Assert.False(store.HasDraft(lab.Id));

        store.SetDraft(prod.Id, Layout([B]));
        store.Delete(prod.Id);
        Assert.False(store.HasDraft(prod.Id));
        store.SetDraft(prod.Id, Layout([B])); // gone: ignored
        Assert.False(store.HasDraft(prod.Id));
    }

    [Fact]
    public void Coordinator_KeepsDraftsWithoutSaving()
    {
        var store = NewStore("coordinator.json");
        var lab = store.SaveAs("Lab", Layout([A]));
        var coordinator = new WorkspaceCoordinator(store, () => Layout([A]), () => { }, (_, _) => { });

        Assert.True(coordinator.KeepDraft(lab.Id, Layout([A, B])));
        Assert.Single(store.Workspaces[0].Groups[0].Tabs);
        Assert.True(store.HasDraft(lab.Id));

        Assert.True(coordinator.DiscardDraft(lab.Id));
        Assert.False(store.HasDraft(lab.Id));
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
