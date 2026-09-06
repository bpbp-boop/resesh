using Resesh.Core.Credentials;
using Resesh.Core.Import;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App;

/// <summary>An isolated, disposable data set for product screenshots.</summary>
internal static class DemoMode
{
    private static readonly Lazy<string> DataDirectory = new(CreateDataDirectory);
    private static readonly Lazy<ImportScanResult> SecureCrtSamples = new(CreateSecureCrtSamples);

    public static ImportScanResult EmptyImportScan() => new() { Importable = [], Skipped = [] };

    public static ImportScanResult ScanSecureCrt() =>
        IsEnabled ? SecureCrtSamples.Value : SecureCrtImporter.ScanDefault();

    private static ImportScanResult CreateSecureCrtSamples()
    {
        var root = StorePath("SecureCRT");
        foreach (var (folder, name, protocol) in new[]
        {
            ("Sydney DC/Subscriber/BNG", "syd-bng-03", "SSH2"),
            ("Sydney DC/Fabric/Leaf", "syd-leaf-05", "SSH2"),
            ("Melbourne DC/Services/RADIUS", "mel-radius-03", "SSH2"),
            ("Perth PoP/Edge & Security/Peering", "per-ix-03", "SSH2"),
            ("Lab/Console", "lab-console-01", "Telnet"),
        })
        {
            var directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".ini"),
                $"S:\"Protocol Name\"={protocol}\nS:\"Hostname\"={name}.mgmt.isp.example\nS:\"Username\"=netops\nD:\"[SSH2] Port\"=00000016\n");
        }
        return SecureCrtImporter.Scan(root);
    }

    public static bool IsEnabled { get; } = Environment.GetCommandLineArgs()
        .Skip(1)
        .Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase));

    public static string StorePath(string fileName) => Path.Combine(DataDirectory.Value, fileName);

    public static ICredentialService CreateCredentialService() =>
        IsEnabled ? new MemoryCredentialService() : new WindowsCredentialService();

    public static void Seed(SessionStore store)
    {
        foreach (var folder in Sessions.Select(session => session.FolderPath)
            .Where(path => !string.IsNullOrEmpty(path)).Distinct())
            store.CreateFolder(folder!);

        foreach (var session in Sessions)
            store.Add(session);
    }

    private static readonly Session[] Sessions = CreateSessions();

    private static Session[] CreateSessions()
    {
        var sessions = new List<Session>
        {
            new() { Id = DemoId(1), Name = "PowerShell 7", Kind = SessionKind.Local,
                Local = new LocalTarget { Executable = "pwsh.exe" }, Icon = "windows" },
            new() { Id = DemoId(2), Name = "Command Prompt", Kind = SessionKind.Local,
                Local = new LocalTarget { Executable = "cmd.exe" }, Icon = "windows" },
            new() { Id = DemoId(3), Name = "Ubuntu 24.04", Kind = SessionKind.Local,
                Local = new LocalTarget { Executable = "wsl.exe", Arguments = ["-d", "Ubuntu-24.04"] }, Icon = "ubuntu" },
        };

        // Fictional management endpoints: demo data never contains customer addresses.
        void Add(string site, string role, int node, string folder, string icon, string notes,
            bool persistent = false, string? color = null)
        {
            var name = $"{site}-{role}-{node:00}";
            sessions.Add(new Session
            {
                Id = DemoId(sessions.Count + 1), Name = name, FolderPath = folder,
                Host = $"{name}.mgmt.isp.example", Username = persistent ? "sysops" : "netops",
                AuthMethod = AuthMethod.PrivateKey, Icon = icon, Notes = notes,
                Persistent = persistent, ColorTag = color,
            });
        }

        foreach (var (site, city) in new[] { ("syd", "Sydney DC"), ("mel", "Melbourne DC"),
            ("bne", "Brisbane PoP"), ("per", "Perth PoP") })
        {
            var dc = city;
            for (var node = 1; node <= 2; node++)
            {
                Add(site, "p", node, $"{dc}/Network/Core", "cisco", $"{city} MPLS core router; diverse national backbone paths", color: "#0078D4");
                Add(site, "pe", node, $"{dc}/Network/Provider edge", "juniper", $"{city} enterprise L3VPN and EVPN provider edge");
                Add(site, "ix", node, $"{dc}/Edge & Security/Peering", "juniper", $"{city} public IX and private peering edge; IPv4 and IPv6");
                Add(site, "transit", node, $"{dc}/Edge & Security/Transit", "cisco", $"{city} upstream transit edge; independent upstream paths");
                Add(site, "bng", node, $"{dc}/Subscriber/BNG", "nokia", $"{city} broadband subscriber termination; IPoE and PPPoE", color: "#FFB900");
                Add(site, "cgn", node, $"{dc}/Subscriber/CGNAT", "juniper", $"{city} carrier-grade NAT pool and translation logging");
                Add(site, "agg", node, $"{dc}/Network/Access aggregation", "nokia", $"{city} wholesale NNI and metro access aggregation");
                Add(site, "radius", node, $"{dc}/Services/RADIUS", "debian", $"{city} subscriber authentication, authorisation and accounting", true);
                Add(site, "resolver", node, $"{dc}/Services/DNS recursive", "debian", $"{city} subscriber recursive DNS; anycast and DNSSEC validation", true);
            }
        }

        foreach (var (site, city) in new[] { ("syd", "Sydney DC"), ("mel", "Melbourne DC") })
        {
            var dc = city;
            for (var node = 1; node <= 2; node++)
            {
                Add(site, "spine", node, $"{dc}/Fabric/Spine", "arista", "EVPN/VXLAN fabric spine; independent failure domain");
                Add(site, "border", node, $"{dc}/Fabric/Border leaf", "arista", "Fabric border leaf; routed handoff to provider edge");
                Add(site, "fw", node, $"{dc}/Edge & Security/Firewalls", "paloalto", "Service-zone firewall HA pair", color: "#E74856");
                Add(site, "authdns", node, $"{dc}/Services/DNS authoritative", "debian", "Public authoritative DNS; separate from subscriber resolvers", true);
                Add(site, "dhcp", node, $"{dc}/Services/DHCP", "ubuntu", "Subscriber address allocation and lease service", true);
                Add(site, "bastion", node, $"{dc}/Management/Bastions", "ubuntu", "Audited production management jump host", true, "#10893E");
                Add(site, "oob", node, $"{dc}/Management/Out-of-band", "cisco", "Console access over independent management network");
            }
            for (var node = 1; node <= 4; node++)
                Add(site, "leaf", node, $"{dc}/Fabric/Leaf", "arista", $"Compute rack {node:00}; redundant server uplinks");
            for (var node = 1; node <= 6; node++)
                Add(site, "hv", node, $"{dc}/Hypervisors", "proxmox", "Proxmox VE service cluster node", true);
            Add(site, "metrics", 1, $"{dc}/Management/Monitoring", "ubuntu", "Prometheus, Alertmanager and network telemetry", true);
            Add(site, "syslog", 1, $"{dc}/Management/Logging", "debian", "Central network syslog and subscriber accounting logs", true);
            Add(site, "rpki", 1, $"{dc}/Services/RPKI", "debian", "RPKI validator; RTR feeds to peering and transit routers", true);
            Add(site, "ntp", 1, $"{dc}/Services/NTP", "debian", "Internal time service for network and subscriber systems", true);
        }
        Add("lab", "bng", 1, "Lab/Subscriber validation", "nokia", "Isolated subscriber policy and software qualification");
        Add("lab", "evpn", 1, "Lab/Fabric validation", "arista", "Pre-production EVPN change validation");
        Add("lab", "automation", 1, "Lab/Automation", "ubuntu", "Network configuration and rollback testing", true);
        Add("syd", "legacy-pe", 1, "Archived/Decommissioned", "cisco", "Retired provider edge; reference configuration only");
        return sessions.ToArray();
    }

    private static Guid DemoId(int index) =>
        Guid.Parse($"c7fe3f75-8527-4ba7-aef7-{index:000000000000}");

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
