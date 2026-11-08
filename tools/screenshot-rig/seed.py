"""Writes a resesh --data-dir for website screenshots: the fictional ISP session tree from
DemoMode, shared identities, the demo key, trusted host keys for demo_server.py, and the
Phthalo Green theme. Every SSH session reaches its host through demo_server.py's SOCKS5
proxy on 127.0.0.1:1080.

    python seed.py <data dir> [--theme phthalo-green] [--history]
"""

import argparse
import base64
import hashlib
import json
import os
import shutil
import uuid

import asyncssh

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
# Outside the user profile, so the key path in screenshots names no account.
KEY_PATH = r"C:\Users\Public\Documents\demo-keys\isp-demo"
HOST_KEY_PATH = os.path.join(HERE, ".hostkey")

KEY_ID = "1d2a5e00-0000-4000-8000-00000000000a"
NETOPS = "1d2a5e00-0000-4000-8000-000000000001"
SYSOPS = "1d2a5e00-0000-4000-8000-000000000002"
LAB = "1d2a5e00-0000-4000-8000-000000000003"


def demo_id(i: int) -> str:
    return f"c7fe3f75-8527-4ba7-aef7-{i:012d}"


def sessions():
    out = [
        dict(id=demo_id(1), name="PowerShell 7", kind="local", icon="windows",
             local=dict(executable="pwsh.exe", arguments=["-NoLogo"])),
        dict(id=demo_id(2), name="Command Prompt", kind="local", icon="windows",
             local=dict(executable="cmd.exe")),
    ]

    def add(site, role, node, folder, icon, notes, persistent=False, color=None):
        name = f"{site}-{role}-{node:02d}"
        s = dict(id=demo_id(len(out) + 1), name=name, folderPath=folder, host=f"{name}.mgmt.isp.example",
                 port=22, inheritLogin=True, icon=icon, notes=notes,
                 proxy=dict(kind="socks5", host="127.0.0.1", port=1080))
        if color:
            s["colorTag"] = color
        out.append(s)

    for site, city in [("syd", "Sydney DC"), ("mel", "Melbourne DC"), ("bne", "Brisbane PoP"), ("per", "Perth PoP")]:
        for node in (1, 2):
            add(site, "p", node, f"{city}/Network/Core", "cisco", f"{city} MPLS core router; diverse national backbone paths", color="#0078D7")
            add(site, "pe", node, f"{city}/Network/Provider edge", "juniper", f"{city} enterprise L3VPN and EVPN provider edge")
            add(site, "ix", node, f"{city}/Edge & Security/Peering", "juniper", f"{city} public IX and private peering edge; IPv4 and IPv6")
            add(site, "transit", node, f"{city}/Edge & Security/Transit", "cisco", f"{city} upstream transit edge; independent upstream paths")
            add(site, "bng", node, f"{city}/Subscriber/BNG", "nokia", f"{city} broadband subscriber termination; IPoE and PPPoE", color="#FFB900")
            add(site, "cgn", node, f"{city}/Subscriber/CGNAT", "juniper", f"{city} carrier-grade NAT pool and translation logging")
            add(site, "agg", node, f"{city}/Network/Access aggregation", "nokia", f"{city} wholesale NNI and metro access aggregation")
            add(site, "radius", node, f"{city}/Services/RADIUS", "debian", f"{city} subscriber authentication, authorisation and accounting")
            add(site, "resolver", node, f"{city}/Services/DNS recursive", "debian", f"{city} subscriber recursive DNS; anycast and DNSSEC validation")
    for site, city in [("syd", "Sydney DC"), ("mel", "Melbourne DC")]:
        for node in (1, 2):
            add(site, "spine", node, f"{city}/Fabric/Spine", "arista", "EVPN/VXLAN fabric spine; independent failure domain")
            add(site, "border", node, f"{city}/Fabric/Border leaf", "arista", "Fabric border leaf; routed handoff to provider edge")
            add(site, "fw", node, f"{city}/Edge & Security/Firewalls", "paloalto", "Service-zone firewall HA pair", color="#E74856")
            add(site, "authdns", node, f"{city}/Services/DNS authoritative", "debian", "Public authoritative DNS; separate from subscriber resolvers")
            add(site, "dhcp", node, f"{city}/Services/DHCP", "ubuntu", "Subscriber address allocation and lease service")
            add(site, "bastion", node, f"{city}/Management/Bastions", "ubuntu", "Audited production management jump host", color="#10893E")
            add(site, "oob", node, f"{city}/Management/Out-of-band", "cisco", "Console access over independent management network")
        for node in range(1, 5):
            add(site, "leaf", node, f"{city}/Fabric/Leaf", "arista", f"Compute rack {node:02d}; redundant server uplinks")
        for node in range(1, 7):
            add(site, "hv", node, f"{city}/Hypervisors", "proxmox", "Proxmox VE service cluster node")
        add(site, "metrics", 1, f"{city}/Management/Monitoring", "ubuntu", "Prometheus, Alertmanager and network telemetry")
        add(site, "syslog", 1, f"{city}/Management/Logging", "debian", "Central network syslog and subscriber accounting logs")
        add(site, "rpki", 1, f"{city}/Services/RPKI", "debian", "RPKI validator; RTR feeds to peering and transit routers")
        add(site, "ntp", 1, f"{city}/Services/NTP", "debian", "Internal time service for network and subscriber systems")
    add("lab", "bng", 1, "Lab/Subscriber validation", "nokia", "Isolated subscriber policy and software qualification")
    add("lab", "evpn", 1, "Lab/Fabric validation", "arista", "Pre-production EVPN change validation")
    add("lab", "automation", 1, "Lab/Automation", "ubuntu", "Network configuration and rollback testing")
    add("syd", "legacy-pe", 1, "Archived/Decommissioned", "cisco", "Retired provider edge; reference configuration only")
    return out


