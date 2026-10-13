# resesh Roadmap

What's planned next, roughly in order of value and dependency. Design details and
version-specific findings for work already shipped live in [DECISIONS.md](DECISIONS.md)
and the git history.

---

## Shipped

- **Command history** — opt-in, local, searchable record of every finished command with
  its output, folder, session, and exit result. Ctrl+Shift+H opens a search view with
  `host:`/`in:`/`exit:` filters, day-grouped results, highlighted output with match
  navigation, and insert-at-prompt. Per-session opt-out, retention in days, clear all.
  Compare runs of the same command on the same host (unified or side by side, word-level
  marks, ignore timestamps or numbers). Next: a "changed since last run" badge on command
  marks, host-to-host comparison, Cisco abbreviation matching, per-command ignore rules.

- **Command completion bell** — explicitly arm one running command from its tab subtitle
  or the command palette. Shell integration supplies the execution identity and exit result.
  Foreground completion stays quiet with a result tooltip; background completion sends a
  Windows notification that returns to the command output. Disconnect cancels the request,
  unknown exit status stays distinct from success, and later commands are never armed automatically.

- **Optional shell integration (experimental)** — off by default, enabled per local
  profile or SSH session with an explicit remote shell choice. Bundled hooks supply
  command marks, results, and folders without profile edits or network-device probes.
  New tmux shells can opt in; resumed shells are never reinjected. Coverage and limitations
  are in [SHELL_INTEGRATION_PLAN.md](SHELL_INTEGRATION_PLAN.md).
- **Foundations** — per-session settings overrides, terminal search (Ctrl+Shift+F),
  host-key mismatch override dialog.
- **Keyword highlighting** — regex rule packs (interfaces, IPs, MACs, up/down states,
  routing protocols, …) with per-session toggles and a custom-rule editor.
- **File transfer & browsing** — in-app local and SFTP file panes, "follow the terminal"
  current-directory tracking (cmd/PowerShell prompts, tmux, OSC 7, `/proc` probe, and
  prompt fallback), direct Explorer access for local folders, and SSHFS-Win access for
  remote folders when installed.
- **Session importers** — SecureCRT, PuTTY registry, and OpenSSH `~/.ssh/config`, offered
  during onboarding and from the main window.
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

## Identities (Phase A built)

Goal: describe who you log in as once, for a whole part of the session tree, instead of
repeating a username and password in every session. The main pain is repetition: a new
session dropped into a folder should need no credentials of its own.

**Today:** each session carries its own username, auth method, and Credential Manager
entry (keys are already shared through the key registry). With hundreds of imported
sessions that means typing the same username repeatedly, retyping a rotated domain or
TACACS password session by session, and no view of which sessions use which account.

**Identity** = a name, a username, and one way to prove it: a stored password (one
Credential Manager entry, `Resesh:identity:<id>`, read like key passphrases), a registered
key, ask every time, and later the SSH agent (see Connectivity; also the path to
password-manager agents in FUTURE.md) and GSSAPI. Stored in `identities.json`.

**Model:** `Session.Login` = `Own | Inherit | Identity(id)`. A missing value means `Own`,
so every existing session keeps its own username, auth method, and secret with zero
migration; new sessions default to `Inherit`. A session may also override only the
username while keeping the inherited proof (cloud images: `ubuntu` vs `ec2-user` on one
key; per-device local accounts). Folder settings live in `sessions.json` as
`folderSettings: [{ path, identityId }]`, keyed by normalized path and reparented by the
same `SessionStore` rename and move code that reparents sessions (test-pinned). Deleting
a folder drops its settings.

**Resolution:** an ordered list of sources, evaluated at the one connect-time choke point
(`ResolveSshCredentialAsync`; the SFTP pane reuses its result):
1. The session's own credentials or explicit identity.
2. The nearest folder (self, then ancestors) with an identity.
3. A prompt for username and password with *"Use for every session in Branch/"*, which
   creates the identity and attaches it to that folder.

Folders only, no tags. NetBox lens attributes (role = firewall → TACACS identity) plug in
later as another source in the list.

**Behaviour:**
- Moving a session into a folder changes its login immediately; the drop shows a
  one-line note when it does.
