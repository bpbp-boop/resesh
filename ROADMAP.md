# Sessions — v2 Roadmap

v1 (M1–M5, plus tmux persistence) is complete — see `ssh-client-v1-plan.md` and `DECISIONS.md`.
This roadmap covers the next tranche. Phases are ordered by dependency and value; the GSSAPI
spike runs early because it is the only item with real feasibility risk.

---

## Phase 0 — Foundations (prerequisites, mostly small) ✅ shipped 2026-08-14

### 0.1 Per-session settings overrides
`AppSettings` is global-only today; per-session highlight toggles (Phase 1) need an override
layer. Add an optional `SessionOverrides` object on `Session` (nullable fields = inherit
global): theme, font, scrollback, and later highlight-rule state. Plumb through
`TerminalTabView.ApplySettings` → `TerminalControl.ApplyOptions`.

### 0.2 Terminal search (Ctrl+Shift+F)
Bundle the xterm.js **search addon** (we already vendor fit + web-links in
`src/Terminal/wwwroot`). Find bar overlay in the tab, next/prev, regex toggle. Cheap, high
value, and exercises the same decoration machinery highlighting will use.

### 0.3 Host-key mismatch override
Today a changed host key hard-fails with no recourse (`SshTerminalSession.cs:104-109`).
Add a scary-but-usable dialog: show old/new fingerprints, require typed confirmation,
update `known_hosts.json` on accept.

---

## Phase 1 — Keyword highlighting ✅ shipped 2026-08-15

