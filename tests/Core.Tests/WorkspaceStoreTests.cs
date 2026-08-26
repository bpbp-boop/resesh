using Resesh.Core.Layout;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("workspace-store-test");

    [Fact]
    public void SaveLoadUpdateRenameDelete_PreservesOrderedGroupsDuplicateTabsAndLastLayout()
    {
        var path = Path.Combine(_dir.FullName, "workspaces.json");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var store = new WorkspaceStore(path);
        store.Load();
        var layout = new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup
                {
                    Tabs =
                    [
                        new WorkspaceTabReference { SessionId = first, Pinned = true },
                        new WorkspaceTabReference { SessionId = first },
                    ],
                    ActiveTabIndex = 1,
                },
                new WorkspaceGroup
                {
                    Tabs = [new WorkspaceTabReference { SessionId = second }],
                    ActiveTabIndex = 0,
                },
            ],
        };

        var saved = store.SaveAs("Morning", layout);
        store.SaveLastLayout(layout);
        store.Update(saved.Id, layout);
        store.Rename(saved.Id, "Operations");

        var loaded = new WorkspaceStore(path);
        loaded.Load();
        var workspace = Assert.Single(loaded.Workspaces);
        Assert.Equal(saved.Id, workspace.Id);
        Assert.Equal("Operations", workspace.Name);
        Assert.Equal([first, first], workspace.Groups[0].Tabs.Select(tab => tab.SessionId));
        Assert.True(workspace.Groups[0].Tabs[0].Pinned);
        Assert.Equal(1, workspace.Groups[0].ActiveTabIndex);
        Assert.Equal(second, workspace.Groups[1].Tabs[0].SessionId);
        Assert.Equal(2, loaded.LastLayout!.Groups.Count);

        Assert.True(loaded.Delete(saved.Id));
        Assert.Empty(loaded.Workspaces);
    }

    [Fact]
    public void SaveLoad_PreservesNestedHorizontalSplitLayout()
    {
        var path = Path.Combine(_dir.FullName, "horizontal-workspace.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var layout = new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup(),
                new WorkspaceGroup(),
                new WorkspaceGroup(),
            ],
            Layout = new WorkspaceLayoutNode
            {
                Orientation = SplitOrientation.Columns,
                Children =
                [
                    new WorkspaceLayoutNode { GroupIndex = 0 },
                    new WorkspaceLayoutNode
                    {
                        Orientation = SplitOrientation.Rows,
                        Children =
                        [
                            new WorkspaceLayoutNode { GroupIndex = 1 },
                            new WorkspaceLayoutNode { GroupIndex = 2 },
                        ],
                    },
                ],
            },
        };

        store.SaveAs("Horizontal split", layout);
        store.SaveLastLayout(layout);

        var loaded = new WorkspaceStore(path);
        loaded.Load();

        var workspaceLayout = Assert.Single(loaded.Workspaces).Layout!;
        Assert.Equal(SplitOrientation.Columns, workspaceLayout.Orientation);
        var horizontalSplit = workspaceLayout.Children[1];
        Assert.Equal(SplitOrientation.Rows, horizontalSplit.Orientation);
        Assert.Equal([1, 2], horizontalSplit.Children.Select(child => child.GroupIndex));
        Assert.Equal(SplitOrientation.Rows, loaded.LastLayout!.Layout!.Children[1].Orientation);
    }

    [Fact]
    public void Load_CorruptPrimaryFallsBackToBakFromPreviousAtomicWrite()
    {
        var path = Path.Combine(_dir.FullName, "workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Before rename", EmptyLayout());
        store.Rename(saved.Id, "After rename");
        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllText(path, "not json");

        var recovered = new WorkspaceStore(path);
        recovered.Load();

        Assert.Equal("Before rename", Assert.Single(recovered.Workspaces).Name);
    }

    [Fact]
    public void Load_StructurallyCorruptPrimaryFallsBackToValidBak()
    {
        var path = Path.Combine(_dir.FullName, "structural-workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Backup layout", EmptyLayout());
        store.Rename(saved.Id, "Primary layout");

        var corruptPayloads = new[]
        {
            "{\"workspaces\":[null]}",
            "{\"workspaces\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Bad\",\"groups\":[null]}]}",
            "{\"workspaces\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Bad\",\"groups\":[{\"tabs\":[null],\"activeTabIndex\":0}]}]}",
        };
        foreach (var payload in corruptPayloads)
        {
            File.WriteAllText(path, payload);
            var recovered = new WorkspaceStore(path);

            recovered.Load();

            Assert.Equal("Backup layout", Assert.Single(recovered.Workspaces).Name);
        }
    }

    [Fact]
    public void SaveAs_RejectsInvalidActiveIndexWithoutChangingFile()
    {
        var path = Path.Combine(_dir.FullName, "workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        store.SaveAs("Valid", EmptyLayout());
        var before = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => store.SaveAs("Invalid", new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup
                {
                    Tabs = [new WorkspaceTabReference { SessionId = Guid.NewGuid() }],
                    ActiveTabIndex = 1,
                },
            ],
        }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Reorder_PersistsExactOrderAndRejectsIncompleteOrder()
    {
        var path = Path.Combine(_dir.FullName, "reordered-workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var first = store.SaveAs("First", EmptyLayout());
        var second = store.SaveAs("Second", EmptyLayout());
        var third = store.SaveAs("Third", EmptyLayout());

        store.Reorder([third.Id, first.Id, second.Id]);

        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal(
            ["Third", "First", "Second"],
            loaded.Workspaces.Select(workspace => workspace.Name));

        var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => store.Reorder([first.Id, second.Id]));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static WorkspaceLayout EmptyLayout() => new()
    {
        Groups = [new WorkspaceGroup()],
    };
}