- A failed stored-password login offers "Update the password for *identity* (used by N
  sessions)"; one update fixes every session using it.
- Ask-every-time identities keep a typed password in memory, per identity, until the app
  closes; never on disk.
- The session editor has a Login section showing the effective identity and where it
  comes from ("Inherited from Branch/ — tacacs-ops (password)"), with "This session
  only" revealing today's fields. Folders get "Set identity…". Settings gains an
  Identities tab listing each identity with its usage and a "Show sessions" link.
- Telnet is out of scope: its login happens in the terminal. Auto-login there is a
  separate feature.

**Migration:** nothing changes until the user opts in. A tidy-up assistant proposes
groupings (same username and same saved password, compared locally only) and folder
assignments. Importers suggest identities from repeated usernames.

**Related:** jump hosts stay a separate piece of work (an identity is who you are; a jump
host is how you reach the machine), but each hop resolves its login through the same
resolver. Backups export identities without secrets unless the encrypted option is used.

**Phases:**
- A (built): store, resolver and connect path, first-connect "use for every session in
  this folder" prompt, username override, editor Login section, folder "Set Identity…",
  Settings › Identities & Keys (replaces the SSH Keys dialog; includes the default
  identity for the tree root), failed-password update, in-memory cache for
  ask-every-time, drop note when a move changes a login, backup.
- B: tidy-up assistant for existing sessions. (Importer suggestions shipped with A: the import
  review is a tab that groups repeated logins into identities, attached where they dominate;
  key logins and directory accounts are pre-ticked, plain repeated usernames are offered
  unticked because their passwords usually differ per host.)
- Later: Agent (after SSH agent support), GSSAPI, lens attributes.

**Accepted cost:** one stored secret now visibly unlocks many sessions (it usually did
already).

---

## Inventory sources: NetBox (design)

Goal: a team shares one up-to-date session tree because it comes from the NetBox they
already maintain, not from everyone's hand-kept sessions.json. Read-only and one-way:
NetBox says where and how to connect, never as whom.

**Why it isn't a tag-mapping nightmare:** NetBox's core device fields are fixed (site,
location, rack, region, role, device type and manufacturer, platform, tenant, status,
primary/OOB IP), so grouping and filtering work on almost any install with no setup.
Installs vary mainly in which address to connect to, the protocol and port, the jump
host, and which devices count. Those are mapping rules, not data modelling.

**Fit with the current tree:**
- The virtual Local root already shows the pattern: a separate folder namespace
  (`FolderScope`, the `ExpansionKey` prefix), plus a root that can't be renamed, moved or
  deleted. A NetBox source becomes another such root ("NetBox: corp"). Generalize the
  Local-only flags into a scope or source id instead of adding a second set of special cases.
- Devices become real `Session` records with a **deterministic id** (UUIDv5 of source id
  + object type + NetBox id). Everything already keys on `Session.Id` through
  `Store.Find`: tabs, workspaces, recents, the jump list, history names and Credential
  Manager entries. That keeps all of it working across resyncs and renames.
- They live in a per-source cache file (`inventory-<source>.json`), not sessions.json, and
  are replaced as a whole on sync. `Store.Find` and `VisibleSessions` consult both through
  a small catalog in front of `SessionStore` (about ten call sites).
- Each record carries an `Inventory` block (source, object id, site, region, location,
  role, platform, manufacturer, device type, tenant, tags, status), and `SessionSearch`
  matches on those fields too.

**Tree lenses (tabs above the tree):** a lens is an ordered list of attributes. The
inventory root's `FolderPath` is computed from them (`site/role`, `manufacturer/device type`,
…), and the existing path-based `SessionTreeBuilder` builds the tree unchanged. Presets:
Location (Region › Site › Location), Role, Platform, Device type, Tenant, Tag. Users can
define their own. Expansion keys include the lens. Your own sessions stay folder-based.

**Selecting devices:** paste a NetBox filter URL (`/dcim/devices/?status=active&role=core`).
The UI and REST API share query parameters, so we need no filter builder. Virtual machines
come from `/virtualization/virtual-machines/` the same way. Multiple filters per source are
allowed.

