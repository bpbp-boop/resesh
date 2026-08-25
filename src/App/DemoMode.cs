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
            "Production/Web",
            "Production/Data",
            "Staging",
            "Network/Core",
            "Network/Edge",
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
            Name = "web-01",
            FolderPath = "Production/Web",
            Host = "web-01.example.com",
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "ubuntu",
            ColorTag = "#E85D75",
            Notes = "Primary production web node",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393905"),
            Name = "web-02",
            FolderPath = "Production/Web",
            Host = "web-02.example.com",
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "debian",
            ColorTag = "#E85D75",
            Notes = "Secondary production web node",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393906"),
            Name = "postgres-primary",
            FolderPath = "Production/Data",
            Host = "db-01.example.com",
            Username = "dba",
            AuthMethod = AuthMethod.PrivateKey,
            Persistent = true,
            Icon = "linux",
            ColorTag = "#F0A44B",
            Notes = "Primary database server",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393907"),
            Name = "app-staging",
            FolderPath = "Staging",
            Host = "staging.example.com",
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "ubuntu",
            ColorTag = "#B27CFF",
            Notes = "Staging application environment",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393908"),
            Name = "core-rtr-01",
            FolderPath = "Network/Core",
            Host = "192.0.2.10",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "cisco",
            ColorTag = "#4F9DFF",
            Notes = "Core router",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393909"),
            Name = "core-sw-01",
            FolderPath = "Network/Core",
            Host = "192.0.2.20",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "arista",
            ColorTag = "#4F9DFF",
            Notes = "Core switch",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393910"),
            Name = "edge-fw-01",
            FolderPath = "Network/Edge",
            Host = "192.0.2.30",
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            Icon = "fortinet",
            ColorTag = "#59C3A5",
            Notes = "Edge firewall",
        },
        new()
        {
            Id = Guid.Parse("c7fe3f75-8527-4ba7-aef7-c16498393911"),
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
