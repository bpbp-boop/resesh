using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SessionTreeBuilderTests
{
    [Fact]
    public void Build_NestsFoldersAndPlacesSessions()
    {
        var sessions = new List<Session>
        {
            new() { Name = "root-box", Host = "h", FolderPath = "" },
            new() { Name = "rack-box", Host = "h", FolderPath = "Datacenter/Rack 4" },
        };
        var root = SessionTreeBuilder.Build(sessions, ["Empty"]);

        Assert.Single(root.Sessions);
        Assert.Equal("root-box", root.Sessions[0].Name);
        Assert.Equal(2, root.Folders.Count); // Datacenter, Empty

        var datacenter = root.Folders.Single(f => f.Name == "Datacenter");
        var rack = Assert.Single(datacenter.Folders);
        Assert.Equal("Datacenter/Rack 4", rack.FullPath);
        Assert.Equal("rack-box", Assert.Single(rack.Sessions).Name);

        var empty = root.Folders.Single(f => f.Name == "Empty");
        Assert.Empty(empty.Folders);
        Assert.Empty(empty.Sessions);
    }

    [Fact]
    public void Build_SortsAlphabetically()
    {
        var sessions = new List<Session>
        {
            new() { Name = "zeta", Host = "h" },
            new() { Name = "alpha", Host = "h" },
        };
        var root = SessionTreeBuilder.Build(sessions, ["b-folder", "A-folder"]);

        Assert.Equal(["A-folder", "b-folder"], root.Folders.Select(f => f.Name).ToArray());
        Assert.Equal(["alpha", "zeta"], root.Sessions.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Build_MergesFolderCasingsCaseInsensitively()
    {
        var sessions = new List<Session>
        {
            new() { Name = "a", Host = "h", FolderPath = "Lab" },
            new() { Name = "b", Host = "h", FolderPath = "lab" },
        };
        var root = SessionTreeBuilder.Build(sessions, []);
        var lab = Assert.Single(root.Folders);
        Assert.Equal(2, lab.Sessions.Count);
    }
}