Regex-rule highlighting in the terminal, modeled on netOS-cli's SecureCRT keyword packs
(<https://github.com/h-lopez/netOS-cli>) but first-class: built-in packs, per-rule toggles,
user-defined rules, all controllable **per session**.

**Rule model** (`highlights.json` in `%APPDATA%\Sessions\`):
```json
{ "id": "state-negative", "name": "Down/error states", "pack": "builtin:network",
  "pattern": "\\b(down|disabled|shutdown|failed|error|denied)\\b",
  "color": "#ff5555", "bold": true, "enabled": true }
```

**Built-in packs** (ported/adapted from netOS-cli, which is regex-per-line + color):
- Interfaces (`Gi0/0/1`-style, Linux `eth0`/`ens…`/`bond…`, loopbacks)
- IPv4 (+CIDR), IPv6, MAC (colon / Cisco dotted / hyphenated)
- Positive states (`up|enabled|connected|success|ok|valid|true`)
- Negative states (`down|disabled|shutdown|failed|error|denied`)
- Routing protocols (`bgp|ospf|eigrp|rip|isis|…`), services, durations (`1w2d3h`)
- Quoted strings, bare numbers (both off by default — noisy)

**Toggle model:** global defaults per rule; per-session overrides stored via Phase 0.1
(`enabledRules` / `disabledRules` deltas, not copies). Quick toggle UI in the tab (a
highlighter dropdown listing rules with checkboxes) + full editor in Settings for custom
rules (name, regex with live preview, color, bold/underline).

**Implementation:** custom xterm.js addon using the **decorations API** — scan viewport
rows on render/scroll, never the raw stream (the 16 ms/32 KB batch path in
`TerminalControl.cs` stays untouched; also keeps us clear of the tmux `indn@` scrollback
gotcha in `TmuxPersistence.cs:38-43`). Regexes compile once per rule; cap matches per row
so a pathological rule can't hitch rendering.

---

## Phase 2 — GSSAPI / Kerberos auth (spike first)

Goal: `AuthMethod.Gssapi` — log in with the Windows AD ticket, no password or key stored.

**The problem:** SSH.NET has no `gssapi-with-mic` (RFC 4462) support, and
`AuthenticationMethod.Authenticate(Session)` is `internal`, so we can't subclass from
outside the assembly.

**Spike (timeboxed, do early):** prove a `GssapiWithMicAuthenticationMethod` built on
.NET 8's `NegotiateAuthentication` (SSPI — Kerberos context tokens from the logged-in
session, `ComputeIntegrityCheck` for the MIC, SPN `host/<fqdn>`), delivered as one of:
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

## Phase 3 — File transfer & remote browsing

SSH.NET already ships `SftpClient`; there is zero transfer code today, so this is greenfield.

### 3.1 In-app SFTP pane (core)
Toggleable pane per session tab (reuse the split-view muscle from v1): remote listing,
upload/download with progress, drag-drop from Explorer in, drag-out via temp file,
"download & open", rename/delete/mkdir/chmod. Separate `SftpClient` connection reusing the
session's credentials/host-key trust.

### 3.2 "Follow the terminal" current-directory tracking
"Open file pane at current folder" needs the remote cwd:
- **Persistent (tmux) sessions:** trivial — `tmux display -p '#{pane_current_path}'` over
  the existing side-channel (`TryRunCommand`).
- **Plain sessions:** OSC 7 reporting if the user's shell emits it (offer a one-line
  snippet to add to `.bashrc`); otherwise fall back to home dir.

### 3.3 Real Explorer window (optional integration)
If **SSHFS-Win/WinFsp** is installed, "Open in Explorer" launches
`\\sshfs.r\user@host\path` — an actual Explorer window on the remote FS. Detect install,
offer the link if present, hide otherwise. We don't bundle or reimplement a filesystem
driver.

---

## Phase 4 — Export / import & backup

On-disk format is already where it should be: `%APPDATA%\Sessions\` (`sessions.json`,
`settings.json`, `known_hosts.json`), human-editable JSON, atomic writes with `.bak`
rotation. Remaining work:

- **Export archive** (`*.sessionsbackup`, a zip): sessions + folders + settings +
  known hosts + highlight rules + custom icons + workspaces (Phase 8). Optional filter (export a folder subtree
  only). Recordings are excluded (large; they live in their own configurable directory).
- **Secrets:** Credential Manager entries are machine-bound and never in JSON. Default
  export excludes them; optional "include secrets" encrypts the archive with a
  user-supplied passphrase (AES-GCM, scrypt/PBKDF2 key derivation) and re-imports into
  Credential Manager on the target machine.
- **Import with merge:** match by session id, then by (host, port, username); prompt on
  conflict (keep / replace / duplicate). Reuse the dedupe logic patterns from
  `SecureCrtImporter`.
- **Schema version field** in the archive manifest so future format changes migrate cleanly.

---

## Phase 5 — Connectivity: tunnels, jump hosts, SSH agent

### 5.1 Port forwarding
SSH.NET has `ForwardedPortLocal/Remote/Dynamic` built in. Per-session tunnel list in the
session editor (type, bind address/port, destination), started with the connection, with
live status/error indicators in the tab and a quick "add tunnel" action on a running
session. Dynamic (SOCKS) covers the browser-proxy use case.

### 5.2 Jump hosts (ProxyJump)
Chain through one or more intermediate sessions: a `JumpHost` reference (another session id)
on the model, connected via SSH.NET's port-forwarding channel (connect to jump, open a
direct-tcpip channel, run the target handshake over it). Each hop uses its own auth —
pairs naturally with GSSAPI credential delegation from Phase 2. UI: dropdown in the session
editor picking an existing session as the jump; chains resolve recursively with a cycle
check.

### 5.3 SSH agent support
New auth option `Agent`: talk to the Windows OpenSSH agent named pipe
(`\\.\pipe\openssh-ssh-agent`, SSH agent protocol) and Pageant's shared-memory protocol.
List agent keys in the session editor, try-all or pin a specific key. Removes the need to
store key passphrases in Credential Manager.

---

## Phase 6 — Session icons

Per-session icon shown in the tree, tab strip, and title bar (alongside the existing
`ColorTag` accent). `Icon` field on `Session` (string key), picker in the session editor.

**Built-in packs (bundled SVGs):**
- **Linux/Unix:** Debian, Ubuntu, RHEL, CentOS, Fedora, SUSE, Arch, Alpine, generic Tux,
  FreeBSD/OpenBSD, Windows (for SSH-to-Windows), macOS.
- **Network OS:** Cisco (IOS/NX-OS), Juniper, Arista, Nokia (SR OS), Palo Alto, Fortinet,
  MikroTik, VyOS, HPE/Aruba — plus generic router/switch/firewall/server glyphs for the
  unbranded case.
- **Custom:** user drops PNG/SVG files into `%APPDATA%\Sessions\icons\`; they appear in the
  picker automatically (icon key = filename). Included in the Phase 4 export archive.

**Nice-to-have:** auto-suggest an icon on first connect from the SSH server banner /
detected OS (e.g. `Cisco-1.25`, `OpenSSH_… Ubuntu`), never overriding a manual choice.

### 6.1 Agent-aware tabs

Keep the session icon above as the stable identity of the saved host, and add a **second,
separate agent icon** to a tab while an agent CLI is active. The two icons answer different
questions: the session icon says *where this tab is connected*; the agent icon says *what is
currently running*. Initial built-in agent identities: Claude Code, Codex, Gemini CLI,
Pi / oh-my-pi, Grok Build, generic agent, and normal shell.

Agent identity can come from a recognized launch command or terminal title, a per-session
default, or a manual tab-menu override. Detection must never replace or modify a manually
selected session icon. Keep the existing connection dot as a third, independent signal for
SSH health.

**Attention state:** show a small badge on the agent icon for `working`, `needs approval`,
`needs answer`, `complete`, `failed`, or `idle`. An inactive tab that needs input keeps its
amber badge until the user sends input or a structured agent event reports that work resumed.
Selecting an alert focuses the correct tab and terminal. Optional Windows toast, sound, and
taskbar-count notifications apply only when the tab or app is in the background.

**Detection and adapters:** prefer explicit lifecycle events over terminal-screen guessing.
Provide small adapters for each supported agent using its hooks, extensions, notification
events, or terminal notification protocol. Normalize them to one minimal Sessions event
containing agent identity, attention state, and a short non-sensitive label. Accept OSC 9 and
BEL as generic fallbacks; pattern matching and quiet-output heuristics are low-confidence only
and must not claim that input is definitely required.

**Remote and security constraints:** validate the event path through plain SSH and the
persistent tmux mode. Bind structured events to the originating tab so arbitrary remote output
cannot impersonate a trusted event. Agent events may change UI state and focus a tab, but must
never approve a tool call or send input automatically. Do not include prompts, commands, or
terminal output in desktop notifications by default. Adapter installation on a remote host is
explicit, previewed, and reversible.

**Later integration:** agents that expose ACP or a similar structured runtime can get richer
status and controls in a later phase. The first version remains a terminal integration rather
than turning Sessions into an agent IDE.

---

## Phase 7 — Session recording

Record terminal sessions for replay and audit; absorbs the old "output logging" backlog item.

- **Format: asciicast v2** (JSON-lines with per-event timestamps, includes resize events) —
  interoperable with the whole asciinema ecosystem — plus a plain-text option (ANSI-stripped)
  for greppable logs.
- **Capture point:** host side, at the `SshTerminalSession` output stream *before* the
  16 ms/32 KB WebView2 batching, so timing is faithful and recording works even for
  backgrounded tabs.
- **Controls:** record button on the tab (with a visible recording indicator), plus a
  per-session "always record" toggle via the Phase 0.1 override layer. Files auto-named
  `{session}-{timestamp}.cast` in a configurable directory.
- **Playback:** open a `.cast` in a read-only terminal tab — replay through xterm.js with
  pause/speed/seek. (Ships after write-side; asciinema tooling covers playback in the interim.)
- **Caveat to surface in UI:** recordings capture everything echoed to the terminal,
  including secrets a server echoes back — the recording indicator must be obvious.

---

## Phase 8 — Workspaces (saved layouts)

The tmux-resurrect/tmuxinator idea, GUI-native: a **workspace** is a named arrangement of
open sessions — which sessions, in which tab groups, in what order, which tabs are pinned,
and which tab is active — reopened with one click ("morning: jump box, both app servers,
the DB, side by side").

**Model** (`workspaces.json` in `%APPDATA%\Sessions\`, same atomic-write/`.bak` treatment):
- `Workspace`: id, name, ordered list of groups; each group is an ordered list of
  `{ sessionId, pinned }` plus the active-tab index. References sessions by id — a
  workspace is a *layout*, never a copy of session data. Deleted sessions are skipped on
  open with a note, not an error.
- Deliberately mirrors what `MainViewModel.Groups` / `TabViewModel` already hold, so
  capture is a straight serialization of live state ("Save current layout as…").

**UI:**
- "Save current layout as workspace…" + a workspaces section in the tree (or a title-bar
  dropdown) listing them; click to open, right-click to rename/update/delete.
- **Open semantics:** default replaces the current layout (prompting if tabs are open);
  modifier/context option to open *additively* into the current window. Sessions already
  connected aren't reconnected — tabs are adopted into position.
- **Restore on launch:** "Reopen last layout at startup" setting — the always-on companion
  feature (continuum to resurrect). Persist the live layout on clean exit; pairs especially
  well with tmux-persistent sessions, which reattach instantly.

**Sequencing note:** lands after the v1 two-group split cap is revisited (or ships with it —
the model above doesn't assume a cap). Include workspaces in the Phase 4 export archive.

---

## Backlog — "what else" (unordered candidates)

**Finish/verify v1 loose ends first:** private-key auth and keyboard-interactive fallback
have never been tested live; keyboard-interactive currently blind-echoes the saved password
to every prompt, which breaks 2FA/Duo — needs a real prompt dialog.

- **Broadcast input** — type into N tabs at once (the classic multi-host admin feature);
  the split view already gives the UI surface.
- **Command snippets** — saved commands with placeholders, per-folder scoping, send on
  click; optional "startup command" per session.
- **Quick connect** — `user@host[:port]` box in the title bar creating an ad-hoc session,
  with "save as session" afterward.
- **More importers** — PuTTY registry, mRemoteNG, OpenSSH `~/.ssh/config` (the config
  parser also helps GSSAPI/jump-host defaults).
- **Auto-reconnect UX** — backoff + toast on drop, auto-reattach when the session is
  tmux-persistent.
- **Per-session color schemes** — extend Phase 0.1 overrides + the two hardcoded palettes
  in `terminal.html`; `Session.ColorTag` already exists as the tab accent.

---

## Suggested order

| # | Item | Size | Risk |
|---|------|------|------|
| 1 | Phase 0 foundations (overrides, search, host-key override) | S–M | Low |
| 2 | **GSSAPI spike** (parallel with anything) | S | **High — do early** |
| 3 | Phase 6 session icons | S | Low |
| 4 | Phase 6.1 agent-aware tabs (identity → attention → adapters) | M | Medium |
| 5 | Phase 1 highlighting | M | Low |
| 6 | Phase 4 export/import | S–M | Low |
| 7 | Phase 5 connectivity (tunnels → agent → jump hosts) | M–L | Medium |
| 8 | Phase 3 SFTP pane (+ cwd tracking, SSHFS-Win link) | M–L | Medium |
| 9 | Phase 7 session recording | M | Low |
| 10 | Phase 8 workspaces (saved layouts) | M | Low |
| 11 | Phase 2 GSSAPI productization (path chosen by spike) | M | Depends on spike |
| 12 | Backlog picks | — | — |

Session icons slot in early because they're small, self-contained, and touch the same
session-editor surface Phase 0 reworks anyway. Agent-aware tabs follow them so the distinct
session and agent identities are designed together, and because background attention is more
valuable than cosmetic terminal highlighting. Export/import lands before SFTP because it's
small and unblocks backing up the growing session tree (and now bundles custom icons). Within
connectivity, tunnels come first (pure SSH.NET), then agent auth, then jump hosts (which build
on the tunnel channel code). Workspaces sits late so the split model has settled, but it's
independent enough to pull forward if it itches. GSSAPI ships last only because its spike must
settle the approach first.