def folders(items):
    paths = set()
    for s in items:
        path = s.get("folderPath") or ""
        parts = path.split("/") if path else []
        for i in range(1, len(parts) + 1):
            paths.add("/".join(parts[:i]))
    return sorted(paths)


def folder_settings():
    out = []
    for city in ("Sydney DC", "Melbourne DC", "Brisbane PoP", "Perth PoP"):
        out.append(dict(path=city, identityId=NETOPS))
    # Two folders keep the identity's "used by" line short in the Identities screenshot.
    for city in ("Sydney DC", "Melbourne DC"):
        out.append(dict(path=f"{city}/Management", identityId=SYSOPS))
    out.append(dict(path="Lab", identityId=LAB))
    return out


def blob_fingerprint(blob: bytes) -> str:
    return base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dir")
    parser.add_argument("--theme", default="phthalo-green")
    parser.add_argument("--appearance", default="dark")
    parser.add_argument("--colors", default="accent")
    parser.add_argument("--history", action="store_true")
    parser.add_argument("--status-bar", action="store_true")
    parser.add_argument("--pane-open", action="store_true")
    args = parser.parse_args()

    shutil.rmtree(args.dir, ignore_errors=True)
    os.makedirs(args.dir)

    def dump(name, data):
        with open(os.path.join(args.dir, name), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

    items = sessions()
    dump("sessions.json", dict(sessions=items, folders=folders(items), localFolders=[],
                               folderSettings=folder_settings()))

    if not os.path.exists(KEY_PATH):
        os.makedirs(os.path.dirname(KEY_PATH), exist_ok=True)
        asyncssh.generate_private_key("ssh-ed25519", comment="demo-only@isp.example").write_private_key(KEY_PATH)
    user_key = asyncssh.read_private_key(KEY_PATH)
    pub = " ".join(user_key.export_public_key().decode().split()[:2]) + " demo-only@isp.example"
    blob = base64.b64decode(pub.split()[1])
    dump("ssh-keys.json", dict(keys=[dict(id=KEY_ID, name="isp-demo", path=KEY_PATH, algorithm="ssh-ed25519",
                                          keyLength=256, fingerprint="SHA256:" + blob_fingerprint(blob),
                                          isEncrypted=False, publicKey=pub)]))
    dump("identities.json", dict(identities=[
        dict(id=NETOPS, name="Network operations", username="netops", proof="key", keyId=KEY_ID),
        dict(id=SYSOPS, name="Systems", username="sysops", proof="key", keyId=KEY_ID),
        dict(id=LAB, name="Lab", username="labadmin", proof="key", keyId=KEY_ID),
    ]))

    host_pub = asyncssh.read_private_key(HOST_KEY_PATH).export_public_key().decode().split()
    host_blob = base64.b64decode(host_pub[1])
    known = {f"{s['host']}:22": dict(KeyType=host_pub[0], Sha256=blob_fingerprint(host_blob), PublicKeyBase64=host_pub[1])
             for s in items if s.get("host")}
    dump("known_hosts.json", known)

    dark = args.appearance != "light"
    dump("settings.json", dict(
        onboardingCompleted=True, appearance=args.appearance,
        darkTheme=args.theme if dark else "dark", lightTheme=args.theme if not dark else "light",
        themeColors=args.colors, fontSize=13, showStatusBar=args.status_bar, sessionsPaneOpen=args.pane_open,
        keepCommandHistory=args.history, confirmCloseActiveSessions=False, reopenLastLayoutAtStartup=False))
    print(args.dir)


if __name__ == "__main__":
    main()
