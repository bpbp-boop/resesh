using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class WorkspaceAppearanceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("workspace-appearance-test");

    [Theory]
    [InlineData("Prod incident", "PI")]
    [InlineData("lab", "La")]
    [InlineData("x", "X")]
    [InlineData("core-network edge", "CN")]
    [InlineData("  web01  ", "We")]
    [InlineData("(staging)", "St")]
    [InlineData("--", "?")]
    public void Monogram_TakesInitialsOfTwoWordsOrTwoLettersOfOne(string name, string expected) =>
        Assert.Equal(expected, WorkspaceAppearance.Monogram(name));

    [Fact]
    public void Color_UsesTheChosenColorOrOneStableForTheId()
    {
        var id = Guid.NewGuid();
        var automatic = WorkspaceAppearance.Color(new Workspace { Id = id, Name = "A" });

        Assert.Contains(automatic, WorkspaceAppearance.Palette.Select(entry => entry.Hex));
        Assert.Equal(automatic, WorkspaceAppearance.Color(new Workspace { Id = id, Name = "Renamed" }));
        Assert.Equal("#C239B3", WorkspaceAppearance.Color(new Workspace { Id = id, ColorTag = "#C239B3" }));
        Assert.Equal(automatic, WorkspaceAppearance.Color(new Workspace { Id = id, ColorTag = "purple" }));
    }

    [Fact]
    public void SetAppearance_PersistsIconAndColorAndClearsBlankValues()
    {
        var path = Path.Combine(_dir.FullName, "workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Lab", new WorkspaceLayout());

        store.SetAppearance(saved.Id, "debian", "#10893E");
        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal("debian", Assert.Single(loaded.Workspaces).Icon);
        Assert.Equal("#10893E", loaded.Workspaces[0].ColorTag);

        loaded.SetAppearance(saved.Id, " ", null);
        Assert.Null(loaded.Workspaces[0].Icon);
        Assert.Null(loaded.Workspaces[0].ColorTag);
    }

    [Fact]
    public void LastActiveWorkspace_RoundTripsAndIsDroppedWithItsWorkspace()
    {
        var path = Path.Combine(_dir.FullName, "last-active.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var lab = store.SaveAs("Lab", new WorkspaceLayout());
        store.SaveLastLayout(new WorkspaceLayout(), lab.Id);

        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal(lab.Id, loaded.LastActiveWorkspaceId);

        loaded.Delete(lab.Id);
        Assert.Null(loaded.LastActiveWorkspaceId);
        loaded.SaveLastLayout(new WorkspaceLayout(), Guid.NewGuid());
        Assert.Null(loaded.LastActiveWorkspaceId);
    }

    [Fact]
    public void AutoSave_KeepsTheSavedLayoutWhenNothingSavableIsOpen()
    {
        var path = Path.Combine(_dir.FullName, "autosave.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var session = Guid.NewGuid();
        var layout = new WorkspaceLayout
        {
            Groups = [new WorkspaceGroup { Tabs = [new WorkspaceTabReference { SessionId = session }] }],
        };
        var lab = store.SaveAs("Lab", layout);
        var notices = new List<string>();
        var coordinator = new WorkspaceCoordinator(store, () => layout, () => { }, (title, _) => notices.Add(title));

        Assert.False(coordinator.AutoSave(lab.Id, new WorkspaceLayout { Groups = [new WorkspaceGroup()] }));
        Assert.False(coordinator.AutoSave(Guid.NewGuid(), layout));
        Assert.Equal(session, Assert.Single(Assert.Single(store.Workspaces).Groups).Tabs[0].SessionId);

        var second = Guid.NewGuid();
        Assert.True(coordinator.AutoSave(lab.Id, new WorkspaceLayout
        {
            Groups = [new WorkspaceGroup { Tabs = [new WorkspaceTabReference { SessionId = second }] }],
        }));
        Assert.Equal(second, store.Workspaces[0].Groups[0].Tabs[0].SessionId);
        Assert.Empty(notices);
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
