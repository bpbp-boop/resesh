# Resesh Roadmap

What's planned next, roughly in order of value and dependency. Design details and
version-specific findings for work already shipped live in [DECISIONS.md](DECISIONS.md)
and the git history.

---

## Shipped

- **Foundations** — per-session settings overrides, terminal search (Ctrl+Shift+F),
  host-key mismatch override dialog.
- **Keyword highlighting** — regex rule packs (interfaces, IPs, MACs, up/down states,
  routing protocols, …) with per-session toggles and a custom-rule editor.
- **File transfer & browsing** — in-app local and SFTP file panes, "follow the terminal"
  current-directory tracking (cmd/PowerShell prompts, tmux, OSC 7, `/proc` probe, and
  prompt fallback), direct Explorer access for local folders, and SSHFS-Win access for
  remote folders when installed.
- **Export / import & backup** — `*.reseshbackup` archives with merge-on-import,
  optional passphrase-encrypted secrets, and a versioned schema.
- **SSH key registry** — private keys as shared named resources with fingerprint
  pinning, path repair, and Credential Manager passphrase storage; explicit
  keyboard-interactive prompts.
- **Session identity & local terminals** — per-session icon packs (Linux distros,
  network OS vendors, custom drop-in icons), first-class local shell profiles on
  ConPTY (PowerShell, cmd, WSL, Git Bash), and agent-aware tabs (Claude Code, Codex,
  Gemini CLI, … with attention badges).
- **Annotated scrollbar** — a custom overview ruler mapping the whole scrollback:
  search matches, highlight-rule hits, bookmarks, command marks (OSC 133, OSC 3008,
  Enter-gated prompt discovery) with exit-status colors, per-line timestamps, and a
  commands panel with jump / copy-output actions.
- **Recording & rewind** — bounded in-memory rewind with xterm state keyframes, paired
  asciicast v2 and timestamped plain logs rendered from terminal output, plus `.cast` playback with
  pause, speed, and seek controls.
- **Workspaces & layout restore** — named ordered tab-group layouts with pinned and active
  tab state, replace or additive opening that adopts live connections, clean-exit startup
  restore, and conflict-aware workspace remapping through backup import.

---

## GSSAPI / Kerberos auth (spike first)

Goal: `AuthMethod.Gssapi` — log in with the Windows AD ticket, no password or key stored.

**The problem:** SSH.NET has no `gssapi-with-mic` (RFC 4462) support, and
`AuthenticationMethod.Authenticate(Session)` is `internal`, so we can't subclass from
outside the assembly.

**Spike (timeboxed, do early — the only item with real feasibility risk):** prove a
`GssapiWithMicAuthenticationMethod` built on .NET 8's `NegotiateAuthentication` (SSPI —
Kerberos context tokens from the logged-in session, `ComputeIntegrityCheck` for the MIC,
SPN `host/<fqdn>`), delivered as one of:
1. **Vendored/forked SSH.NET** with the new method (we already accept SSH.NET fragility —
   see `ShellStreamResizer` reflection + pinning tests). Most likely path.
2. Upstream PR to SSH.NET (long-standing open issue; slow, but do it alongside 1).
3. Fallback if 1 fails: spawn Win32-OpenSSH `ssh.exe` as a backend for GSSAPI sessions
   only (it supports SSPI Kerberos) and pipe its PTY into xterm.js. Ugly; last resort.

**Product work after the spike:** new auth option in session editor (with "delegate
credentials" checkbox for `gssapi-keyex`-style hops later), clear failure messaging
(no ticket / clock skew / SPN mismatch), fall back to password prompt on failure.
Needs testing against a real AD-joined host — the local `TestSshServer` can't exercise this.

---

## Connectivity: tunnels, jump hosts, SSH agent

### Port forwarding
SSH.NET has `ForwardedPortLocal/Remote/Dynamic` built in. Per-session tunnel list in the
session editor (type, bind address/port, destination), started with the connection, with
live status/error indicators in the tab and a quick "add tunnel" action on a running
session. Dynamic (SOCKS) covers the browser-proxy use case.

### Jump hosts (ProxyJump)
Chain through one or more intermediate sessions: a `JumpHost` reference (another session id)
on the model, connected via SSH.NET's port-forwarding channel (connect to jump, open a
direct-tcpip channel, run the target handshake over it). Each hop uses its own auth —
pairs naturally with GSSAPI credential delegation. UI: dropdown in the session
editor picking an existing session as the jump; chains resolve recursively with a cycle
check.

### SSH agent support
New auth option `Agent`: talk to the Windows OpenSSH agent named pipe
(`\\.\pipe\openssh-ssh-agent`, SSH agent protocol) and Pageant's shared-memory protocol.
List agent keys in the session editor, try-all or pin a specific key. Removes the need to
store key passphrases in Credential Manager.

---

## Telnet & serial consoles

Console-cable and terminal-server access — the last standing objection from the SecureCRT
crowd. Cheap now because the local-terminal work already did the hard part:
`ITerminalBackend` + capability flags mean telnet and serial are just two more backend
implementations feeding the same xterm byte path, with remote-only UI (SFTP, tmux, host
keys, agent) hidden by capability flags exactly as local shells do. Everything page-side —
highlighting, the overview ruler, Enter-gated command discovery, recording — works unchanged.

