using Resesh.Core.Models;
using Resesh.Core.Search;

namespace Resesh.Core.Tests;

public sealed class SessionSearchTests
{
    private static readonly List<Session> Fleet =
    [
        new() { Name = "prod-web-01", Host = "10.0.0.1", Username = "deploy", FolderPath = "Datacenter", Notes = "nginx front end" },
        new() { Name = "core-sw-3", Host = "192.168.1.3", Username = "netops", FolderPath = "Branch", Notes = "" },
        new() { Name = "edge-rtr-1", Host = "192.168.1.1", Username = "netops", FolderPath = "Branch", Notes = "WAN uplink" },
        new() { Name = "backup-nas", Host = "nas.internal", Username = "root", FolderPath = "", Notes = "synology" },
    ];

    [Theory]
    [InlineData(null, 4)]
    [InlineData("", 4)]
    [InlineData("   ", 4)]
    [InlineData("PROD", 1)]        // name, case-insensitive
    [InlineData("192.168", 2)]     // host substring
    [InlineData("netops", 2)]      // username
    [InlineData("branch", 2)]      // folder path
    [InlineData("uplink", 1)]      // notes
    [InlineData("zzz", 0)]
    [InlineData("netops edge", 1)] // multiple terms AND together
    public void Filter_MatchesExpectedCount(string? query, int expected)
    {
        Assert.Equal(expected, SessionSearch.Filter(Fleet, query).Count);
    }

    [Fact]
    public void Rank_PrefersNamePrefixThenNameThenHost()
    {
        var sessions = new List<Session>
        {
            new() { Name = "other", Host = "core.example.com" }, // host match
            new() { Name = "my-core-box", Host = "1.1.1.1" },    // name substring
            new() { Name = "core-sw-3", Host = "2.2.2.2" },      // name prefix
        };
        var ranked = SessionSearch.Rank(sessions, "core");
        Assert.Equal(["core-sw-3", "my-core-box", "other"], ranked.Select(s => s.Name).ToArray());
    }
}
