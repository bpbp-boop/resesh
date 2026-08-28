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
            "DC1 Sydney/Spine",
            "DC1 Sydney/Leaf",
            "DC1 Sydney/Hypervisors",
            "DC1 Sydney/Services",
            "DC2 Melbourne/Spine",
            "DC2 Melbourne/Leaf",
            "DC2 Melbourne/Hypervisors",
            "DC2 Melbourne/Services",
            "Network/BNG",
            "Network/Peering",
            "Network/Transit",
            "Services/DNS",
            "Services/RADIUS",
            "Services/Monitoring",
            "Lab",
            "Archived",
        })
        {
            store.CreateFolder(folder);
        }

        foreach (var folder in new[] { "Admin", "Development", "WSL" })
            store.CreateFolder(folder, SessionKind.Local);

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
            FolderPath = "Admin",
            Icon = "windows",
            ColorTag = "#4F9DFF",
            Notes = "Administrative shell",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393902"),
            Name = "Developer Shell",
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "cmd.exe" },
            FolderPath = "Development",
            Icon = "windows",
            ColorTag = "#B27CFF",
            Notes = "Local development environment",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393903"),
            Name = "Ubuntu 24.04",
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "wsl.exe", Arguments = ["-d", "Ubuntu-24.04"] },
            FolderPath = "WSL",
            Icon = "ubuntu",
            ColorTag = "#E67E45",
            Notes = "Local WSL environment",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393904"),
            Name = "syd-spine-01",
            FolderPath = "DC1 Sydney/Spine",
            Host = "10.10.1.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            ColorTag = "#4F9DFF",
            Notes = "Sydney fabric spine switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393905"),
            Name = "syd-spine-02",
            FolderPath = "DC1 Sydney/Spine",
            Host = "10.10.1.12",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            ColorTag = "#4F9DFF",
            Notes = "Sydney fabric spine switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393906"),
            Name = "syd-leaf-01",
            FolderPath = "DC1 Sydney/Leaf",
            Host = "10.10.2.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            ColorTag = "#4F9DFF",
            Notes = "Sydney server leaf switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393907"),
            Name = "syd-leaf-02",
            FolderPath = "DC1 Sydney/Leaf",
            Host = "10.10.2.12",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            ColorTag = "#4F9DFF",
            Notes = "Sydney server leaf switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393908"),
            Name = "syd-hv-01",
            FolderPath = "DC1 Sydney/Hypervisors",
            Host = "syd-hv-01.example.net",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "proxmox",
            ColorTag = "#59C3A5",
            Notes = "Sydney Proxmox compute host",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393909"),
            Name = "syd-hv-02",
            FolderPath = "DC1 Sydney/Hypervisors",
            Host = "syd-hv-02.example.net",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "proxmox",
            ColorTag = "#59C3A5",
            Notes = "Sydney Proxmox compute host",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393910"),
            Name = "mel-spine-01",
            FolderPath = "DC2 Melbourne/Spine",
            Host = "10.20.1.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "juniper",
            ColorTag = "#4F9DFF",
            Notes = "Melbourne fabric spine switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393911"),
            Name = "mel-leaf-01",
            FolderPath = "DC2 Melbourne/Leaf",
            Host = "10.20.2.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "juniper",
            ColorTag = "#4F9DFF",
            Notes = "Melbourne server leaf switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393912"),
            Name = "mel-hv-01",
            FolderPath = "DC2 Melbourne/Hypervisors",
            Host = "mel-hv-01.example.net",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "proxmox",
            ColorTag = "#59C3A5",
            Notes = "Melbourne Proxmox compute host",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393913"),
            Name = "bng-syd-01",
            FolderPath = "Network/BNG",
            Host = "192.0.2.10",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "nokia",
            ColorTag = "#F0A44B",
            Notes = "Sydney broadband network gateway",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393914"),
            Name = "bng-mel-01",
            FolderPath = "Network/BNG",
            Host = "192.0.2.11",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "nokia",
            ColorTag = "#F0A44B",
            Notes = "Melbourne broadband network gateway",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393915"),
            Name = "route-server-01",
            FolderPath = "Network/Peering",
            Host = "198.51.100.10",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "linux",
            ColorTag = "#B27CFF",
            Notes = "Internet exchange route server",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393916"),
            Name = "transit-rtr-01",
            FolderPath = "Network/Transit",
            Host = "198.51.100.20",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "juniper",
            ColorTag = "#B27CFF",
            Notes = "Upstream transit router",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393917"),
            Name = "dns-auth-01",
            FolderPath = "Services/DNS",
            Host = "dns-auth-01.example.net",
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "debian",
            ColorTag = "#59C3A5",
            Notes = "Authoritative DNS service",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393918"),
            Name = "dns-rec-01",
            FolderPath = "Services/DNS",
            Host = "dns-rec-01.example.net",
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "debian",
            ColorTag = "#59C3A5",
            Notes = "Recursive DNS service",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393919"),
            Name = "radius-01",
            FolderPath = "Services/RADIUS",
            Host = "radius-01.example.net",
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "ubuntu",
            ColorTag = "#E85D75",
            Notes = "Subscriber authentication and accounting",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393920"),
            Name = "monitoring-01",
            FolderPath = "Services/Monitoring",
            Host = "monitoring-01.example.net",
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "ubuntu",
            ColorTag = "#E85D75",
            Notes = "NOC monitoring and alerting",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393921"),
            Name = "sandbox",
            FolderPath = "Lab",
            Host = "sandbox.example.com",
            Username = "developer",
            AuthMethod = AuthMethod.Password,
            Icon = "fedora",
            ColorTag = "#59C3A5",
            Notes = "Disposable integration sandbox",
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
