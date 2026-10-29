namespace Resesh.Core.Models;

/// <summary>One built-in session icon: its stable key, display name, and picker section.</summary>
public sealed record SessionIconInfo(string Key, string Name, string Group);

/// <summary>Where a brand mark (or device glyph) sits in a picker.</summary>
public enum BrandGroup
{
    NetworkDevices,
    Devices,
    OperatingSystems,
    Infrastructure,
    Network,
    Databases,
    Containers,
    Clouds,
    Web,
    Monitoring,
    Automation,
}

/// <summary>A one-colour brand mark: its saved key, display name, file in
/// Assets\SessionTreeIcons, picker group, and extra words a picker filter matches.</summary>
public sealed record BrandIcon(string Key, string Name, string File, BrandGroup Group, string Keywords);

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

    /// <summary>
    /// Brand marks drawn in one colour from Assets\SessionTreeIcons (a 64 px PNG and its SVG
    /// per file): the session tree uses them for sessions, the workspace rail and its picker
    /// for workspaces. This is the one list to grow. Keys are saved by sessions and
    /// workspaces, so never rename one. The session icon keys among them keep the files they had.
    /// </summary>
    /// <remarks>New marks come from Simple Icons (CC0) at a pinned version, rendered black
    /// on transparent with the mark fitted into the centred 52 px, like the originals.
    /// Brands Simple Icons has dropped (AWS, Azure, Oracle, SQL Server) are not available.
    /// Where a brand's mark is mostly lettering it is cropped to its compact part (MySQL to the
    /// dolphin, VMware to "vm") through the SVG's viewBox, like the vendor favicon marks; a
    /// wordmark with no compact part (Synology) is left out, since it is unreadable at 16 px.</remarks>
    public static readonly IReadOnlyList<BrandIcon> Brands =
    [
        new("linux", "Linux", "linux.png", BrandGroup.OperatingSystems, "tux unix"),
        new("debian", "Debian", "debian.png", BrandGroup.OperatingSystems, "raspbian"),
        new("ubuntu", "Ubuntu", "ubuntu.png", BrandGroup.OperatingSystems, ""),
        new("rhel", "Red Hat", "redhat.png", BrandGroup.OperatingSystems, "rhel enterprise linux"),
        new("centos", "CentOS", "centos.png", BrandGroup.OperatingSystems, ""),
        new("fedora", "Fedora", "fedora.png", BrandGroup.OperatingSystems, ""),
        new("suse", "SUSE", "suse.png", BrandGroup.OperatingSystems, "opensuse sles"),
        new("arch", "Arch", "archlinux.png", BrandGroup.OperatingSystems, "archlinux"),
        new("alpine", "Alpine", "alpinelinux.png", BrandGroup.OperatingSystems, "alpinelinux"),
        new("freebsd", "FreeBSD", "freebsd.png", BrandGroup.OperatingSystems, "bsd"),
        new("openbsd", "OpenBSD", "openbsd.png", BrandGroup.OperatingSystems, "bsd"),
        new("windows", "Windows", "panels-top-left.png", BrandGroup.OperatingSystems, "microsoft"),
        new("macos", "macOS", "apple.png", BrandGroup.OperatingSystems, "apple mac"),
        new("raspberrypi", "Raspberry Pi", "raspberrypi.png", BrandGroup.OperatingSystems, "pi"),
        new("proxmox", "Proxmox", "proxmox.png", BrandGroup.Infrastructure, "pve hypervisor virtualization"),
        new("vmware", "VMware", "vmware.png", BrandGroup.Infrastructure, "esxi vsphere hypervisor virtualization"),
        new("truenas", "TrueNAS", "truenas.png", BrandGroup.Infrastructure, "nas storage zfs"),
        new("unraid", "Unraid", "unraid.png", BrandGroup.Infrastructure, "nas storage"),
        new("homeassistant", "Home Assistant", "homeassistant.png", BrandGroup.Infrastructure, "home automation"),
        new("cisco", "Cisco", "cisco.png", BrandGroup.Network, "ios"),
        new("juniper", "Juniper", "junipernetworks.png", BrandGroup.Network, "junos"),
        new("arista", "Arista", "arista.png", BrandGroup.Network, "eos"),
        new("nokia", "Nokia", "nokia.png", BrandGroup.Network, "sros"),
        new("paloalto", "Palo Alto", "paloaltonetworks.png", BrandGroup.Network, "pan-os firewall"),
        new("fortinet", "Fortinet", "fortinet.png", BrandGroup.Network, "fortigate firewall"),
        new("mikrotik", "MikroTik", "mikrotik.png", BrandGroup.Network, "routeros"),
        new("vyos", "VyOS", "vyos.png", BrandGroup.Network, "router"),
        new("opnsense", "OPNsense", "opnsense.png", BrandGroup.Network, "firewall"),
        new("pfsense", "pfSense", "pfsense.png", BrandGroup.Network, "firewall"),
        new("cloudflare", "Cloudflare", "cloudflare.png", BrandGroup.Network, "cdn dns tunnel"),
        new("mysql", "MySQL", "mysql.png", BrandGroup.Databases, "sql database db"),
        new("mariadb", "MariaDB", "mariadb.png", BrandGroup.Databases, "sql database db mysql"),
        new("postgresql", "PostgreSQL", "postgresql.png", BrandGroup.Databases, "postgres sql database db"),
        new("couchdb", "CouchDB", "apachecouchdb.png", BrandGroup.Databases, "apache nosql database db"),
        new("mongodb", "MongoDB", "mongodb.png", BrandGroup.Databases, "mongo nosql database db"),
        new("redis", "Redis", "redis.png", BrandGroup.Databases, "cache valkey database db"),
        new("sqlite", "SQLite", "sqlite.png", BrandGroup.Databases, "sql database db"),
        new("elasticsearch", "Elasticsearch", "elasticsearch.png", BrandGroup.Databases, "elastic search opensearch"),
        new("cassandra", "Cassandra", "apachecassandra.png", BrandGroup.Databases, "apache nosql database db"),
        new("clickhouse", "ClickHouse", "clickhouse.png", BrandGroup.Databases, "analytics database db"),
        new("influxdb", "InfluxDB", "influxdb.png", BrandGroup.Databases, "time series database db"),
        new("rabbitmq", "RabbitMQ", "rabbitmq.png", BrandGroup.Databases, "queue messaging amqp"),
        new("kafka", "Kafka", "apachekafka.png", BrandGroup.Databases, "apache queue messaging streaming"),
        new("docker", "Docker", "docker.png", BrandGroup.Containers, "container compose"),
        new("kubernetes", "Kubernetes", "kubernetes.png", BrandGroup.Containers, "k8s k3s cluster"),
        new("podman", "Podman", "podman.png", BrandGroup.Containers, "container"),
        new("helm", "Helm", "helm.png", BrandGroup.Containers, "kubernetes chart"),
        new("rancher", "Rancher", "rancher.png", BrandGroup.Containers, "kubernetes k3s"),
        new("portainer", "Portainer", "portainer.png", BrandGroup.Containers, "docker container"),
        new("googlecloud", "Google Cloud", "googlecloud.png", BrandGroup.Clouds, "gcp gce"),
        new("digitalocean", "DigitalOcean", "digitalocean.png", BrandGroup.Clouds, "droplet vps"),
        new("hetzner", "Hetzner", "hetzner.png", BrandGroup.Clouds, "vps dedicated"),
        new("akamai", "Akamai", "akamai.png", BrandGroup.Clouds, "linode vps cdn"),
        new("vultr", "Vultr", "vultr.png", BrandGroup.Clouds, "vps"),
        new("ovh", "OVHcloud", "ovh.png", BrandGroup.Clouds, "ovhcloud vps dedicated"),
        new("nginx", "nginx", "nginx.png", BrandGroup.Web, "web proxy server"),
        new("apache", "Apache", "apache.png", BrandGroup.Web, "httpd web server"),
        new("caddy", "Caddy", "caddy.png", BrandGroup.Web, "web proxy server"),
        new("traefik", "Traefik", "traefikproxy.png", BrandGroup.Web, "proxy ingress"),
        new("grafana", "Grafana", "grafana.png", BrandGroup.Monitoring, "dashboard metrics"),
        new("prometheus", "Prometheus", "prometheus.png", BrandGroup.Monitoring, "metrics alerting"),
        new("datadog", "Datadog", "datadog.png", BrandGroup.Monitoring, "metrics apm"),
        new("ansible", "Ansible", "ansible.png", BrandGroup.Automation, "automation playbook"),
        new("terraform", "Terraform", "terraform.png", BrandGroup.Automation, "iac opentofu"),
        new("jenkins", "Jenkins", "jenkins.png", BrandGroup.Automation, "ci build"),
        new("gitlab", "GitLab", "gitlab.png", BrandGroup.Automation, "git ci repository"),
        new("github", "GitHub", "github.png", BrandGroup.Automation, "git ci repository actions"),
        new("git", "Git", "git.png", BrandGroup.Automation, "repository source"),
    ];

    /// <summary>Generic device glyphs for hosts that are no particular brand, from the same folder
    /// and drawn the same way: Lucide (ISC) glyphs, plus a network switch drawn in Lucide's style
    /// since Lucide has none. Sessions and workspaces both offer them. Keys are saved, so never
    /// rename one; "aruba" and "switch" are keys from before the rename and redraw.</summary>
    public static readonly IReadOnlyList<BrandIcon> DeviceGlyphs =
    [
        new("router", "Router", "router.png", BrandGroup.NetworkDevices, "gateway cpe edge"),
        new("switch", "Switch", "switch.png", BrandGroup.NetworkDevices, "network lan ethernet layer 2 l2 l3 core access"),
        new("firewall", "Firewall", "shield.png", BrandGroup.NetworkDevices, "security utm ngfw"),
        new("aruba", "Wireless", "wifi.png", BrandGroup.NetworkDevices, "wifi wlan access point ap controller hpe aruba"),
        new("network", "Network", "network.png", BrandGroup.NetworkDevices, "topology lan segment subnet vlan"),
        new("ethernet", "Ethernet port", "ethernet-port.png", BrandGroup.NetworkDevices, "patch panel port rj45 lan"),
        new("console", "Console cable", "cable.png", BrandGroup.NetworkDevices, "serial console server rollover oob out of band"),
        new("loadbalancer", "Load balancer", "split.png", BrandGroup.NetworkDevices, "lb haproxy f5 proxy adc"),
        new("vpn", "VPN", "globe-lock.png", BrandGroup.NetworkDevices, "tunnel ipsec wireguard openvpn concentrator"),
        new("internet", "Internet", "globe.png", BrandGroup.NetworkDevices, "wan isp uplink transit"),
        new("mesh", "Mesh / SD-WAN", "waypoints.png", BrandGroup.NetworkDevices, "sdwan overlay tailscale zerotier"),
        new("radio", "Radio tower", "radio-tower.png", BrandGroup.NetworkDevices, "ptp ptmp backhaul microwave wisp"),
        new("cellular", "Cellular", "signal.png", BrandGroup.NetworkDevices, "lte 4g 5g modem mobile"),
        new("satellite", "Satellite", "satellite-dish.png", BrandGroup.NetworkDevices, "starlink vsat dish"),
        new("server", "Server", "server.png", BrandGroup.Devices, "host machine"),
        new("storage", "Storage", "hard-drive.png", BrandGroup.Devices, "nas san disk drive array"),
        new("database", "Database", "database.png", BrandGroup.Devices, "db sql data"),
        new("pdu", "PDU", "plug-zap.png", BrandGroup.Devices, "power distribution outlet"),
        new("ups", "UPS", "battery-charging.png", BrandGroup.Devices, "power battery"),
        new("embedded", "Embedded", "circuit-board.png", BrandGroup.Devices, "iot board microcontroller esp32 arduino firmware"),
        new("printer", "Printer", "printer.png", BrandGroup.Devices, "print"),
        new("camera", "Camera", "cctv.png", BrandGroup.Devices, "cctv ip camera nvr surveillance"),
        new("phone", "VoIP phone", "phone.png", BrandGroup.Devices, "sip pbx telephone voip"),
    ];

    /// <summary>Every built-in session icon in picker order: device glyphs, then brands by group.
    /// Each is a one-colour image in Assets\SessionTreeIcons.</summary>
    public static readonly IReadOnlyList<BrandIcon> TreeIcons =
        [.. DeviceGlyphs, .. Brands.OrderBy(brand => brand.Group)];

    public static readonly IReadOnlyList<SessionIconInfo> BuiltIn =
        [.. TreeIcons.Select(icon => new SessionIconInfo(icon.Key, icon.Name, GroupName(icon.Group)))];

    /// <summary>A picker section title for each group.</summary>
    public static string GroupName(BrandGroup group) => group switch
    {
        BrandGroup.NetworkDevices => "Network devices",
        BrandGroup.Devices => "Devices",
        BrandGroup.OperatingSystems => "Operating systems",
        BrandGroup.Infrastructure => "Servers and storage",
        BrandGroup.Network => "Network",
        BrandGroup.Databases => "Databases and queues",
        BrandGroup.Containers => "Containers",
        BrandGroup.Clouds => "Clouds",
        BrandGroup.Web => "Web servers",
        BrandGroup.Monitoring => "Monitoring",
        _ => "Automation and code",
    };

    /// <summary>Whether a picker entry matches filter text: every word must appear in its
    /// name, key or search words, ignoring case.</summary>
    public static bool Matches(string filter, string name, string key, string keywords) =>
        string.IsNullOrWhiteSpace(filter)
        || filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || key.Contains(word, StringComparison.OrdinalIgnoreCase)
            || keywords.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>How well a matching entry fits the filter, lower first: its name or key exactly,
    /// a name starting with it, a name containing it, then a search word. "mysql" puts MySQL
    /// ahead of an icon that only lists mysql among its search words.</summary>
    public static int MatchRank(string filter, string name, string key)
    {
        var text = filter.Trim();
        if (text.Length == 0)
            return 0;
        if (name.Equals(text, StringComparison.OrdinalIgnoreCase) || key.Equals(text, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase) || key.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Contains(text, StringComparison.OrdinalIgnoreCase) || key.Contains(text, StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

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
