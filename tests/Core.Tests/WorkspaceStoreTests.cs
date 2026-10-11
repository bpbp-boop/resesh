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
    public void SaveLoad_PreservesArrangedPaneSizes()
    {
        var path = Path.Combine(_dir.FullName, "sized-workspace.json");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var layout = new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup { Tabs = [new WorkspaceTabReference { SessionId = first }] },
                new WorkspaceGroup { Tabs = [new WorkspaceTabReference { SessionId = second }] },
            ],
            Layout = new WorkspaceLayoutNode
            {
                Orientation = SplitOrientation.Columns,
                Sizes = [417.25, 803.5],
                Children =
                [
                    new WorkspaceLayoutNode { GroupIndex = 0 },
                    new WorkspaceLayoutNode { GroupIndex = 1 },
                ],
            },
        };

        var store = new WorkspaceStore(path);
        store.Load();
        store.SaveAs("Sized", layout);

        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal([417.25, 803.5], Assert.Single(loaded.Workspaces).Layout!.Sizes);
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

    [Theory]
    [InlineData("SaveAs")]
    [InlineData("Rename")]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("Reorder")]
    [InlineData("SaveLastLayout")]
    public void Mutation_FailedWriteDoesNotPublishOrLeakIntoNextSave(string mutation)
    {
        var path = Path.Combine(_dir.FullName, "failed-write-workspaces.json");
        var sessionId = Guid.NewGuid();
        var layout = new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup
                {
                    Tabs = [new WorkspaceTabReference { SessionId = sessionId, Pinned = true }],
                },
            ],
        };
        var store = new WorkspaceStore(path);
        store.Load();
        var first = store.SaveAs("First", layout);
        var second = store.SaveAs("Second", EmptyLayout());
        store.SaveLastLayout(layout);
        var before = store.Workspaces;
        var lastLayout = store.LastLayout;
        var primary = File.ReadAllBytes(path);
        var backup = File.ReadAllBytes(path + ".bak");
        Directory.CreateDirectory(path + ".tmp");

        var exception = Record.Exception(() =>
        {
            switch (mutation)
            {
                case "SaveAs":
                    store.SaveAs("Rejected", EmptyLayout());
                    break;
                case "Rename":
                    store.Rename(first.Id, "Rejected");
                    break;
                case "Update":
                    store.Update(first.Id, EmptyLayout());
                    break;
                case "Delete":
                    store.Delete(first.Id);
                    break;
                case "Reorder":
                    store.Reorder([second.Id, first.Id]);
                    break;
                case "SaveLastLayout":
                    store.SaveLastLayout(EmptyLayout());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal(before.Count, store.Workspaces.Count);
        for (var index = 0; index < before.Count; index++)
            Assert.Same(before[index], store.Workspaces[index]);
        Assert.Same(lastLayout, store.LastLayout);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));

        Directory.Delete(path + ".tmp");
        store.Rename(second.Id, "Committed");

        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal([first.Id, second.Id], loaded.Workspaces.Select(workspace => workspace.Id));
        Assert.Equal(["First", "Committed"], loaded.Workspaces.Select(workspace => workspace.Name));
        var savedTab = Assert.Single(Assert.Single(loaded.Workspaces[0].Groups).Tabs);
        Assert.Equal(sessionId, savedTab.SessionId);
        Assert.True(savedTab.Pinned);
        Assert.Empty(Assert.Single(loaded.Workspaces[1].Groups).Tabs);
        var lastTab = Assert.Single(Assert.Single(loaded.LastLayout!.Groups).Tabs);
        Assert.Equal(sessionId, lastTab.SessionId);
        Assert.True(lastTab.Pinned);
    }

    [Fact]
    public void Mutation_FailedRecoveryWritePreservesBackupUntilSuccessfulSave()
    {
        var path = Path.Combine(_dir.FullName, "recovered-workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Backup", EmptyLayout());
        store.Rename(saved.Id, "Primary");
        var backup = File.ReadAllBytes(path + ".bak");
        File.WriteAllText(path, "not json");
        var recovered = new WorkspaceStore(path);
        recovered.Load();
        var before = Assert.Single(recovered.Workspaces);
        var warning = recovered.LoadWarning;
        Assert.NotNull(warning);
        Directory.CreateDirectory(path + ".tmp");

        var exception = Record.Exception(() => recovered.Rename(saved.Id, "Rejected"));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Same(before, Assert.Single(recovered.Workspaces));
        Assert.Equal(warning, recovered.LoadWarning);
        Assert.Equal("not json", File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));

        Directory.Delete(path + ".tmp");
        recovered.Rename(saved.Id, "Recovered");
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal("Recovered", Assert.Single(loaded.Workspaces).Name);

        var primary = File.ReadAllBytes(path);
        recovered.Rename(saved.Id, "Next");
        Assert.Equal(primary, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void WriteImported_FailedWriteLeavesStoreAndFilesUnchanged()
    {
        var path = Path.Combine(_dir.FullName, "imported-workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Original", EmptyLayout());
        store.SaveLastLayout(EmptyLayout());
        var lastLayout = store.LastLayout;
        var primary = File.ReadAllBytes(path);
        var backup = File.ReadAllBytes(path + ".bak");
        var imported = new WorkspaceStoreData
        {
            Workspaces = [saved with { Name = "Rejected" }],
            LastLayout = new WorkspaceLayout(),
        };
        Directory.CreateDirectory(path + ".tmp");

        var exception = Record.Exception(() => WorkspaceStore.WriteImported(path, imported));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Same(saved, Assert.Single(store.Workspaces));
        Assert.Same(lastLayout, store.LastLayout);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        Assert.Equal("Rejected", Assert.Single(imported.Workspaces!).Name);
        Assert.Empty(imported.LastLayout!.Groups);

        Directory.Delete(path + ".tmp");
        store.Rename(saved.Id, "Committed");
        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal("Committed", Assert.Single(loaded.Workspaces).Name);
        Assert.Single(loaded.LastLayout!.Groups);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static WorkspaceLayout EmptyLayout() => new()
    {
        Groups = [new WorkspaceGroup()],
    };
}