**Connection mapping, in precedence order:**
1. NetBox **config context** under a `resesh` key, which NetBox itself merges by region,
   site, role, platform and tag. Example: `{"resesh": {"protocol": "telnet", "port": 23,
   "jump": "bastion-syd", "host": "oob"}}`. Admins set it once and every teammate gets it.
2. Local source rules: host = `primary_ip` | `oob_ip` | a custom field | a template
   (`{name}.mgmt.corp`), plus a platform-to-protocol map.
3. Defaults: primary IP, device name, SSH on 22, active devices with a primary IP only.

**Per-user layer:** NetBox entries are read-only. A local overlay file keyed by the
deterministic id holds username, auth, icon, colour, notes and terminal overrides. Icons
are auto-suggested from manufacturer or platform through `SessionIconCatalog` (Cisco,
Juniper, Arista, …). The menu offers Connect, Open in NetBox and "Copy to my sessions";
Edit, Rename and Delete are off. `SessionDropPlanner` rejects inventory nodes as move
sources or targets.

**Sync:**
- Per-user read-only API token, stored in Credential Manager.
- Sync runs on start and on a timer, fetching only devices changed since the last sync
  (`last_updated__gte`), with a periodic full fetch to catch deletions.
  `brief`/`fields` or GraphQL keep the payload small.
- The cache serves offline use. A status line shows the last sync time and any error.
- Devices gone from NetBox drop out of the tree. Open tabs and saved workspace entries for
  them keep working until closed, as a deleted session does today.

**Console ports (later):** NetBox models the cable from a device's console port to a
console server port. Offer "Console" on a device, opening telnet or SSH to the console
server's mapped port (`200x`-style). This builds directly on shipped telnet support.

**Depends on / pairs with:**
- **Jump hosts:** config-context `jump` needs them. Until then, ignore it with a visible
  note.
- **Identities:** this answers the "folder or tag" question. Identities should attach to
  lens attributes too (role = firewall → TACACS identity), since inventory devices
  have no hand-made folders.
- **Backup:** export includes source settings and overlays, but never the token or the
  device cache.

**Build order:** (1) one source, one filter, primary IP + SSH, read-only root with a
Site lens; (2) lens tabs plus custom lenses; (3) overlays and icon suggestion; (4) config
context mapping; (5) console ports. Keep the source behind an `IInventorySource`
interface. Nautobot's API is close to NetBox's, and Ansible inventory or CSV could follow.

**Open questions:**
- Lens tabs over the whole tree, or only on inventory roots?
- Should a lens be able to mix in your own sessions (by matching host or name to a device)?
- Minimum NetBox version (4.x `fields=`, v2 tokens) vs. supporting 3.x installs still common
  in the field.
- Should "Copy to my sessions" stay linked, following host changes, or be a plain fork?

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

