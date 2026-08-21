namespace Resesh.Core.Models;

/// <summary>One built-in session icon: its stable key (also the bundled SVG filename,
/// without extension), display name, and picker group.</summary>
public sealed record SessionIconInfo(string Key, string Name, string Group);

/// <summary>
/// The built-in icon catalog and the server-banner → icon suggestion logic. Rendering
/// (SVG loading, custom-icon directory scanning) lives in the app layer; this is the
/// UI-free part so it can be unit tested.
/// </summary>
public static class SessionIcons
{
    /// <summary>Sentinel icon key meaning "explicitly no icon" — blocks auto-suggestion,
    /// unlike null (unset), which allows it.</summary>
    public const string None = "none";

    public const string LinuxGroup = "Linux / Unix";
    public const string NetworkGroup = "Network";

    public static readonly IReadOnlyList<SessionIconInfo> BuiltIn =
    [
        new("linux", "Linux", LinuxGroup),
        new("proxmox", "Proxmox", LinuxGroup),
        new("debian", "Debian", LinuxGroup),
        new("ubuntu", "Ubuntu", LinuxGroup),
        new("rhel", "Red Hat", LinuxGroup),
        new("centos", "CentOS", LinuxGroup),
        new("fedora", "Fedora", LinuxGroup),
        new("suse", "SUSE", LinuxGroup),
        new("arch", "Arch", LinuxGroup),
        new("alpine", "Alpine", LinuxGroup),
        new("freebsd", "FreeBSD", LinuxGroup),
        new("openbsd", "OpenBSD", LinuxGroup),
        new("windows", "Windows", LinuxGroup),
        new("macos", "macOS", LinuxGroup),
        new("cisco", "Cisco", NetworkGroup),
        new("juniper", "Juniper", NetworkGroup),
        new("arista", "Arista", NetworkGroup),
        new("nokia", "Nokia", NetworkGroup),
        new("paloalto", "Palo Alto", NetworkGroup),
        new("fortinet", "Fortinet", NetworkGroup),
        new("mikrotik", "MikroTik", NetworkGroup),
        new("vyos", "VyOS", NetworkGroup),
        new("aruba", "HPE / Aruba", NetworkGroup),
        new("router", "Router", NetworkGroup),
        new("switch", "Switch", NetworkGroup),
        new("firewall", "Firewall", NetworkGroup),
        new("server", "Server", NetworkGroup),
    ];

    public static bool IsBuiltIn(string key) =>
        BuiltIn.Any(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // Checked in order, so put more specific markers first. Banners are the SSH version
    // exchange string, e.g. "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.10" or "SSH-2.0-Cisco-1.25".
    private static readonly (string Marker, string Key)[] BannerMarkers =
    [
        ("openssh_for_windows", "windows"),
        ("proxmox", "proxmox"),
        ("raspbian", "debian"),
        ("debian", "debian"),
        ("ubuntu", "ubuntu"),
        ("fedora", "fedora"),
        ("centos", "centos"),
        ("red hat", "rhel"),
        ("redhat", "rhel"),
        ("rhel", "rhel"),
        ("suse", "suse"),
        ("alpine", "alpine"),
        ("freebsd", "freebsd"),
        ("openbsd", "openbsd"),
        ("cisco", "cisco"),
        ("rosssh", "mikrotik"), // MikroTik RouterOS announces "SSH-2.0-ROSSSH"
        ("mikrotik", "mikrotik"),
        ("forti", "fortinet"),
        ("juniper", "juniper"),
        ("junos", "juniper"),
        ("arista", "arista"),
        ("palo alto", "paloalto"),
        ("paloalto", "paloalto"),
        ("pan-os", "paloalto"),
        ("aruba", "aruba"),
        ("vyos", "vyos"),
    ];

    /// <summary>
    /// Maps an SSH server version banner to a built-in icon key, or null when the banner
    /// doesn't identify the OS/vendor (a bare "SSH-2.0-OpenSSH_9.6" suggests nothing).
    /// </summary>
    public static string? SuggestFromBanner(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner))
            return null;
        foreach (var (marker, key) in BannerMarkers)
        {
            if (banner.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }
}
