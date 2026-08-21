using System.Text.Json.Serialization;

namespace Resesh.Core.Models;

/// <summary>
/// One keyword-highlight rule: a regex applied per terminal row, painting its matches.
/// Patterns must stay in the syntax subset valid in both .NET and JavaScript regexes —
/// they are validated host-side but executed by the xterm page.
/// </summary>
public sealed record HighlightRule
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>"builtin:&lt;pack&gt;" for shipped rules, "custom" for user-defined ones.</summary>
    public string Pack { get; init; } = "custom";

    public string Pattern { get; init; } = "";

    /// <summary>Foreground color for matches, #RRGGBB.</summary>
    public string Color { get; init; } = "#ffffff";

    /// <summary>Extra emphasis. The decorations API cannot re-weight glyphs, so the page
    /// renders bold as a translucent background tint of the rule color.</summary>
    public bool Bold { get; init; }

    public bool Underline { get; init; }

    public bool MatchCase { get; init; }

    /// <summary>Marks this rule's hits in the annotated scrollbar's content lane. Off by
    /// default for all but the negative-states rule: the ruler answers "where is it
    /// broken", not "where is there text".</summary>
    public bool ShowInOverview { get; init; }

    /// <summary>Global enabled state (session overrides layer on top of this).</summary>
    public bool Enabled { get; init; } = true;

    [JsonIgnore]
    public bool IsBuiltin => Pack.StartsWith("builtin:", StringComparison.Ordinal);
}

/// <summary>
/// The shipped rule packs, adapted from netOS-cli's SecureCRT keyword packs.
/// Ids are stable — they are referenced by global and per-session enable deltas.
/// </summary>
public static class BuiltinHighlights
{
    public const string NetworkPack = "builtin:network";
    public const string GenericPack = "builtin:generic";

    /// <summary>Rule order matters: for overlapping matches the later rule wins
    /// (e.g. MAC addresses are listed after IPv6 so colon-hex paints as MAC).</summary>
    public static IReadOnlyList<HighlightRule> Rules { get; } =
    [
        new()
        {
            Id = "iface-cisco", Name = "Interfaces (network OS)", Pack = NetworkPack,
            Pattern = @"\b(?:(?:Hundred|Forty|TwentyFive|Ten|Two|Four)?Gig(?:abit)?E(?:thernet)?|FastEthernet|Ethernet|Serial|Loopback|Tunnel|Vlan|Port-?channel|Bundle-Ether|BVI|BDI|Hu|Fo|Twe|Te|Gi|Fa|Eth|Se|Lo|Tu|Vl|Po)\d+(?:/\d+)*(?:\.\d+)?\b",
            Color = "#e5c07b",
        },
        new()
        {
            Id = "iface-linux", Name = "Interfaces (Linux)", Pack = NetworkPack,
            Pattern = @"\b(?:eth|ens|eno|enp\d+s|wlan|wlp\d+s|bond|br|tap|tun|veth|virbr|docker|vmnet|ppp)\d+(?:\.\d+)?\b|\blo\b",
            Color = "#e5c07b",
        },
        new()
        {
            Id = "ipv4", Name = "IPv4 addresses", Pack = NetworkPack,
            Pattern = @"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b",
            Color = "#61afef",
        },
        new()
        {
            // Compressed ("::") form first so it wins over the plain form's shorter prefix
            // match; the plain form needs 3+ groups so hh:mm:ss timestamps don't match;
            // the lookbehind keeps C++ "std::name" tokens out.
            Id = "ipv6", Name = "IPv6 addresses", Pack = NetworkPack,
            Pattern = @"\b(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?(?:/\d{1,3})?|\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}(?:/\d{1,3})?\b|(?<![\w:])::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)(?:/\d{1,3})?\b",
            Color = "#56b6c2",
        },
        new()
        {
            Id = "mac", Name = "MAC addresses", Pack = NetworkPack,
            Pattern = @"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}\b",
            Color = "#c678dd",
        },
        new()
        {
            Id = "state-positive", Name = "Up/positive states", Pack = NetworkPack,
            Pattern = @"\b(?:up|enabled|active|connected|established|listening|running|started|successful(?:ly)?|success|passed|okay|ok|yes|valid|reachable|synchronized)\b",
            Color = "#23d18b",
        },
        new()
        {
            Id = "state-negative", Name = "Down/error states", Pack = NetworkPack,
            Pattern = @"\b(?:down|disabled|shutdown|shut|failure|failed|fail|err-?disabled|error|denied|deny|dropped|refused|rejected|invalid|unreachable|timed[- ]?out|timeout|critical|crit|emergency|emerg|alert|blocked|blocking|notconnect|stopped|dead|expired)\b",
            Color = "#ff5555", Bold = true, ShowInOverview = true,
        },
        new()
        {
            Id = "proto-routing", Name = "Routing protocols", Pack = NetworkPack,
            Pattern = @"\b(?:bgp|ospfv3|ospf|eigrp|ripng|ripv2|rip|is-is|isis|mpls|ldp|rsvp|pim|igmp|msdp|vrrp|hsrp|glbp|lacp|pagp|udld|rstp|mstp|mst|stp|cdp|lldp|bfd|nhrp|gre|ipsec|ikev2|ike)\b",
            Color = "#d19a66",
        },
        new()
        {
            Id = "services", Name = "Services & protocols", Pack = NetworkPack,
            Pattern = @"\b(?:sshd|ssh|telnet|https|http|dns|dhcp|ntp|snmp|syslog|tftp|ftp|sftp|scp|smtp|imap|pop3|ldaps|ldap|radius|tacacs\+?|kerberos|nfs|smb|rdp|vnc)\b",
            Color = "#4ec9b0",
        },
        new()
        {
            Id = "duration", Name = "Durations & uptimes", Pack = NetworkPack,
            Pattern = @"\b\d+[wdhms](?:\d+[wdhms])+\b|\b\d{1,2}:\d{2}:\d{2}\b|\b\d+(?:\.\d+)?ms\b",
            Color = "#ce9178",
        },
        new()
        {
            Id = "quoted-string", Name = "Quoted strings", Pack = GenericPack,
            Pattern = "\"[^\"]*\"|'[^']*'",
            Color = "#ce9178", Enabled = false, // noisy — off by default
        },
        new()
        {
            Id = "number", Name = "Numbers", Pack = GenericPack,
            Pattern = @"\b\d+(?:\.\d+)?\b",
            Color = "#b5cea8", Enabled = false, // noisy — off by default
        },
    ];
}