New target kinds alongside `SshTarget`/`LocalTarget` (same tagged-profile model, same
atomic store, no migration impact): `TelnetTarget` and `SerialTarget`.

### Telnet
- **Target:** host, port (default 23). Quick connect accepts `telnet host[:port]` — terminal
  servers map console lines to high ports (`200x`-style), so port entry must be frictionless.
- **Backend:** raw TCP plus minimal IAC option negotiation — BINARY, SGA, ECHO (remote echo
  on, we never echo locally), TERMINAL-TYPE (`xterm-256color`), and NAWS wired to terminal
  resize. Politely refuse (WONT/DONT) everything else. A few hundred lines; no library needed.
- **Capabilities:** no host keys, no SFTP/SSHFS, no tmux, no auth storage. Disconnect/
  Reconnect verbs as SSH.
- **UI note in the editor:** plaintext protocol — position it for console servers and lab/
  management networks, not general remote access.

### Serial
- **Target:** COM port, baud (default 9600), data bits, parity, stop bits, flow control
  (none / XON-XOFF / RTS-CTS).
- **Transport:** `System.IO.Ports.SerialPort` feeding the existing byte path. No resize
  semantics, no remote cwd — capability flags hide all of it.
- **Port picker:** enumerate with friendly names ("USB Serial Device (COM5)" via SetupAPI/
  WMI, not bare `COM5`), and refresh live on device arrival/removal (USB console cables
  come and go constantly).
- **Absent-port semantics:** a saved session whose COM port is missing shows as unavailable,
  not an error; cable yank mid-session moves the tab to a disconnected state with the reason,
  keeps scrollback, and Reconnect retries — auto-offer when the port reappears.
- **Send Break** (tab menu / Ctrl+Break): required for password recovery and boot interrupts
  on common network gear.

**Acceptance:** telnet to a real network device or terminal server with NAWS-driven resize;
serial to a switch console via USB adapter including unplug/replug survival and Break;
highlighting + command discovery ticks working on both; both target kinds round-trip the
export archive.

---

## Deferred small items

- Title-bar session icon (needs SVG→ICO rasterization).
- Agent tabs: Windows toast notifications (needs package identity), taskbar count, an
  alerts list to jump between waiting tabs, adapters beyond Codex / Claude Code / the
  generic shell wrapper, richer ACP-style status.
- Ruler: next/prev **error** jump (failed-exit marks only).
- Live verification loose ends: private-key auth has never been tested against a real
  host; a real 2FA/Duo keyboard-interactive exchange likewise.

---

## Backlog — unordered candidates

**Non-goal — scripting/automation API:** Resesh is the interactive layer; fleet
automation belongs in Ansible/Netmiko-class tooling run from a trusted host, where it's
versioned and reviewed. No embedded scripting language, ever. Command snippets stay in
scope — interactive-shaped, not automation-shaped. **Broadcast input is cut too**: it's
the classic multi-device-typo footgun on heterogeneous fleets, and anything worth sending
to N devices at once is worth a playbook. Power users on tmux-persistent sessions already
have `synchronize-panes` if they truly want it.

- **Command snippets** — saved commands with placeholders, per-folder scoping, send on
  click; optional "startup command" per session.
- **More importers** — PuTTY registry, mRemoteNG, OpenSSH `~/.ssh/config` (the config
  parser also helps GSSAPI/jump-host defaults).
- **Auto-reconnect UX** — backoff + toast on drop, auto-reattach when the session is
  tmux-persistent.
- **Per-session color schemes** — extend the settings-override layer + the palettes in
  `terminal.html`; `Session.ColorTag` already exists as the tab accent.
- **Manual shell-integration snippet** — optionally show and copy OSC 133/OSC 7 setup
  for users who choose to install it. Resesh must never send setup code automatically.
- **Foreground-process title side channel** — tmux-grade `pane_current_command` on plain
  sessions with zero shell cooperation: each tab owns its SSH connection, so an exec
  channel can locate the shell channel's process ($PPID → our sshd child → its child
  holding a tty), then watch `/proc/<shell>/stat` tpgid → `/proc/<tpgid>/comm`. Sees
  nested children (make → cc) and survives fancy prompts; Linux/procfs only, adds polling,
  and assumes one shell per connection. Back pocket unless discovery + stock context data
  prove insufficient.

---

## Suggested order

| # | Item | Size | Risk |
|---|------|------|------|
| 1 | **GSSAPI spike** (parallel with anything) | S | **High — do early** |
| 2 | Connectivity (tunnels → agent → jump hosts) | M–L | Medium |
| 3 | Telnet & serial consoles (telnet → serial) | S–M | Low |
| 4 | GSSAPI productization (path chosen by spike) | M | Depends on spike |
| 5 | Backlog picks | — | — |

Within connectivity, tunnels come first (pure SSH.NET), then agent auth, then jump hosts
(which build on the tunnel channel code). Telnet/serial rides directly behind connectivity:
it reuses the backend contract as-is, telnet before serial (no new dependency vs. port
enumeration + device-arrival plumbing), and together they close the console-cable objection
cheaply. GSSAPI ships last only because its spike must settle the approach first.
