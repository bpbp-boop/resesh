using Sessions.Core.Models;
using Sessions.Core.Storage;

namespace Sessions.Core.Tests;

public class SessionIconsTests
{
    [Fact]
    public void BuiltInKeysAreUniqueAndWellFormed()
    {
        var keys = SessionIcons.BuiltIn.Select(i => i.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SessionIcons.BuiltIn, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Key));
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.DoesNotContain('.', i.Key); // custom keys are filenames; built-ins must not collide
            Assert.Equal(i.Key, i.Key.ToLowerInvariant());
        });
        Assert.DoesNotContain(SessionIcons.None, keys);
    }

    [Fact]
    public void EveryBuiltInIconHasABundledAsset()
    {
        var dir = FindSessionIconAssetDirectory();
        foreach (var info in SessionIcons.BuiltIn)
        {
            Assert.True(
                File.Exists(Path.Combine(dir, info.Key + ".svg")) || File.Exists(Path.Combine(dir, info.Key + ".png")),
                $"no bundled asset for built-in icon '{info.Key}'");
        }
    }

    private static string FindSessionIconAssetDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "App", "Assets", "SessionIcons");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"icon asset directory not found above {AppContext.BaseDirectory}");
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.10", "ubuntu")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Proxmox-VE Debian-2+deb12u3", "proxmox")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", "debian")]
    [InlineData("SSH-2.0-OpenSSH_7.9p1 Raspbian-10+deb10u2", "debian")]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_8.1", "windows")]
    [InlineData("SSH-2.0-Cisco-1.25", "cisco")]
    [InlineData("SSH-2.0-ROSSSH", "mikrotik")]
    [InlineData("SSH-2.0-OpenSSH_7.5 FreeBSD-20170903", "freebsd")]
    [InlineData("SSH-2.0-OpenSSH_9.7 FEDORA-40", "fedora")]
    public void SuggestFromBannerMapsKnownBanners(string banner, string expected) =>
        Assert.Equal(expected, SessionIcons.SuggestFromBanner(banner));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SSH-2.0-OpenSSH_9.6")] // a bare OpenSSH banner identifies nothing
    [InlineData("SSH-2.0-dropbear_2022.83")]
    public void SuggestFromBannerReturnsNullWhenUnidentified(string? banner) =>
        Assert.Null(SessionIcons.SuggestFromBanner(banner));

    [Fact]
    public void EverySuggestionResolvesToABuiltInKey()
    {
        // Exercise the mapping through representative banners; every returned key must exist.
        string?[] banners =
        [
            "ubuntu", "proxmox", "debian", "raspbian", "fedora", "centos", "red hat", "redhat", "rhel", "suse",
            "alpine", "freebsd", "openbsd", "cisco", "rosssh", "mikrotik", "forti", "juniper", "junos",
            "arista", "palo alto", "pan-os", "aruba", "vyos", "openssh_for_windows",
        ];
        foreach (var banner in banners)
        {
            var key = SessionIcons.SuggestFromBanner(banner);
            Assert.NotNull(key);
            Assert.True(SessionIcons.IsBuiltIn(key!), $"banner '{banner}' suggested unknown key '{key}'");
        }
    }

    [Fact]
    public void IconRoundTripsThroughSessionStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-icons-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SessionStore(path);
            store.Load();
            var withIcon = new Session { Name = "r1", Host = "10.0.0.1", Icon = "cisco" };
            var withoutIcon = new Session { Name = "r2", Host = "10.0.0.2" };
            store.Add(withIcon);
            store.Add(withoutIcon);

            var reloaded = new SessionStore(path);
            reloaded.Load();
            Assert.Equal("cisco", reloaded.Find(withIcon.Id)!.Icon);
            Assert.Null(reloaded.Find(withoutIcon.Id)!.Icon);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
