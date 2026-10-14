using Resesh.Core.Credentials;
using Resesh.Core.Import;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

/// <summary>Jump hosts, proxies and tunnels as saved, imported and bulk-edited data.</summary>
public sealed class ConnectivityStorageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("connectivity-storage-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SessionStore NewStore()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessions.json"));
        store.Load();
        return store;
    }

    [Fact]
    public void RouteAndTunnelsSurviveARoundTrip()
    {
        var bastion = new Session { Name = "bastion", Host = "bastion.example.com", Username = "ops" };
        var session = new Session
        {
            Name = "db",
            Host = "10.0.0.5",
            Username = "ops",
            JumpHostId = bastion.Id,
            Proxy = new ProxySettings { Kind = ProxyKind.Http, Host = "proxy.corp", Port = 3128, Username = "me" },
            Tunnels =
            [
                new Tunnel { Kind = TunnelKind.Local, BindPort = 5432, DestinationHost = "localhost", DestinationPort = 5432 },
                new Tunnel { Kind = TunnelKind.Dynamic, BindAddress = "0.0.0.0", BindPort = 1080 },
            ],
        };
        var store = NewStore();
        store.AddMany([bastion, session]);

        var loaded = NewStore().Find(session.Id)!;

        Assert.Equal(bastion.Id, loaded.JumpHostId);
        Assert.Equal(session.Proxy, loaded.Proxy);
        Assert.Equal(session.Tunnels, loaded.Tunnels);
    }

    [Fact]
    public void SessionWithoutARouteWritesNoRouteFields()
    {
        var store = NewStore();
        store.Add(new Session { Name = "web", Host = "web", Username = "ops" });

        var json = File.ReadAllText(Path.Combine(_dir, "sessions.json"));

        Assert.DoesNotContain("jumpHostId", json);
        Assert.DoesNotContain("proxy", json);
        Assert.DoesNotContain("tunnels", json);
    }

    [Theory]
    [InlineData(TunnelKind.Local, 0, "db", 5432, "Enter a listening port between 1 and 65535.")]
    [InlineData(TunnelKind.Local, 8080, "", 5432, "Enter the destination host.")]
    [InlineData(TunnelKind.Remote, 8080, "localhost", 0, "Enter a destination port between 1 and 65535.")]
    [InlineData(TunnelKind.Dynamic, 1080, "", 0, null)]
    [InlineData(TunnelKind.Local, 8080, "db", 5432, null)]
    public void TunnelValidation(TunnelKind kind, int bindPort, string host, int port, string? expected)
    {
        var tunnel = new Tunnel { Kind = kind, BindPort = bindPort, DestinationHost = host, DestinationPort = port };

        Assert.Equal(expected, tunnel.Validate());
    }

    [Fact]
    public void TunnelDescriptionsReadInTheDirectionTrafficFlows()
    {
        Assert.Equal("Local 127.0.0.1:8080 → db:5432",
            new Tunnel { BindPort = 8080, DestinationHost = "db", DestinationPort = 5432 }.Describe());
        Assert.Equal("Remote 127.0.0.1:9000 → localhost:3000",
            new Tunnel { Kind = TunnelKind.Remote, BindPort = 9000, DestinationHost = "localhost", DestinationPort = 3000 }.Describe());
        Assert.Equal("SOCKS 127.0.0.1:1080", new Tunnel { Kind = TunnelKind.Dynamic, BindPort = 1080 }.Describe());
    }

    [Fact]
    public void BulkEditSetsAndClearsTheJumpHostButNeverOnItselfOrLocalProfiles()
    {
        var bastion = new Session { Name = "bastion", Host = "bastion", Username = "ops" };
        var ssh = new Session { Name = "db", Host = "db", Username = "ops" };
        var telnet = new Session { Name = "console", Host = "console", Kind = SessionKind.Telnet };
        var local = new Session { Name = "pwsh", Kind = SessionKind.Local, Local = new LocalTarget { Executable = "pwsh" } };
        var set = new SessionBulkEdit { SetJumpHost = true, JumpHostId = bastion.Id };

        Assert.False(set.IsEmpty);
        Assert.Equal(bastion.Id, set.Apply(ssh).JumpHostId);
        Assert.Equal(bastion.Id, set.Apply(telnet).JumpHostId);
        Assert.Same(local, set.Apply(local));
        Assert.Same(bastion, set.Apply(bastion));

        var clear = new SessionBulkEdit { SetJumpHost = true };
        Assert.Null(clear.Apply(ssh with { JumpHostId = bastion.Id }).JumpHostId);
        Assert.Same(ssh, clear.Apply(ssh));
    }

    [Fact]
    public void ProxyPasswordIsKeptApartFromTheSessionPassword()
    {
        ICredentialService credentials = new MemoryCredentials();
        var id = Guid.NewGuid();
        credentials.Write(id, "session-secret");

        credentials.WriteProxy(id, "proxy-secret");

        Assert.Equal("session-secret", credentials.Read(id));
        Assert.Equal("proxy-secret", credentials.ReadProxy(id));

        credentials.DeleteProxy(id);

        Assert.Null(credentials.ReadProxy(id));
        Assert.Equal("session-secret", credentials.Read(id));
    }

    [Theory]
    [InlineData("bastion", "bastion")]
    [InlineData("ops@bastion:2222", "bastion")]
    [InlineData("edge, ops@core:22", "core")]
    [InlineData("[2001:db8::1]:22", "2001:db8::1")]
    [InlineData("none", null)]
    [InlineData("ssh://ops@bastion", null)]
    [InlineData("", null)]
    public void ProxyJumpNamesTheHostThatReachesTheTarget(string value, string? expected)
    {
        Assert.Equal(expected, OpenSshConfigImporter.JumpAlias(value));
    }

    [Fact]
    public void OpenSshProxyJumpLinksImportedSessions()
    {
        var scan = OpenSshConfigImporter.Parse("""
            Host bastion
              HostName bastion.example.com
              User ops
            Host db
              HostName 10.0.0.5
              ProxyJump bastion
            Host web
              HostName 10.0.0.6
              ProxyJump unknown-host
            """);
        var store = NewStore();

        Assert.Equal((3, 0), SecureCrtImporter.Commit(store, scan.Importable, new SshKeyStore(Path.Combine(_dir, "keys.json"))));

        var bastion = store.Sessions.Single(session => session.Name == "bastion");
        Assert.Equal(bastion.Id, store.Sessions.Single(session => session.Name == "db").JumpHostId);
        Assert.Null(store.Sessions.Single(session => session.Name == "web").JumpHostId);
        Assert.Null(bastion.JumpHostId);
    }

    [Fact]
    public void ImportLinksAJumpHostThatWasSavedEarlier()
    {
        var store = NewStore();
        var bastion = new Session { Name = "bastion", Host = "bastion.example.com", Username = "ops" };
        store.Add(bastion);

        SecureCrtImporter.Commit(store,
            [new ImportCandidate { Name = "db", Host = "10.0.0.5", Username = "ops", Protocol = "SSH2", JumpHostName = "bastion.example.com" }],
            new SshKeyStore(Path.Combine(_dir, "keys.json")));

        Assert.Equal(bastion.Id, store.Sessions.Single(session => session.Name == "db").JumpHostId);
    }

    [Fact]
    public void ImportNeverLinksTwoHostsAsEachOthersJump()
    {
        var store = NewStore();

        SecureCrtImporter.Commit(store,
            [
                new ImportCandidate { Name = "a", Host = "a", Username = "ops", Protocol = "SSH2", JumpHostName = "b" },
                new ImportCandidate { Name = "b", Host = "b", Username = "ops", Protocol = "SSH2", JumpHostName = "a" },
            ],
            new SshKeyStore(Path.Combine(_dir, "keys.json")));

        Assert.Single(store.Sessions, session => session.JumpHostId is not null);
    }

    [Fact]
    public void PuttyProxySettingsBecomeAProxyOrAJumpHost()
    {
        var socks = PuttyRegistryImporter.ParseSession("via-socks", new Dictionary<string, object?>
        {
            ["HostName"] = "10.0.0.5",
            ["Protocol"] = "ssh",
            ["ProxyMethod"] = 2,
            ["ProxyHost"] = "proxy.corp",
            ["ProxyPort"] = 1081,
            ["ProxyUsername"] = "me",
        });
        var http = PuttyRegistryImporter.ParseSession("via-http", new Dictionary<string, object?>
        {
            ["HostName"] = "10.0.0.5",
            ["Protocol"] = "ssh",
            ["ProxyMethod"] = 3,
            ["ProxyHost"] = "proxy.corp",
        });
        var jump = PuttyRegistryImporter.ParseSession("via-ssh", new Dictionary<string, object?>
        {
            ["HostName"] = "10.0.0.5",
            ["Protocol"] = "ssh",
            ["ProxyMethod"] = 6,
            ["ProxyHost"] = "bastion",
        });
        var direct = PuttyRegistryImporter.ParseSession("direct", new Dictionary<string, object?>
        {
            ["HostName"] = "10.0.0.5",
            ["Protocol"] = "ssh",
            ["ProxyMethod"] = 0,
            ["ProxyHost"] = "proxy",
        });

        Assert.Equal(new ProxySettings { Kind = ProxyKind.Socks5, Host = "proxy.corp", Port = 1081, Username = "me" }, socks.Proxy);
        Assert.Equal(new ProxySettings { Kind = ProxyKind.Http, Host = "proxy.corp", Port = 8080 }, http.Proxy);
        Assert.Null(jump.Proxy);
        Assert.Equal("bastion", jump.JumpHostName);
        Assert.Null(direct.Proxy);
        Assert.Null(direct.JumpHostName);
    }

    private sealed class MemoryCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _secrets = [];

        public string? Read(Guid sessionId) => _secrets.GetValueOrDefault(sessionId);
        public void Write(Guid sessionId, string secret) => _secrets[sessionId] = secret;
        public void Delete(Guid sessionId) => _secrets.Remove(sessionId);
    }
}
