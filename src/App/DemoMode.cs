using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App;

/// <summary>An isolated, disposable data set for product screenshots.</summary>
internal static class DemoMode
{
    private static readonly Lazy<string> DataDirectory = new(CreateDataDirectory);

    public static bool IsEnabled { get; } = Environment.GetCommandLineArgs()
        .Skip(1)
        .Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase));

    public static string StorePath(string fileName) => Path.Combine(DataDirectory.Value, fileName);

    public static ICredentialService CreateCredentialService() =>
        IsEnabled ? new MemoryCredentialService() : new WindowsCredentialService();

    public static void Seed(SessionStore store)
    {
        foreach (var folder in new[]
        {
            "Sydney DC/Network",
            "Sydney DC/Hypervisors",
            "Melbourne DC/Network",
            "Melbourne DC/Hypervisors",
            "Edge & Security",
            "Core Services",
            "Staging & Lab",
        })
        {
            store.CreateFolder(folder);
        }

        foreach (var session in Sessions)
            store.Add(session);
    }

    private static readonly Session[] Sessions =
    [
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393901"),
            Name = "PowerShell 7",
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "pwsh.exe" },
            Icon = "windows",
            Notes = "Administrative PowerShell environment",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393902"),
            Name = "Command Prompt",
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "cmd.exe" },
            Icon = "windows",
            Notes = "Local Windows command prompt",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393903"),
            Name = "Ubuntu 24.04",
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "wsl.exe", Arguments = ["-d", "Ubuntu-24.04"] },
            Icon = "ubuntu",
            Notes = "Local WSL Linux environment",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393904"),
            Name = "syd-spine-01",
            FolderPath = "Sydney DC/Network",
            Host = "10.10.1.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            Notes = "Sydney fabric spine switch 01",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393905"),
            Name = "syd-spine-02",
            FolderPath = "Sydney DC/Network",
            Host = "10.10.1.12",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            Notes = "Sydney fabric spine switch 02",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393906"),
            Name = "syd-leaf-01",
            FolderPath = "Sydney DC/Network",
            Host = "10.10.2.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            Notes = "Sydney compute leaf switch 01",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393907"),
            Name = "syd-leaf-02",
            FolderPath = "Sydney DC/Network",
            Host = "10.10.2.12",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            Notes = "Sydney compute leaf switch 02",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393908"),
            Name = "syd-hv-01",
            FolderPath = "Sydney DC/Hypervisors",
            Host = "syd-hv01.corp.internal",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "proxmox",
            Notes = "Sydney Proxmox VE cluster node 01",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393909"),
            Name = "syd-hv-02",
            FolderPath = "Sydney DC/Hypervisors",
            Host = "syd-hv02.corp.internal",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "proxmox",
            Notes = "Sydney Proxmox VE cluster node 02",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393910"),
            Name = "mel-spine-01",
            FolderPath = "Melbourne DC/Network",
            Host = "10.20.1.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "juniper",
            Notes = "Melbourne fabric spine switch 01",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393911"),
            Name = "mel-leaf-01",
            FolderPath = "Melbourne DC/Network",
            Host = "10.20.2.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "juniper",
            Notes = "Melbourne compute leaf switch 01",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393912"),
            Name = "mel-hv-01",
            FolderPath = "Melbourne DC/Hypervisors",
            Host = "mel-hv01.corp.internal",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "proxmox",
            Notes = "Melbourne Proxmox VE cluster node 01",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393913"),
            Name = "edge-gw-01",
            FolderPath = "Edge & Security",
            Host = "203.0.113.1",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "cisco",
            ColorTag = "#E74856",
            Notes = "Primary perimeter gateway router",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393914"),
            Name = "edge-fw-01",
            FolderPath = "Edge & Security",
            Host = "198.51.100.1",
            Username = "admin",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "paloalto",
            ColorTag = "#E74856",
            Notes = "Perimeter firewall cluster active node",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393915"),
            Name = "vpn-gw-01",
            FolderPath = "Edge & Security",
            Host = "198.51.100.10",
            Username = "admin",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "fortinet",
            Notes = "Corporate VPN gateway",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393916"),
            Name = "bgp-peer-01",
            FolderPath = "Edge & Security",
            Host = "198.51.100.254",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "vyos",
            Notes = "Internet exchange BGP peering router",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393917"),
            Name = "auth-radius-01",
            FolderPath = "Core Services",
            Host = "auth01.corp.internal",
            Username = "sysadmin",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "debian",
            Notes = "RADIUS authentication and accounting server",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393918"),
            Name = "dns-primary-01",
            FolderPath = "Core Services",
            Host = "ns1.corp.internal",
            Username = "sysadmin",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "debian",
            Notes = "Authoritative internal DNS service",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393919"),
            Name = "monitoring-01",
            FolderPath = "Core Services",
            Host = "mon01.corp.internal",
            Username = "infra",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "ubuntu",
            Notes = "Prometheus and Alertmanager cluster",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393920"),
            Name = "jumpbox-prod",
            FolderPath = "Core Services",
            Host = "bastion.corp.internal",
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "ubuntu",
            ColorTag = "#10893E",
            Notes = "Production management jump host",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393921"),
            Name = "dev-sandbox",
            FolderPath = "Staging & Lab",
            Host = "sandbox.lab.internal",
            Username = "developer",
            AuthMethod = AuthMethod.Password,
            Icon = "fedora",
            Notes = "Disposable development and integration sandbox",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393922"),
            Name = "lab-router-01",
            FolderPath = "Staging & Lab",
            Host = "172.16.0.1",
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            Icon = "mikrotik",
            Notes = "Lab testbed router",
        },
    ];

    private static string CreateDataDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Resesh", "Demo", Environment.ProcessId.ToString());
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            Directory.CreateDirectory(path);
        }
        catch (IOException)
        {
            path = Path.Combine(Path.GetTempPath(), "Resesh", "Demo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
        }

        var cleanupPath = path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(cleanupPath, recursive: true);
            }
            catch (IOException)
            {
                // The operating system can remove a stale demo directory later.
            }
            catch (UnauthorizedAccessException)
            {
                // The operating system can remove a stale demo directory later.
            }
        };
        return path;
    }

    private sealed class MemoryCredentialService : ICredentialService
    {
        private readonly Dictionary<Guid, string> _secrets = [];

        public string? Read(Guid sessionId) => _secrets.GetValueOrDefault(sessionId);

        public void Write(Guid sessionId, string secret) => _secrets[sessionId] = secret;

        public void Delete(Guid sessionId) => _secrets.Remove(sessionId);
    }
}
