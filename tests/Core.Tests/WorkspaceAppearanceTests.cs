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
    public void Icons_HaveDistinctKeysAndGlyphsAndLeaveHomeAlone()
    {
        var icons = WorkspaceAppearance.Icons;

        Assert.Equal(icons.Count, icons.Select(icon => icon.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(icons.Count, icons.Select(icon => icon.Glyph + icon.Path).Distinct().Count());
        // Exactly one way to draw each: a single glyph, or path data.
        Assert.All(icons, icon => Assert.True(icon.Glyph.Length == 1 ^ icon.Path.Length > 0, icon.Key));
        Assert.DoesNotContain(icons, icon => icon.Glyph == WorkspaceAppearance.HomeGlyph);
    }

    [Theory]
    [InlineData("server", "server")]
    [InlineData("Server", "server")]
    [InlineData("debian", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Find_ReturnsCatalogIconsAndNothingForUnknownKeys(string? key, string? expected) =>
        Assert.Equal(expected, WorkspaceAppearance.Find(key)?.Key);

    [Theory]
    [InlineData("", "server", true)]
    [InlineData("SQL", "database", true)]
    [InlineData("data", "database", true)]
    [InlineData("k8s", "cluster", true)]
    [InlineData("prod live", "lightning", true)]
    [InlineData("prod web", "lightning", false)]
    [InlineData("data", "server", false)]
    public void Matches_FindsNameKeyOrKeywordsForEveryWord(string filter, string key, bool expected) =>
        Assert.Equal(expected, WorkspaceAppearance.Find(key)!.Matches(filter));

    [Fact]
    public void SetAppearance_PersistsIconAndInitialsAndClearsBlankOnes()
    {
        var path = Path.Combine(_dir.FullName, "workspaces.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Lab", new WorkspaceLayout());

        store.SetAppearance(saved.Id, "server", " p r d 1 ", labelIcon: true);
        var loaded = new WorkspaceStore(path);
        loaded.Load();
        Assert.Equal("server", Assert.Single(loaded.Workspaces).Icon);
        Assert.Equal("prd", loaded.Workspaces[0].Initials);
        Assert.True(loaded.Workspaces[0].LabelIcon);

        loaded.SetAppearance(saved.Id, " ", " ");
        Assert.Null(loaded.Workspaces[0].Icon);
        Assert.Null(loaded.Workspaces[0].Initials);
        Assert.False(loaded.Workspaces[0].LabelIcon);
    }

    [Theory]
    [InlineData(null, "Prod incident", "PI")]
    [InlineData("", "Prod incident", "PI")]
    [InlineData("DB", "Prod incident", "DB")]
    [InlineData("prod", "x", "pro")]
    [InlineData("é1", "x", "é1")]
    [InlineData("🔥🔥🔥🔥", "x", "🔥🔥🔥")]
    public void Initials_PreferTheWorkspacesOwnCutToThreeCharacters(string? initials, string name, string expected) =>
        Assert.Equal(expected, WorkspaceAppearance.Initials(new Workspace { Name = name, Initials = initials }));

    [Fact]
    public void Load_IgnoresTheColourEarlierBuildsSaved()
    {
        var path = Path.Combine(_dir.FullName, "with-color.json");
        var store = new WorkspaceStore(path);
        store.Load();
        var saved = store.SaveAs("Lab", new WorkspaceLayout());
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"name\"", "\"colorTag\": \"#10893E\", \"name\""));

        var loaded = new WorkspaceStore(path);
        loaded.Load();

        Assert.Null(loaded.LoadWarning);
        Assert.Equal(saved.Id, Assert.Single(loaded.Workspaces).Id);
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
    public void Save_KeepsTheSavedLayoutWhenNothingSavableIsOpen()
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

        Assert.False(coordinator.Save(lab.Id, new WorkspaceLayout { Groups = [new WorkspaceGroup()] }));
        Assert.False(coordinator.Save(Guid.NewGuid(), layout));
        Assert.Equal(session, Assert.Single(Assert.Single(store.Workspaces).Groups).Tabs[0].SessionId);

        var second = Guid.NewGuid();
        Assert.True(coordinator.Save(lab.Id, new WorkspaceLayout
        {
            Groups = [new WorkspaceGroup { Tabs = [new WorkspaceTabReference { SessionId = second }] }],
        }));
        Assert.Equal(second, store.Workspaces[0].Groups[0].Tabs[0].SessionId);
        Assert.Empty(notices);
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
