"""Fake ISP devices for website screenshots.

A SOCKS5 proxy on 127.0.0.1:1080 accepts CONNECT requests for any *.isp.example host and
hands each host its own in-process SSH server, so resesh shows the real fictional host
names everywhere (tabs, status bar, history targets) while nothing leaves the machine.
The persona comes from the host name's role: pe/ix/cgn are Junos, p/transit/oob Cisco IOS
XR/IOS, bng/agg Nokia MD-CLI, spine/leaf/border Arista EOS, everything else Linux.

Every device accepts any user with any password or key. Output is fictional: documentation
addresses (RFC 5737), documentation AS numbers (RFC 5398) and made-up counters.

    python demo_server.py [--root <sftp home folder>]
"""

import argparse
import asyncio
import datetime as dt
import os
import random
import re
import struct
import sys

import asyncssh

from glyphs import showcase

HOST_KEY_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".hostkey")
ESC = "\x1b"
OSC_END = "\x1b\\"

JUNOS = {"pe", "ix", "cgn", "legacy-pe"}
IOS = {"p", "transit", "oob"}
NOKIA = {"bng", "agg"}
ARISTA = {"spine", "leaf", "border", "evpn"}


def role_of(host: str) -> str:
    name = host.split(".")[0]
    parts = name.split("-")
    return "-".join(parts[1:-1]) if len(parts) >= 3 else name


def short_name(host: str) -> str:
    return host.split(".")[0]


# --------------------------------------------------------------------------------------
# Shared fictional state, so repeated commands change the way a real network would.
# --------------------------------------------------------------------------------------
class Network:
    def __init__(self):
        self.runs: dict[tuple[str, str], int] = {}
        # The run of `show bgp summary` on each router from which peer 203.0.113.21 is down.
        self.down_from = 2

    def bump(self, host: str, key: str) -> int:
        n = self.runs.get((host, key), 0) + 1
        self.runs[(host, key)] = n
        return n


NETWORK = Network()