### Telnet — shipped
Built as `SessionKind.Telnet` on the existing Host/Port fields (no separate `TelnetTarget`),
sharing the SSH folder tree; `Core/Telnet` holds the I/O-free `TelnetProtocol` and the socket
backend. Verified against telehack.com and JPL Horizons (NAWS, TTYPE, remote echo, live resize).
Send Break (Session menu, tab menu, palette, Ctrl+Break; `IBreakSender` + `SendBreak` capability,
ready for serial) and PuTTY/SecureCRT telnet import (port chosen by the session's protocol key) shipped.

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

## Program status (OSC 7501) and the alerts list

Programs report their own state through the terminal output: `idle`, `working`,
`blocked` (`kind=permission|question|auth`), `done` or `error`, with an optional app name,
message, progress and child records
([spec](https://gist.github.com/mitchellh/7acae3abd8355c1c00287d67e96c913a), parsed by
libghostty-vt since ghostty-org/ghostty#14560). Claude Code 2.1.295, Pi 1.1.0, Crush and
Factory Droid already send it, but **only after the terminal answers the `OSC 7501 ; ?`
probe** (sent together with DA1; a DA1 reply that arrives first means "unsupported" for the
whole session). libghostty answers the probe only while the program-status callback is set.

**Where we are (2026-10-13, shim ABI 7):** a first cut is in. The shim sets
`GHOSTTY_TERMINAL_OPT_PROGRAM_STATUS`, so the probe is answered, and forwards each report
on the existing OSC event. `AgentTracker.ObserveProgramStatus` shows the root record: a
known agent (`app` matched against the agent command names; a report naming no app updates
the agent already showing) gets its identity and state, and any other program gets the
badge alone, with no icon, for `blocked`, `done` and `error`. A program's `done`/`error`
survives the prompt until the tab is viewed or typed into; an agent's still goes when the
agent does. Still open from the steps below: the per-id record store and the
alerts list. A root report's percentage drives the tab progress bar (filled while working,
paused while blocked), and the adapter settings name the agents that need no hooks.

**Why it fits us better than most:** the report travels in the terminal output, so it
works over SSH with nothing installed on the server, and it isn't agent-only: `brew`,
`terraform`, deploy scripts and progress libraries (tqdm, indicatif) are picking it up.
The states map almost one to one onto `AgentAttention`:

| 7501 | `AgentAttention` |
|---|---|
| `working` | `Working` (+ progress into the tab's existing progress combiner) |
| `blocked kind=permission` | `NeedsApproval` |
| `blocked kind=question` / `auth` / none | `NeedsAnswer` (`auth` labelled "needs sign-in") |
| `done` | `Complete` |
| `error` | `Failed` |
| `idle` | `Idle` |
| `clear` | record removed; tracker falls back to the other evidence sources |

### Steps
1. **Shim (next reseshvt ABI bump):** register the program-status callback and emit a new
   `RVT_EVENT_PROGRAM_STATUS` carrying state, kind, progress, id, app, title and message
   (copied; libghostty's strings live only for the callback). Confirm the probe reply
   leaves through `write_pty` ahead of the DA1 reply on both the SSH and ConPTY paths.
2. **Core `ProgramStatusStore` (per tab, pure, unit-tested):** one record per id, each
   report replaces its record, `clear` removes a subtree (no id clears all), `app` is
   inherited from the nearest ancestor, 256 records with least-recently-updated eviction.
   Lifetime: OSC 133 A and process/channel exit drop `working`/`blocked`; RIS drops
   everything; `done`/`error` survive until the tab is viewed (`ClearsOnView`).
3. **Tracker:** the root record feeds `AgentTracker` as `AgentSource.Structured` and
   outranks 7377, titles and process detection while it is `working` or `blocked`.
   Children are stored but not shown in the first cut (everyone else did the same).
   Non-agent apps get the attention badge without claiming an agent identity; `app`
   values for known agents (`claude-code`, `codex`, `pi`, …) map to `AgentIdentities` keys.
4. **Display:** tooltip `app · message`, run through `AgentText.Sanitize` (the spec
   requires plain text with invisible formatting removed).
5. **Adapters:** the adapter dialog says Claude Code ≥ 2.1.295 needs no hooks; keep the
   7377 snippets for agents that don't send 7501 yet. 7377 stays as our own protocol.

### tmux-persistent sessions (tested 2026-10-13, tmux 3.4)
Confirmed on a persistent session: tmux answers DA1 itself and never forwards the
`OSC 7501 ; ?` probe, so a program gets no reply, and a plain report is dropped. The hook
adapters are no fallback there either: plain OSC 7377 and OSC 777 are dropped the same way.
With `allow-passthrough on`, a report or probe wrapped in `DCS tmux;` does arrive, and the
probe reply comes back to the pane, but no program wraps on its own. So persistent
sessions cannot see native 7501 until tmux passes it through itself (declined upstream for
now, tmux issue 5713).

The adapters get around it without passthrough (`TmuxStatus`): inside tmux a hook stores a
7501 report body in the pane option `@resesh_status`, our `set-titles-string` appends the
option to the title unless a shell is in the foreground, and the tab view takes it off the
title and feeds it to the same `ProgramStatus` path. tmux pushes the title within ~10 ms of
the option changing. Needs tmux 3.0 for pane options. Verified on rct with the generated
Codex and Claude Code hooks and the shell wrapper. Known gap: a program that dies without
unsetting the option leaves it for the next non-shell program in that pane. Any script can
use the option, so agents with extension systems (Pi, oh-my-pi) could get adapters too.

This bridge stays for hosts on tmux. The longer fix is to take tmux out of the stream:
see [Remote session agent](#remote-session-agent-spiked-2026-10-13).

### Alerts list (promoted from Deferred)
Once 7501 gives real states, one place to see every tab that wants you: a flyout from the
title bar plus a command-palette entry and shortcut, listing tabs that are `blocked`,
`error` or unseen `done` (in that order) with app, message and age; click jumps to the tab.
Taskbar overlay count of blocked tabs. Background `blocked`/`error`/`done` can raise a
Windows notification through the plumbing command completion already uses
(`CommandCompletionNotifications`), behind a setting and rate-limited.

**Acceptance:** with no hooks configured, Claude Code 2.1.295+ in a local tab and in a
plain SSH tab goes working → needs approval → finished; a `printf` script covers
`brew`-style reports, invalid base64 (ignored), RIS (clears), and a new prompt (drops
`working`); the alerts list jumps to a blocked background tab.

---

## Remote session agent (spiked 2026-10-13)

**Problem.** tmux keeps a session alive, but it is a second terminal emulator in the
stream. It redraws program output, drops the sequences it does not know, and answers
terminal queries itself. Every new sequence we care about needs another bridge like
`TmuxStatus`. We use very little of tmux: one session, one window, one pane, no status
line, no prefix key.

**Direction.** Put our own small binary on the host in tmux's place. One daemon per
session holds the pty and forwards bytes untouched in both directions. A headless
libghostty-vt beside the stream is used only to build a snapshot when a client attaches.
shpool, zmx and VS Code's remote terminals all arrived at this design.

**Spike.** Stock [zmx](https://github.com/neurosnap/zmx) 0.8.1 (MIT, Zig, libghostty-vt)
against our tmux setup on Ubuntu 24.04. Scripts, raw output and the full tables are in
[tools/session-probe](tools/session-probe/RESULTS.md).

| | tmux | zmx |
|---|---|---|
| 21 output sequences unchanged | 0 | 20 |
| 11 terminal queries answered by the terminal | 0 | 11 |
| Kitty keys, focus, mouse, paste input | Ctrl+A and Shift+Enter degraded, focus lost | identical |
| `seq 1 100000`, lines reaching the terminal live | 6,745 | all |
| Reattach restores scrollback and output made while detached | yes | yes |
| Reattach restores alternate screen and kitty keyboard mode | no | yes |
| Reattach restores OSC 8 links | yes | no |
| Reattach restores OSC 7501 status and OSC 133 marks | no | no |

In the app, a plain OSC 7501 from inside a zmx session lit the tab icon and badge with no
title bridge. After a reattach the badge was gone, as the last row predicts.

### Decision
Fork zmx and trim it, rather than write from nothing. It passed every passthrough test
unmodified, and it already handles the pty and daemon cases that cost the most to find:
answering DA1/DA2 while detached (fish waits about 10 seconds otherwise), one client owning
the pty size, and the snapshot traps (tabstops, synchronized output, the title). It bundles
the same libghostty-vt version we pin, and we already build with Zig.

tmux stays as a fallback and is frozen: no new bridges. The ladder per host is agent, then
tmux, then a plain session.

### Steps
1. **Fork and build.** Fork at v0.8.1, keep the licence notice. Build static musl binaries
   for x86_64 and aarch64 Linux, stripped, from `eng/` next to `build-ghostty-vt.ps1`. Cut
   the CLI down to what the app calls. Measure the size; the stock release is 15.7 MB with
   debug info.
2. **Snapshot.** Turn on hyperlinks in the formatter options, as `rvt_format_vt` does. Find
   out whether prompt marks can be kept, and stop rewriting `OSC 133;A` if they can.
3. **State the snapshot cannot hold.** The daemon remembers the last OSC 7501 report and
   sends it after the snapshot. Decide the same way for progress (OSC 9;4).
4. **Protocol.** Add a version exchange as the first message. Keep the tags frozen and
   unknown tags ignored. Sessions outlive app upgrades, so an old daemon must keep working
   with a new client; this is why it is one daemon per session and not one per host.
5. **Side channel.** List sessions, and per session the working directory, foreground
   command and client count, as structured replies. This replaces the `list-panes` parsing
   in `TmuxPersistence`.
6. **Getting it onto the host.** Upload over the SSH connection we already have, to a
   versioned path under `~/.local/share/resesh/`, and check the hash. Pick the binary from
   `uname`. Ask once per host: always, never, or ask each time. No download from the host
   side.
7. **App.** A second persistence backend beside `TmuxPersistence`, with the choice stored
   per host. `TmuxStatus` stays in use for tmux sessions only.
8. **Done when** `tools/session-probe` passes on the fork (21 of 21 sequences, 11 of 11
   queries, identical input) and a reattach brings back links, prompt marks and status,
   and the tab badge survives a dropped connection in the app.

### Open questions
- Memory per session with a 50,000-line scrollback, since each session is its own process
  with its own emulator.
- Two clients of different sizes on one session. zmx gives the size to one "leader".
- Hosts where the home directory is mounted `noexec`, and hosts that are not Linux. macOS
  is a build target for zmx; the BSDs are not.
- Whether a session started by an old agent version can be handed to a new one, or only
  kept alive until it ends.

### Bug to fix now, separate from the above
On tmux 3.4, `capture-pane -t =name` fails with `can't find pane`; `=name:` works.
`TmuxPersistence` uses the failing form in both `BootstrapCommand` and `ResumeCommand`, so
the reattach replay prints an error and only the visible screen comes back. Found with the
command on the host; not yet confirmed through the app.

---

## Deferred small items

- Title-bar session icon (needs SVG→ICO rasterization).
- Agent tabs: adapters beyond Codex / Claude Code / the generic shell wrapper, richer
  ACP-style status. (Toasts now exist via command completion; the alerts list and taskbar
  count moved to Program status above.)
- Ruler: next/prev **error** jump (failed-exit marks only).
- Live verification loose end: a real 2FA/Duo keyboard-interactive exchange is untested.
  Password and private-key auth are in daily use against real hosts.

---

## Backlog — unordered candidates

**Non-goal — scripting/automation API:** resesh is the interactive layer; fleet
automation belongs in Ansible/Netmiko-class tooling run from a trusted host, where it's
versioned and reviewed. No embedded scripting language, ever. Command snippets stay in
scope — interactive-shaped, not automation-shaped. **Broadcast input is cut too**: it's
the classic multi-device-typo footgun on heterogeneous fleets, and anything worth sending
to N devices at once is worth a playbook. Power users on tmux-persistent sessions already
have `synchronize-panes` if they truly want it.

- **Command snippets** — saved commands with placeholders, per-folder scoping, send on
  click; optional "startup command" per session.
- **More importers** — mRemoteNG. The shipped OpenSSH config parser can also supply
  GSSAPI/jump-host defaults.
- **Auto-reconnect UX** — backoff + toast on drop, auto-reattach when the session is
  tmux-persistent.
- **Per-session color schemes** — extend the settings-override layer + the palettes in
  `terminal.html`; `Session.ColorTag` already exists as the tab accent.
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
| 3 | Program status (OSC 7501) → alerts list | S–M | Low |
| 4 | Telnet & serial consoles (telnet → serial) | S–M | Low |
| 5 | GSSAPI productization (path chosen by spike) | M | Depends on spike |
| 6 | NetBox inventory source (read-only root → lenses → config context → console ports) | M–L | Medium |
| 7 | Remote session agent (zmx fork → snapshot and status → upload → app backend) | L | Medium |
| 8 | Backlog picks | — | — |

Within connectivity, tunnels come first (pure SSH.NET), then agent auth, then jump hosts
(which build on the tunnel channel code). Telnet/serial rides directly behind connectivity:
it reuses the backend contract as-is, telnet before serial (no new dependency vs. port
enumeration + device-arrival plumbing), and together they close the console-cable objection
cheaply. GSSAPI ships last only because its spike must settle the approach first.
NetBox's first steps (read-only root, lenses, overlays) don't depend on anything above.
Config-context jump hosts wait for connectivity, and attribute-based identities wait for
Identities.