def crlf(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\n", "\r\n")


TODAY = dt.date.today()


def dated(text: str) -> str:
    """The canned output is written for 27 September 2026; show it as happening today."""
    return (text.replace("Sun Sep 27", f"{TODAY:%a %b} {TODAY.day:2d}")
            .replace("Sep 27", f"{TODAY:%b} {TODAY.day:2d}")
            .replace("2026-09-27", TODAY.isoformat()))


# --------------------------------------------------------------------------------------
# Personas
# --------------------------------------------------------------------------------------
class Persona:
    banner = ""

    def __init__(self, host: str, user: str, process):
        self.host = host
        self.name = short_name(host)
        self.user = user
        self.process = process

    def write(self, text: str):
        self.process.stdout.write(crlf(dated(text)))

    def prompt(self) -> str:
        raise NotImplementedError

    async def run(self, line: str) -> bool:
        """Runs one command line; returns False to close the session."""
        raise NotImplementedError

    async def stream(self, lines, delay=0.05):
        for line in lines:
            self.write(line + "\n")
            await asyncio.sleep(delay)


class Junos(Persona):
    def __init__(self, *a):
        super().__init__(*a)
        self.edit: list[str] | None = None
        self.banner = ("--- JUNOS 23.4R2-S3.9 Kernel 64-bit  JNPR-15.0-20240522.0c3bb3c_buil\n")

    def prompt(self):
        if self.edit is None:
            return f"{self.user}@{self.name}> "
        where = "[edit" + (" " + " ".join(self.edit) if self.edit else "") + "]"
        return f"\n{where}\n{self.user}@{self.name}# "

    def bgp_summary(self, run: int, state_override: str | None = None) -> str:
        failed = state_override is not None or run >= NETWORK.down_from
        peer21 = state_override or ("Active" if failed else "Establ")
        down = 0 if peer21 == "Establ" else 1
        flaps = 3 if failed else 2
        jitter = lambda base: base + random.randint(0, 40) + run * 61
        updown21 = "41:07" if failed else "6d 04:12:51"
        rx21 = "121002" if failed else "120934"
        tx21 = "118340" if failed else "118277"
        if state_override == "Establ":
            updown21, flaps = "0:02", 4
        lines = [
            f"Groups: 4 Peers: 6 Down peers: {down}",
            "Table          Tot Paths  Act Paths Suppressed    History Damp State    Pending",
            "inet.0               ",
            f"                  967551     950345          0          0          0          0",
            "bgp.l3vpn.0          ",
            "                   18244      18244          0          0          0          0",
            "Peer                     AS      InPkt     OutPkt    OutQ   Flaps Last Up/Dwn State",
            f"192.0.2.1             64500     {jitter(886233):>6}     {jitter(886079):>6}       0       0    41w3d3h Establ",
            "  inet.0: 950345/967551/967551/0",
            f"192.0.2.2             64500     {jitter(886230):>6}     {jitter(886071):>6}       0       0    41w3d3h Establ",
            "  inet.0: 0/967551/967551/0",
            f"198.51.100.9          64511      {jitter(42310):>5}      {jitter(40291):>5}       0       1    3w5d12h Establ",
            "  bgp.l3vpn.0: 9120/9120/9120/0",
            f"198.51.100.13         64512      {jitter(42027):>5}      {jitter(40287):>5}       0       0    3w5d12h Establ",
            "  bgp.l3vpn.0: 9124/9124/9124/0",
            f"203.0.113.21          64496     {rx21:>6}     {tx21:>6}       0       {flaps} {updown21:>10} {peer21}",
        ]
        if peer21 == "Establ":
            lines.append("  inet.0: 1204/1204/1204/0")
        lines += [
            f"203.0.113.25          64497      {jitter(98611):>5}      {jitter(97405):>5}       0       0    12w2d2h Establ",
            "  inet.0: 861/861/861/0",
        ]
        return "\n".join(lines) + "\n"

    async def run(self, line):
        cmd = line.strip()
        if not cmd:
            return True
        base, _, pipe = cmd.partition("|")
        base = base.strip()
        pipe = pipe.strip()
        if self.edit is not None:
            return await self.run_config(cmd)
        if cmd in ("exit", "quit", "logout"):
            return False
        if base in ("configure", "configure private", "edit"):
            self.write("Entering configuration mode\n" if base != "configure private"
                       else "warning: uncommitted changes will be discarded on exit\nEntering configuration mode\n")
            self.edit = []
            return True
        if base.startswith("show bgp summary"):
            run = NETWORK.bump(self.name, "bgp")
            m = re.match(r"refresh\s*(\d+)?", pipe)
            if m:
                await self.refresh_bgp(int(m.group(1) or 2))
            else:
                self.write(self.bgp_summary(run))
            return True
        if base.startswith("show interfaces terse"):
            out = JUNOS_INTERFACES
            if pipe.startswith("match"):
                pat = pipe[5:].strip().strip('"')
                rx = re.compile(pat)
                out = "\n".join(l for l in out.splitlines() if l.startswith("Interface") or rx.search(l)) + "\n"
            self.write(out)
            return True
        if base.startswith("show log messages"):
            out = JUNOS_LOG
            m = re.match(r"last\s+(\d+)", pipe)
            if m:
                out = "\n".join(out.splitlines()[-int(m.group(1)):]) + "\n"
            elif pipe.startswith("match"):
                pat = pipe[5:].strip().strip('"')
                out = "\n".join(l for l in out.splitlines() if re.search(pat, l, re.I)) + "\n"
            await self.stream(out.splitlines(), 0.004)
            return True
        if base.startswith("show bgp neighbor"):
            failed = NETWORK.runs.get((self.name, "bgp"), 0) >= NETWORK.down_from or NETWORK.down_from <= 1
            state = "Active" if failed else "Established"
            last = "Idle" if failed else "OpenConfirm"
            text = (f"Peer: 203.0.113.21+179 AS 64496 Local: 203.0.113.22+61207 AS 64500\n"
                    f"  Group: transit-upstream-b   Routing-Instance: master\n"
                    f"  Type: External    State: {state}       Flags: <Sync>\n"
                    f"  Last State: {last}    Last Event: ConnectRetry\n"
                    f"  Last Error: Hold Timer Expired Error\n"
                    f"  Error: 'Hold Timer Expired Error' Sent: 3 Recv: 0\n")
            if pipe.startswith("match"):
                pat = pipe[5:].strip().strip('"')
                text = "\n".join(l for l in text.splitlines() if re.search(pat, l)) + "\n"
            self.write(text)
            return True
        if base.startswith("show route summary"):
            self.write(JUNOS_ROUTE_SUMMARY)
            return True
        if base.startswith("show system uptime"):
            self.write(JUNOS_UPTIME)
            return True
        if base.startswith("show chassis routing-engine"):
            self.write(JUNOS_RE)
            return True
        if base.startswith("ping"):
            target = base.split()[1] if len(base.split()) > 1 else "192.0.2.1"
            await self.ping(target)
            return True
        self.write(f"{' ' * (len(self.prompt()) + 0)}^\nunknown command.\n")
        return True

    async def ping(self, target):
        self.write(f"PING {target} ({target}): 56 data bytes\n")
        for seq in range(5):
            await asyncio.sleep(0.4)
            self.write(f"64 bytes from {target}: icmp_seq={seq} ttl=63 time={random.uniform(0.6, 1.4):.3f} ms\n")
        self.write(f"\n--- {target} ping statistics ---\n5 packets transmitted, 5 packets received, 0% packet loss\n"
                   "round-trip min/avg/max/stddev = 0.642/0.913/1.377/0.262 ms\n")

    async def refresh_bgp(self, every):
        states = ["Active", "Active", "Connect", "Active", "Connect", "OpenSent", "OpenConfirm", "Establ"]
        for i, state in enumerate(states):
            now = dt.datetime.now()
            self.write(f"\n---(refreshed at {now:%Y-%m-%d %H:%M:%S} AEDT)---\n")
            self.write(self.bgp_summary(NETWORK.down_from, state))
            await asyncio.sleep(every)

    async def run_config(self, cmd):
        words = cmd.split()
        if not words:
            return True
        if words[0] == "edit":
            self.edit += words[1:]
        elif words[0] == "up":
            self.edit = self.edit[:-1]
        elif words[0] == "top":
            self.edit = []
        elif words[0] in ("exit", "quit"):
            if self.edit:
                self.edit = self.edit[:-1]
            else:
                self.write("Exiting configuration mode\n")
                self.edit = None
        elif words[0] == "show":
            if self.edit and self.edit[:2] == ["protocols", "bgp"]:
                self.write(JUNOS_BGP_CONFIG)
            else:
                self.write("## Last changed: 2026-09-27 08:41:13 AEST\nversion 23.4R2-S3.9;\n")
        elif words[0] == "commit":
            await asyncio.sleep(1.2)
            self.write("re0:\nconfiguration check succeeds\nre1:\ncommit complete\nre0:\ncommit complete\n")
        elif words[0] in ("set", "delete", "deactivate", "activate"):
            pass
        else:
            self.write("syntax error.\n")
        return True


class IosXr(Persona):
    def __init__(self, *a):
        super().__init__(*a)
        self.mode: str | None = None
        self.banner = ("\nIMPORTANT:  READ CAREFULLY\nWelcome to the Demo Software Package, a fictional Cisco IOS XR router.\n"
                       "Authorised access only. All sessions are logged.\n\n")

    def prompt(self):
        base = f"RP/0/RP0/CPU0:{self.name}"
        if self.mode is None:
            return f"{base}#"
        return f"{base}({self.mode})#"

    async def run(self, line):
        cmd = line.strip()
        if not cmd:
            return True
        if self.mode is not None:
            words = cmd.split()
            if cmd in ("end",):
                self.mode = None
            elif words[0] == "exit":
                self.mode = "config" if self.mode != "config" else None
            elif words[0] == "interface":
                self.mode = "config-if"
            elif words[0] == "router" and len(words) > 1 and words[1] == "bgp":
                self.mode = "config-bgp"
            elif words[0] == "neighbor":
                self.mode = "config-bgp-nbr"
            elif words[0] == "commit":
                await asyncio.sleep(0.8)
            return True
        if cmd in ("exit", "quit", "logout"):
            return False
        if cmd in ("conf t", "configure terminal", "configure"):
            self.mode = "config"
            return True
        if cmd.startswith("show ip interface brief") or cmd.startswith("sh ip int br"):
            self.write(f"\n{dt.datetime(2026, 9, 27, 8, 52, 11):%a %b %d %H:%M:%S.%f}"[:-3] + " AEST\n" + IOS_INT_BRIEF)
            return True
        if cmd.startswith("show bgp summary") or cmd.startswith("show ip bgp summary"):
            self.write(IOS_BGP)
            return True
        if cmd.startswith("show version"):
            self.write(IOS_VERSION)
            return True
        if cmd.startswith("show logging"):
            self.write(IOS_LOG)
            return True
        self.write("                      ^\n% Invalid input detected at '^' marker.\n")
        return True


class Nokia(Persona):
    def __init__(self, *a):
        super().__init__(*a)
        self.config: str | None = None
        self.path: list[str] = []
        self.dirty = False
        self.banner = "\n SR OS Software\n Copyright (c) Demo. All rights reserved.\n\n"

    def prompt(self):
        where = "[/" + " ".join(self.path) + "]" if self.path else "[/]"
        if self.config is None:
            return f"\n{where}\nA:{self.user}@{self.name}# "
        star = "*" if self.dirty else ""
        tag = {"private": "pr", "exclusive": "ex", "global": "gl", "read-only": "ro"}[self.config]
        return f"\n{star}({tag}){where}\nA:{self.user}@{self.name}# "

    async def run(self, line):
        cmd = line.strip()
        if not cmd:
            return True
        words = cmd.split()
        if words[0] == "edit-config" and len(words) > 1:
            self.config = words[1]
            self.write(f"INFO: CLI #2051: Entering {words[1]} configuration mode\n"
                       f"INFO: CLI #2052: All changes are local to this session until committed\n" if words[1] == "private" else "")
            return True
        if self.config is not None:
            if words[0] == "quit-config":
                self.config, self.path, self.dirty = None, [], False
                self.write("INFO: CLI #2064: Exiting private configuration mode\n")
            elif words[0] == "exit":
                if words[1:] == ["all"]:
                    self.path = []
                else:
                    self.path = self.path[:-1]
            elif words[0] == "commit":
                await asyncio.sleep(0.6)
                self.dirty = False
            elif words[0] in ("configure", "router", "service", "subscriber-mgmt", "port", "interface"):
                self.path += words
            elif words[0] in ("admin-state", "description", "delete", "ipv4", "address"):
                self.dirty = True
            elif words[0] == "info":
                self.write(NOKIA_INFO)
            return True
        if cmd in ("logout", "exit all", "quit"):
            return False
        if cmd.startswith("show router bgp summary"):
            self.write(NOKIA_BGP)
            return True
        if cmd.startswith("show service active-subscribers summary"):
            self.write(NOKIA_SUBS)
            return True
        self.write("MINOR: MGMT_CORE #2201: Unknown element\n")
        return True


class Arista(Persona):
    def __init__(self, *a):
        super().__init__(*a)
        self.mode = None
        self.banner = "Last login: Sun Sep 27 08:12:40 2026 from 192.0.2.250\n"

    def prompt(self):
        return f"{self.name}#" if self.mode is None else f"{self.name}({self.mode})#"

    async def run(self, line):
        cmd = line.strip()
        if not cmd:
            return True
        if cmd in ("exit", "quit", "logout") and self.mode is None:
            return False
        if cmd in ("conf t", "configure", "configure terminal"):
            self.mode = "config"
            return True
        if self.mode is not None:
            self.mode = None if cmd in ("end", "exit") else self.mode
            return True
        if cmd.startswith("show ip bgp summary"):
            self.write(ARISTA_BGP)
            return True
        if cmd.startswith("show interfaces status"):
            self.write(ARISTA_STATUS)
            return True
        self.write("% Invalid input\n")
        return True


class Linux(Persona):
    """A Bash-like shell that reports prompts, commands and exit codes (OSC 133), the
    window title and the current folder (OSC 7), as Bash with shell integration would."""

    def __init__(self, *a):
        super().__init__(*a)
        self.cwd = f"/home/{self.user}/netops/playbooks" if "bastion" in self.name or "automation" in self.name \
            else f"/home/{self.user}"
        self.last_exit = 0
        self.banner = (f"Welcome to Ubuntu 24.04.3 LTS (GNU/Linux 6.8.0-79-generic x86_64)\n\n"
                       f" System load:  0.08              Processes:             241\n"
                       f" Usage of /:   37.2% of 96.73GB   Users logged in:       3\n"
                       f" Memory usage: 22%               IPv4 address for ens3: 192.0.2.40\n\n"
                       f"Last login: Sun Sep 27 07:58:02 2026 from 198.51.100.77\n")

    def tilde(self):
        home = f"/home/{self.user}"
        return "~" + self.cwd[len(home):] if self.cwd.startswith(home) else self.cwd

    def prompt(self):
        title = f"{ESC}]0;{self.user}@{self.name}: {self.tilde()}\a"
        cwd = f"{ESC}]7;file://{self.name}{self.cwd}{OSC_END}"
        return (f"{ESC}]133;D;{self.last_exit}{OSC_END}{title}{cwd}{ESC}]133;A{OSC_END}"
                f"{ESC}[1;32m{self.user}@{self.name}{ESC}[0m:{ESC}[1;34m{self.tilde()}{ESC}[0m$ "
                f"{ESC}]133;B{OSC_END}")

    async def run(self, line):
        cmd = line.strip()
        self.write(f"{ESC}]133;C{OSC_END}")
        if not cmd:
            return True
        self.last_exit = 0
        argv = cmd.split()
        prog = argv[0]
        if prog in ("exit", "logout"):
            return False
        if prog == "cd":
            target = argv[1] if len(argv) > 1 else f"/home/{self.user}"
            if target.startswith("~"):
                target = f"/home/{self.user}" + target[1:]
            elif not target.startswith("/"):
                target = os.path.normpath(os.path.join(self.cwd, target)).replace("\\", "/")
            self.cwd = target.rstrip("/") or "/"
            return True
        if prog == "git" and argv[1:2] == ["pull"]:
            await self.stream(GIT_PULL.splitlines(), 0.08)
            return True
        if prog == "ansible-playbook":
            check = "--check" in argv
            await self.stream(ansible_run(check).splitlines(), 0.12)
            self.last_exit = 0 if check else 2
            return True
        if prog == "ping":
            target = argv[-1]
            self.write(f"PING {target} ({target}) 56(84) bytes of data.\n")
            await asyncio.sleep(2.1)
            self.write(f"\n--- {target} ping statistics ---\n3 packets transmitted, 0 received, 100% packet loss, time 2041ms\n\n")
            self.last_exit = 1
            return True
        if prog == "ls":
            self.write(LS_PLAYBOOKS if "playbooks" in self.cwd else LS_HOME)
            return True
        if prog == "uptime":
            self.write(" 08:47:31 up 41 days,  3:12,  3 users,  load average: 0.08, 0.11, 0.09\n")
            return True
        if prog in ("df",):
            self.write(DF)
            return True
        if prog == "journalctl" or (prog == "sudo" and "journalctl" in argv):
            await self.stream(JOURNAL.splitlines(), 0.03)
            return True
        if prog == "tail":
            await self.stream(SYSLOG.splitlines(), 0.01)
            return True
        if prog == "systemctl":
            self.write(SYSTEMCTL)
            self.last_exit = 3 if "failed" in cmd else 0
            return True
        if prog == "sleep":
            await asyncio.sleep(float(argv[1]) if len(argv) > 1 else 1)
            return True
        if prog == "make":
            await self.stream(MAKE.splitlines(), 0.25)
            return True
        if prog == "echo":
            self.write(" ".join(argv[1:]) + "\n")
            return True
        if prog == "./showcase.sh":
            self.write(showcase())
            return True
        if prog == "clear":
            self.write(f"{ESC}[H{ESC}[2J")
            return True
        if prog == "printf":
            self.write(bytes(cmd[7:].strip("'\""), "utf-8").decode("unicode_escape"))
            return True
        self.write(f"{prog}: command not found\n")
        self.last_exit = 127
        return True


# --------------------------------------------------------------------------------------
# Canned output
# --------------------------------------------------------------------------------------
JUNOS_INTERFACES = """Interface               Admin Link Proto    Local                 Remote
ae0                     up    up
ae0.0                   up    up   inet     192.0.2.4/31
                                   mpls
ae1                     up    up
ae1.0                   up    up   inet     192.0.2.8/31
                                   mpls
et-0/0/0                up    up
et-0/0/0.0              up    up   aenet    --> ae0.0
et-0/0/1                up    up
et-0/0/1.0              up    up   aenet    --> ae1.0
xe-0/1/0                up    up
xe-0/1/0.0              up    up   inet     198.51.100.10/30
xe-0/1/1                up    up
xe-0/1/1.0              up    up   inet     198.51.100.14/30
xe-0/1/3                up    down
xe-0/1/3.0              up    down inet     203.0.113.22/30
xe-0/1/4                up    up
xe-0/1/4.0              up    up   inet     203.0.113.26/30
lo0                     up    up
lo0.0                   up    up   inet     192.0.2.101         --> 0/0
fxp0                    up    up
fxp0.0                  up    up   inet     192.0.2.201/24
"""

JUNOS_ROUTE_SUMMARY = """Autonomous system number: 64500
Router ID: 192.0.2.101

Highwater Mark (All time / Time averaged watermark)
    RIB unique destination routes: 1919982 at 2026-09-21 03:14:07 / 1911407
    RIB routes                   : 3842115 at 2026-09-21 03:14:07 / 3830244
    FIB routes                   : 951159 at 2026-09-21 03:14:07 / 950880
    VRF type routing instances   : 12 at 2026-08-02 22:10:41

inet.0: 1919982 destinations, 3840002 routes (1919982 active, 0 holddown, 0 hidden)
              Direct:     14 routes,     14 active
               Local:     13 routes,     13 active
                OSPF:    188 routes,    186 active
                 BGP: 3839787 routes, 951159 active
"""

JUNOS_UPTIME = """re0:
--------------------------------------------------------------------------
Current time: 2026-09-27 08:47:22 AEST
Time Source:  NTP CLOCK
System booted: 2025-12-03 04:35:19 AEST (42w3d 04:12 ago)
Protocols started: 2025-12-03 04:38:51 AEST (42w3d 04:08 ago)
Last configured: 2026-09-27 08:41:13 AEST (00:06:09 ago) by netops
 8:47AM  up 297 days,  4:12, 2 users, load averages: 0.41, 0.38, 0.36
"""

JUNOS_RE = """Routing Engine status:
  Slot 0:
    Current state                  Master
    Election priority              Master (default)
    Temperature                 41 degrees C / 105 degrees F
    CPU temperature             47 degrees C / 116 degrees F
    DRAM                      65536 MB (65536 MB installed)
    Memory utilization          31 percent
    CPU utilization:
      User                       6 percent
      Idle                      92 percent
  Slot 1:
    Current state                  Backup
    Election priority              Backup (default)
"""

JUNOS_BGP_CONFIG = """group transit-upstream-b {
    type external;
    description "Upstream B - diverse path via MEL";
    import [ peer-in-64496 reject-bogons ];
    export [ export-customer-routes ];
    peer-as 64496;
    neighbor 203.0.113.21 {
        description "upstream-b-syd-1";
        authentication-key "$9$demo-only"; ## SECRET-DATA
        bfd-liveness-detection {
            minimum-interval 300;
            multiplier 3;
        }
    }
}
"""


def _junos_log():
    lines = []
    t = dt.datetime(2026, 9, 27, 7, 40, 0)
    rnd = random.Random(7)
    events = [
        "mgd[{p}]: UI_CMDLINE_READ_LINE: User 'netops', command 'show bgp summary '",
        "sshd[{p}]: Accepted publickey for netops from 198.51.100.77 port {port} ssh2: ED25519 SHA256:p0Wd1oRfrV4n2Wq",
        "rpd[2214]: bgp_read_v4_message:12201: NOTIFICATION received from 198.51.100.9 (External AS 64511): code 6 (Cease) subcode 4 (Administratively Reset)",
        "chassisd[3011]: CHASSISD_SNMP_TRAP7: SNMP trap generated: Fan/Blower OK (jnxContentsContainerIndex 4)",
        "rpd[2214]: RPD_OSPF_NBRUP: OSPF neighbor 192.0.2.5 (realm ospf-v2 ae0.0 area 0.0.0.0) state changed from Loading to Full due to LoadDone",
        "mib2d[2981]: SNMP_TRAP_LINK_UP: ifIndex 541, ifAdminStatus up(1), ifOperStatus up(1), ifName xe-0/1/4",
        "rpd[2214]: bgp_traffic_timeout:1418: NOTIFICATION sent to 203.0.113.25 (External AS 64497): code 4 (Hold Timer Expired Error)",
        "rpd[2214]: BGP_PREFIX_THRESH_EXCEEDED: 203.0.113.25 (External AS 64497): Configured maximum prefix-limit threshold(800) exceeded for inet-unicast nlri: 861 (instance master)",
        "ntpd[1907]: NTP Server 192.0.2.123 is Reachable",
        "mgd[{p}]: UI_COMMIT: User 'netops' requested 'commit' operation (comment: CHG-4182 import policy)",
        "kernel: bpf_jit_compile: compiled 2 BPF programs",
    ]
    for i in range(240):
        t += dt.timedelta(seconds=rnd.randint(3, 25))
        ev = rnd.choice(events).format(p=rnd.randint(40000, 60000), port=rnd.randint(40000, 65000))
        lines.append(f"{t:%b %d %H:%M:%S}  {short_host}-re0 {ev}")
    tail = [
        "Sep 27 08:33:41  syd-pe-01-re0 rpd[2214]: BGP_IO_ERROR_CLOSE_SESSION: BGP peer 203.0.113.21 Hold Timer Expired",
        "Sep 27 08:33:41  syd-pe-01-re0 rpd[2214]: bgp_event: peer 203.0.113.21 (External AS 64496) state Established -> Idle (event HoldTime)",
        "Sep 27 08:34:13  syd-pe-01-re0 mib2d[2981]: SNMP_TRAP_LINK_DOWN: ifIndex 539, ifAdminStatus up(1), ifOperStatus down(2), ifName xe-0/1/3",
        "Sep 27 08:34:13  syd-pe-01-re0 rpd[2214]: RPD_BFD_SESSION_DOWN: BFD session 203.0.113.21 on xe-0/1/3.0 down",
        "Sep 27 08:41:13  syd-pe-01-re0 mgd[51902]: UI_COMMIT_COMPLETED: commit complete",
    ]
    return "\n".join(lines + tail) + "\n"


short_host = "syd-pe-01"
JUNOS_LOG = _junos_log()

IOS_INT_BRIEF = """
Interface                      IP-Address      Status          Protocol Vrf-Name
Loopback0                      192.0.2.11      Up              Up       default
MgmtEth0/RP0/CPU0/0            192.0.2.211     Up              Up       mgmt
HundredGigE0/0/0/0             192.0.2.20      Up              Up       default
HundredGigE0/0/0/1             192.0.2.22      Up              Up       default
HundredGigE0/0/0/2             192.0.2.24      Up              Up       default
HundredGigE0/0/0/3             unassigned      Shutdown        Down     default
TenGigE0/0/1/0                 198.51.100.33   Up              Up       default
TenGigE0/0/1/1                 198.51.100.37   Down            Down     default
TenGigE0/0/1/2                 unassigned      Shutdown        Down     default
Bundle-Ether10                 192.0.2.30      Up              Up       default
Bundle-Ether20                 192.0.2.34      Up              Up       default
"""

IOS_BGP = """BGP router identifier 192.0.2.11, local AS number 64500
BGP generic scan interval 60 secs
BGP table state: Active
Table ID: 0xe0000000   RD version: 4183312
BGP main routing table version 4183312

Neighbor        Spk    AS MsgRcvd MsgSent   TblVer  InQ OutQ  Up/Down  St/PfxRcd
192.0.2.101       0 64500  884119  901223  4183312    0    0    41w3d     950345
192.0.2.102       0 64500  884022  901187  4183312    0    0    41w3d     950340
198.51.100.34     0 64501   21930   22471  4183312    0    0    5d02h      18244
198.51.100.38     0 64501   21901   22460        0    0    0 00:04:18 Active
"""

IOS_VERSION = """Cisco IOS XR Software, Version 7.11.2
Copyright (c) 2013-2026 by Cisco Systems, Inc.

Build Information:
 Built By     : demo
 Built On     : Tue Jun 10 09:41:22 PDT 2026

cisco 8201-32FH (Intel(R) Xeon(R) CPU D-1530 @ 2.40GHz) processor with 32GB of memory
syd-p-01 uptime is 41 weeks, 3 days, 4 hours, 9 minutes
"""

IOS_LOG = """RP/0/RP0/CPU0:Sep 27 08:33:40.118 AEST: bgp[1087]: %ROUTING-BGP-5-ADJCHANGE : neighbor 198.51.100.38 Down - BGP Notification received, hold time expired (VRF: default) (AS: 64501)
RP/0/RP0/CPU0:Sep 27 08:33:40.120 AEST: ifmgr[263]: %PKT_INFRA-LINK-3-UPDOWN : Interface TenGigE0/0/1/1, changed state to Down
RP/0/RP0/CPU0:Sep 27 08:33:40.121 AEST: ifmgr[263]: %PKT_INFRA-LINEPROTO-5-UPDOWN : Line protocol on Interface TenGigE0/0/1/1, changed state to Down
"""

NOKIA_BGP = """===============================================================================
 BGP Router ID:192.0.2.61       AS:64500       Local AS:64500
===============================================================================
BGP Admin State         : Up          BGP Oper State              : Up
Total Peer Groups       : 2           Total Peers                 : 4
Total BGP Paths         : 2214        Total Path Memory           : 812544
===============================================================================
BGP Summary
===============================================================================
Neighbor
Description
                   AS PktRcvd InQ  Up/Down   State|Rcv/Act/Sent (Addr Family)
                      PktSent OutQ
-------------------------------------------------------------------------------
192.0.2.101
syd-pe-01
                64500  412236    0 41d03h12m 1103/1103/88 (IPv4)
                       398201    0
192.0.2.102
syd-pe-02
                64500  412198    0 41d03h12m 1103/0/88 (IPv4)
                       398177    0
-------------------------------------------------------------------------------
"""

NOKIA_SUBS = """===============================================================================
Active Subscribers Summary
===============================================================================
Total active subscribers          : 48211
  IPoE sessions                   : 41907
  PPPoE sessions                  : 6304
  IPv4 hosts                      : 48211
  IPv6 hosts (PD)                 : 39066
===============================================================================
"""

NOKIA_INFO = """    admin-state enable
    description "NNI to wholesale aggregation, SYD"
    ethernet {
        mode hybrid
        encap-type qinq
        mtu 9212
    }
"""

ARISTA_BGP = """BGP summary information for VRF default
Router identifier 192.0.2.81, local AS number 65101
Neighbor Status Codes: m - Under maintenance
  Neighbor     V AS           MsgRcvd   MsgSent  InQ OutQ  Up/Down State   PfxRcd PfxAcc
  192.0.2.140  4 65201          91224     90871    0    0   41d03h Estab   48     48
  192.0.2.142  4 65202          91220     90869    0    0   41d03h Estab   48     48
  192.0.2.144  4 65203          91198     90866    0    0   41d03h Estab   48     48
  192.0.2.146  4 65204           1203      1180    0    0 00:04:12 Connect
"""

ARISTA_STATUS = """Port       Name                 Status       Vlan     Duplex Speed  Type            Flags Encapsulation
Et1/1      hv-01 uplink a       connected    routed   full   100G   100GBASE-SR4
Et2/1      hv-02 uplink a       connected    routed   full   100G   100GBASE-SR4
Et3/1      hv-03 uplink a       notconnect   routed   full   100G   100GBASE-SR4
Et49/1     spine-01             connected    routed   full   400G   400GBASE-DR4
Et50/1     spine-02             connected    routed   full   400G   400GBASE-DR4
"""

GIT_PULL = """Updating 51b9a0d..8c1f2e7
Fast-forward
 policies/peer-in-64496.j2 | 14 ++++++++++++++
 1 file changed, 14 insertions(+)"""


def ansible_run(check: bool) -> str:
    G, Y, R, C, N = f"{ESC}[92m", f"{ESC}[93m", f"{ESC}[91m", f"{ESC}[96m", f"{ESC}[0m"
    star = lambda title: title + " " + "*" * max(3, 72 - len(title))
    out = [
        "",
        star("PLAY [Provider edge BGP policy]"),
        "",
        star("TASK [Gathering Facts]"),
        f"{G}ok: [syd-pe-01]{N}",
        f"{G}ok: [syd-pe-02]{N}",
        "",
        star("TASK [Render import policy]"),
        f"{G}ok: [syd-pe-01]{N}",
        f"{G}ok: [syd-pe-02]{N}",
        "",
        star("TASK [Commit import policy]"),
        f"{Y}changed: [syd-pe-01]{N}",
        f"{Y}changed: [syd-pe-02]{N}",
        "",
        star("TASK [Verify BGP sessions]"),
    ]
    if check:
        out += [f"{G}ok: [syd-pe-01] => (6 configured, 6 established){N}",
                f"{G}ok: [syd-pe-02] => (6 configured, 6 established){N}"]
    else:
        out += [f"{R}fatal: [syd-pe-01]: FAILED! => (6 configured, 5 established; 203.0.113.21 Active){N}",
                f"{G}ok: [syd-pe-02] => (6 configured, 6 established){N}"]
    out += [
        "",
        star("PLAY RECAP"),
        f"{(G if check else R)}syd-pe-01{N}  : {G}ok=4{N}  {Y}changed=1{N}  unreachable=0  "
        f"{(R + 'failed=1' + N) if not check else 'failed=0'}  skipped=0",
        f"{Y}syd-pe-02{N}  : {G}ok=4{N}  {Y}changed=1{N}  unreachable=0  failed=0  skipped=0",
        "",
    ]
    return "\n".join(out)


LS_PLAYBOOKS = (f"{ESC}[1;34mgroup_vars{ESC}[0m  {ESC}[1;34mhost_vars{ESC}[0m  {ESC}[1;34mpolicies{ESC}[0m  {ESC}[1;34mroles{ESC}[0m  "
                "ansible.cfg  bgp-policy.yml  inventory.yml  README.md  site.yml\n")
LS_HOME = f"{ESC}[1;34mbackups{ESC}[0m  {ESC}[1;34mnetops{ESC}[0m  {ESC}[1;34mscripts{ESC}[0m  notes.md\n"
DF = """Filesystem      Size  Used Avail Use% Mounted on
/dev/vda1        97G   36G   57G  39% /
/dev/vdb1       493G  301G  167G  65% /var/log/network
tmpfs           7.8G     0  7.8G   0% /dev/shm
"""
SYSTEMCTL = """● freeradius.service - FreeRADIUS multi-protocol policy server
     Loaded: loaded (/usr/lib/systemd/system/freeradius.service; enabled; preset: enabled)
     Active: active (running) since Fri 2026-08-14 03:12:51 AEST; 1 month 13 days ago
   Main PID: 1182 (freeradius)
     Status: "Processing requests"
      Tasks: 6 (limit: 9387)
     Memory: 412.6M (peak: 431.0M)
        CPU: 3d 4h 12min 6.410s
"""


def _journal():
    rnd = random.Random(3)
    t = dt.datetime(2026, 9, 27, 8, 30, 0)
    out = []
    for i in range(60):
        t += dt.timedelta(seconds=rnd.randint(1, 9))
        out.append(rnd.choice([
            f"{t:%b %d %H:%M:%S} syd-radius-01 freeradius[1182]: (4{rnd.randint(1000,9999)}) Login OK: [cust{rnd.randint(10000,99999)}@isp.example] (from client syd-bng-01 port {rnd.randint(1,4000)} cli 02:00:5e:{rnd.randint(16,99)}:{rnd.randint(10,99)}:{rnd.randint(10,99)})",
            f"{t:%b %d %H:%M:%S} syd-radius-01 freeradius[1182]: (4{rnd.randint(1000,9999)}) Login incorrect (Invalid user): [unknown@isp.example] (from client syd-bng-02 port 0)",
            f"{t:%b %d %H:%M:%S} syd-radius-01 freeradius[1182]: (4{rnd.randint(1000,9999)}) Accounting-Request Interim-Update for cust{rnd.randint(10000,99999)}@isp.example",
        ]))
    return "\n".join(out) + "\n"


JOURNAL = _journal()
SYSLOG = JUNOS_LOG
MAKE = """building release from 8c1f2e7
  render    inventory.yml
  lint      policies/*.j2 (41 files)
  test      bgp-policy (28 cases)
  package   netops-playbooks-2026.09.27.tar.gz
done in 14m 02s"""


PERSONAS = [(JUNOS, Junos), (IOS, IosXr), (NOKIA, Nokia), (ARISTA, Arista)]


def persona_for(host: str):
    role = role_of(host)
    for roles, cls in PERSONAS:
        if role in roles:
            return cls
    return Linux


# --------------------------------------------------------------------------------------
# SSH plumbing
# --------------------------------------------------------------------------------------
class DeviceServer(asyncssh.SSHServer):
    def begin_auth(self, username):
        return True

    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return True

    def public_key_auth_supported(self):
        return True

    def validate_public_key(self, username, key):
        return True

    def kbdint_auth_supported(self):
        return False

    def connection_made(self, conn):
        self.conn = conn

    def connection_requested(self, dest_host, dest_port, orig_host, orig_port):
        # As a jump host: a channel to another fictional host goes to that host's server.
        # The channel carries SSH bytes, so it opts out of the connection's text encoding.
        if dest_host.lower().endswith(".isp.example"):
            return self.conn.create_tcp_channel(encoding=None), HostPipe(dest_host.lower())
        return False


class HostPipe(asyncssh.SSHTCPSession):
    """A direct-tcpip channel joined to another fake host's in-process SSH server."""

    def __init__(self, host):
        self.host = host
        self.writer = None
        self.pending = []

    def connection_made(self, chan):
        self.chan = chan
        asyncio.ensure_future(self.open())

    async def open(self):
        port = await HOSTS.port_for(self.host)
        reader, self.writer = await asyncio.open_connection("127.0.0.1", port)
        for data in self.pending:
            self.writer.write(data)
        self.pending.clear()
        try:
            while data := await reader.read(65536):
                self.chan.write(data)
        except (ConnectionError, OSError):
            pass
        self.chan.close()

    def data_received(self, data, datatype):
        if self.writer:
            self.writer.write(data)
        else:
            self.pending.append(data)

    def connection_lost(self, exc):
        if self.writer:
            self.writer.close()


def make_process_handler(host):
    async def handle(process: asyncssh.SSHServerProcess):
        if process.command is not None:
            # Probes (platform, /proc cwd, tmux): answer like a box without those tools.
            process.exit(127)
            return
        persona = persona_for(host)(host, process.get_extra_info("username") or "netops", process)
        try:
            # Clears resesh's own "Connecting to … via SOCKS5 proxy" notice and scrollback, so
            # captures show the device as if reached directly.
            persona.write(f"{ESC}[H{ESC}[2J{ESC}[3J")
            if persona.banner:
                persona.write(persona.banner)
            while True:
                persona.write(persona.prompt())
                try:
                    line = await process.stdin.readline()
                except asyncssh.TerminalSizeChanged:
                    continue
                except asyncssh.BreakReceived:
                    continue
                if line == "":
                    break
                if not await persona.run(line.rstrip("\r\n")):
                    break
        except (asyncssh.Error, ConnectionError, BrokenPipeError):
            pass
        finally:
            try:
                process.exit(0)
            except Exception:
                pass

    return handle


class Hosts:
    def __init__(self, key, sftp_root):
        self.key = key
        self.sftp_root = sftp_root
        self.ports: dict[str, int] = {}
        self.lock = asyncio.Lock()

    async def port_for(self, host: str) -> int:
        async with self.lock:
            if host not in self.ports:
                root = self.sftp_root

                def sftp_factory(chan):
                    return asyncssh.SFTPServer(chan, chroot=root)

                server = await asyncssh.create_server(
                    DeviceServer, "127.0.0.1", 0,
                    server_host_keys=[self.key],
                    process_factory=make_process_handler(host),
                    sftp_factory=sftp_factory if root else None,
                    allow_scp=False,
                    keepalive_interval=15,
                    line_editor=True,
                )
                self.ports[host] = server.sockets[0].getsockname()[1]
                print(f"[rig] {host} -> {persona_for(host).__name__} on {self.ports[host]}", flush=True)
            return self.ports[host]


async def socks5(reader, writer, hosts: Hosts):
    try:
        ver, n = await reader.readexactly(2)
        await reader.readexactly(n)
        writer.write(b"\x05\x00")
        _, cmd, _, atyp = await reader.readexactly(4)
        if atyp == 3:
            length = (await reader.readexactly(1))[0]
            host = (await reader.readexactly(length)).decode()
        elif atyp == 1:
            host = ".".join(str(b) for b in await reader.readexactly(4))
        else:
            host = (await reader.readexactly(16)).hex()
        await reader.readexactly(2)
        port = await hosts.port_for(host.lower())
        up_r, up_w = await asyncio.open_connection("127.0.0.1", port)
        writer.write(b"\x05\x00\x00\x01" + bytes(4) + struct.pack(">H", 0))
        await writer.drain()

        async def pump(src, dst):
            try:
                while data := await src.read(65536):
                    dst.write(data)
                    await dst.drain()
            except (ConnectionError, OSError):
                pass
            finally:
                dst.close()

        await asyncio.gather(pump(reader, up_w), pump(up_r, writer))
    except (asyncio.IncompleteReadError, ConnectionError, OSError):
        writer.close()


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", help="folder served over SFTP as every host's file system")
    parser.add_argument("--port", type=int, default=1080)
    parser.add_argument("--down-from", type=int, default=1,
                        help="the run of `show bgp summary` on each router from which 203.0.113.21 is Active")
    args = parser.parse_args()
    NETWORK.down_from = args.down_from
    if not os.path.exists(HOST_KEY_PATH):
        asyncssh.generate_private_key("ssh-ed25519").write_private_key(HOST_KEY_PATH)
    key = asyncssh.read_private_key(HOST_KEY_PATH)
    global HOSTS
    hosts = HOSTS = Hosts(key, args.root)
    server = await asyncio.start_server(lambda r, w: socks5(r, w, hosts), "127.0.0.1", args.port)
    print(f"[rig] SOCKS5 on 127.0.0.1:{args.port}; host key {key.get_fingerprint()}", flush=True)
    print(f"[rig] public key {key.export_public_key().decode().strip()}", flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        sys.exit(0)
