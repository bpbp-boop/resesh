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

## Phase 3 — File transfer & remote browsing ✅ shipped 2026-08-15 (pane + SSHFS link verified live; transfers/cwd-tracking pending deeper testing)

SSH.NET already ships `SftpClient`; there is zero transfer code today, so this is greenfield.

### 3.1 In-app SFTP pane (core)
Toggleable pane per session tab (reuse the split-view muscle from v1): remote listing,
upload/download with progress, drag-drop from Explorer in, drag-out via temp file,
"download & open", rename/delete/mkdir/chmod. Separate `SftpClient` connection reusing the
session's credentials/host-key trust.

### 3.2 "Follow the terminal" current-directory tracking ✅ shipped 2026-08-19
"Open file pane at current folder" needs the remote cwd:
- **Persistent (tmux) sessions:** trivial — `tmux display -p '#{pane_current_path}'` over
  the existing side-channel (`TryRunCommand`).
- **Plain sessions:** validated OSC 7 reporting when the user's shell emits it, then a
  zero-input Linux `/proc` query over a separate SSH channel, then a path-shaped prompt
  fallback, then the home directory. A compact tab-bar action opens the pane at the best
  current path. Host changes (for example, nested SSH) and a foreground non-shell process
  block stale-path fallback instead of opening the wrong folder.

### 3.3 Real Explorer window (optional integration)
If **SSHFS-Win/WinFsp** is installed, "Open in Explorer" launches
`\\sshfs.r\user@host\path` — an actual Explorer window on the remote FS. Detect install,
offer the link if present, hide otherwise. We don't bundle or reimplement a filesystem
driver.

---

## Phase 4 — Export / import & backup ✅ shipped 2026-08-16

On-disk format remains `%APPDATA%\Sessions\` (`sessions.json`, `settings.json`,
`known_hosts.json`), human-editable JSON, with atomic writes. Shipped work:

- **Export archive** (`*.sessionsbackup`, a zip): sessions + folders + settings +
  known hosts + highlight rules + custom icons + workspaces (Phase 8). Optional filter (export a folder subtree
  only). Recordings are excluded (large; they live in their own configurable directory).
- **Secrets:** Credential Manager entries are machine-bound and never in the on-disk JSON
  store. Default export excludes them; optional "include secrets" encrypts the complete
  archive with a user-supplied passphrase (AES-256-GCM, PBKDF2-SHA256) and re-imports
  them into Credential Manager on the target machine.
- **Import with merge:** match by session id, then by (host, port, username); prompt on
  conflict (keep / replace / duplicate). Reuse the dedupe logic patterns from
  `SecureCrtImporter`.
- **Schema version field** in the archive manifest so future format changes migrate cleanly.

---

## Phase 5 — Connectivity: tunnels, jump hosts, SSH agent

### 5.0 First-class private-key management ✅ shipped 2026-08-18
Private keys are shared registered resources, not file paths copied into every session.
`ssh-keys.json` stores a stable id, user-facing name, external path, algorithm, public
fingerprint, encryption state, and public key. Sessions keep only the key id. The app never
copies, moves, or deletes the private-key file; **Locate** repairs a moved path, and a changed
public fingerprint requires typed confirmation before use. Passphrases are stored once per key
in Windows Credential Manager and may stay in memory only when the user does not save them.

Existing path-based sessions migrate by normalized path without moving files. Backup schema 2
includes referenced key metadata and assignments, but never private-key files; optional encrypted
backups may include key passphrases. The session editor selects a registered key, while **File →
SSH Keys** adds, renames, locates, reports use, copies the public key, and safely removes unused
references. Password keyboard-interactive fallback now shows every server challenge in a real
visible/secret field instead of sending the saved password to every prompt.

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

## Phase 6 — Session identity and local terminals (base icons shipped 2026-08-15; 6.1 and 6.2 shipped 2026-08-16; title-bar icon deferred — needs SVG→ICO rasterization)

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

### 6.1 Local terminal profiles ✅ shipped 2026-08-16

Make local shells first-class Sessions targets instead of requiring a loopback SSH session or
an external terminal. Add a permanent, virtual **Local** root at the top of the session tree.
It is expanded by default and cannot be renamed, deleted, or moved. Local profiles participate
in search, tabs, splits, pinning, highlighting, the overview ruler, recording, and workspaces
in the same way as SSH profiles.

**Built-in profiles and tree behavior:**
- Discover PowerShell 7 (`pwsh.exe`), Command Prompt, PowerShell 5.1, installed WSL
  distributions, and Git Bash when present. Hide unavailable shells.
- Give discovered profiles stable identities so pinned tabs and workspaces can reference them.
  Users may edit or reset built-in profile defaults, and may create, rename, delete, and organize
  custom profiles below `Local`; local and SSH profiles cannot be dragged across that boundary.
- During normal browsing `Local` is always first. During filtering it follows normal match rules
  and disappears when no local profile matches, so it does not become an empty search result.
- Change the title-bar **+ Session** control to a split button: its primary action opens the
  default local profile; its menu lists local profiles plus **New SSH Session…** and
  **New Local Profile…**. `Ctrl+Shift+T` opens the default local profile, and Quick Connect
  searches local and SSH profiles together.

**Profile model and migration:** replace the SSH-shaped leaf model with one tagged profile
model containing common identity, folder, icon/color, and terminal overrides, plus exactly one
target: `SshTarget` (host, port, username, auth, tmux) or `LocalTarget` (executable, arguments,
starting directory, environment overrides). Store both in the same atomic profile store. Treat
existing JSON records with no target kind as SSH during migration. The `Local` root itself is
virtual and is never serialized as an ordinary folder. Keep executable and arguments separate;
do not store an opaque command string with ambiguous quoting.

**Terminal backend:** extract the input/output/resize/exit lifecycle from `TerminalTabView` into
a small backend contract. The current `SshTerminalSession` is one implementation; the local
implementation hosts the process with Windows ConPTY. ConPTY output feeds the existing xterm.js
byte path, terminal resize calls `ResizePseudoConsole`, and close/stop owns the complete child
process tree so an abandoned shell cannot leave child processes running. No console window may
flash during launch.

**Lifecycle and capability-aware UI:** local states are `starting`, `running`, `exited`, and
`failed`; a normal `exit` is neutral, while launch failure is red. Keep the tab open after exit,
show the exit code, and let Enter or **Restart** launch a fresh process with a scrollback divider.
Rename SSH-specific actions for local tabs (`Disconnect` → `Stop`, `Reconnect` → `Restart`) and
hide SFTP, SSHFS, host-key, tmux, and **End Remote Session** actions. The common backend surface
must expose capabilities so the UI does not grow scattered target-kind checks.

**Local profile editor:** name, executable, arguments, starting directory, environment-variable
overrides, icon/color, terminal overrides, and **Make default**. Defer elevation / **Run as
administrator** until a separate UAC and pseudoconsole-ownership design exists. Later, add a
local filesystem provider to the file pane and OSC cwd integration; first delivery may expose
**Open Working Folder** instead.

**Acceptance:** open PowerShell and Command Prompt together; verify interactive input, UTF-8 and
ANSI output, full-screen programs, resize, split/move without process loss, clone independence,
clean `exit`/restart, and close with no orphaned descendants. Verify discovered profiles survive
restart with stable identities, local profiles appear in search/Quick Connect, remote-only
commands never appear on local tabs, and an existing SSH `sessions.json` migrates unchanged.

### 6.2 Agent-aware tabs ✅ shipped 2026-08-16 (live-verified local + SSH; toasts deferred)

Shipped: `AgentTracker` (Core) resolves identity and attention from manual override →
adapter events → detection → session default, with every precedence rule unit-tested;
`OSC 7377` structured events plus OSC 9 / OSC 777 / BEL as low-confidence signals;
local detection from job-object membership and remote detection from the Phase 9.4
command marks (new `onCommand` hook); a second tab icon with an attention badge,
tab-menu override + session default, adapter snippets dialog, settings toggles, and a
taskbar flash / optional sound for background alerts. Details in DECISIONS.md.

Deferred: Windows toast notifications (needs package identity) and a taskbar count; an
alerts list to jump between waiting tabs; per-agent adapters beyond Codex, Claude Code,
and the generic shell wrapper; richer ACP-style status.

Use one icon slot in each tab. Keep the session icon as the normal identity of the saved target
(remote host or local shell), then replace it with the agent icon while an agent CLI is active.
Restore the session icon when the agent exits. Initial built-in agent identities:
Claude Code, Codex, Gemini CLI,
Pi / oh-my-pi, Grok Build, generic agent, and normal shell.

Agent identity can come from a recognized launch command or terminal title, a per-session
default, or a manual tab-menu override. Agent detection only changes the icon shown in the tab;
it must never modify the saved session icon. Keep the existing state dot as an independent
signal for SSH connection health or local-process health.

**Attention state:** show a small badge on the agent icon for `working`, `needs approval`,
`needs answer`, `complete`, `failed`, or `idle`. An inactive tab that needs input keeps its
amber badge until the user sends input or a structured agent event reports that work resumed.
Selecting an alert focuses the correct tab and terminal. Optional Windows toast, sound, and
taskbar-count notifications apply only when the tab or app is in the background.

The Codex adapter uses Codex lifecycle hooks: `SessionStart`, `UserPromptSubmit`,
`PermissionRequest`, `PostToolUse`, `Stop`, and `SessionEnd`. It reports status only through
OSC 7377. It does not return an approval decision, inspect tool input, or send terminal input.
Codex requires the user to review and trust the hook definition with `/hooks`.

**Detection and adapters:** prefer explicit lifecycle events over terminal-screen guessing.
Provide small adapters for each supported agent using its hooks, extensions, notification
events, or terminal notification protocol. Normalize them to one minimal Sessions event
containing agent identity, attention state, and a short non-sensitive label. Accept OSC 9 and
BEL as generic fallbacks; pattern matching and quiet-output heuristics are low-confidence only
and must not claim that input is definitely required.

**Transport and security constraints:** validate the event path through local ConPTY, plain SSH,
and persistent tmux mode. Bind structured events to the originating tab so arbitrary process or
remote output cannot impersonate a trusted event. Agent events may change UI state and focus a
tab, but must never approve a tool call or send input automatically. Do not include prompts,
commands, or terminal output in desktop notifications by default. Adapter installation on a
local or remote target is explicit, previewed, and reversible.

**Later integration:** agents that expose ACP or a similar structured runtime can get richer
status and controls in a later phase. The first version remains a terminal integration rather
than turning Sessions into an agent IDE.

---

## Phase 7 — Session recording & instant rewind

One capture spine, one player, three surfaces: rewind the live session (iTerm2
Instant Replay-style), record to disk for audit, and play back `.cast` files. Absorbs the
old "output logging" backlog item. The timing spine already exists — 9.5 records wall-clock
time per SSH read *before* the 16 ms/32 KB WebView2 batching, so timing is faithful and
capture works even for backgrounded tabs.

**Event shape everywhere: asciicast v2** (JSON-lines, per-event timestamps, resize events
included) — the in-memory ring and the on-disk file share one format, and disk recordings
interoperate with the whole asciinema ecosystem.

### 7.1 Instant rewind
"What did htop look like 10 minutes ago." Rewind is about **screen state, not scrollback**:
for streaming output, scrollback + the Phase 9 ruler already answer history better. Rewind
earns its keep exactly where they can't — alternate-buffer apps, cleared screens, TUIs that
overwrite in place — which is also where the ruler bows out (it hides on alt buffer), so
the features are complementary, not overlapping. Judge it on that turf.
- **Always-on bounded ring buffer** at the capture point: timestamped output + resize
  events, capped by bytes and/or minutes, trimmed from the tail. In-memory only, dies with
  the tab — the secrets caveat below doesn't apply (no disk, no export).
- **Keyframes for seeking:** an event stream gives state-at-T only by replaying everything
  before T. Snapshot full terminal state every N seconds / M bytes (check whether the
  vendored xterm.js includes the **serialize addon** — the search addon and ruler renderer
  were both already in the bundle); seek = nearest keyframe + replay the delta. Keyframes
  also let the ring drop raw events older than the last snapshot, keeping the cap honest.
- **Frozen-view UI:** enter rewind (freeze the tab or overlay a read-only twin), scrub a
  timeline with real wall-clock labels (9.5), jump back to live. Live output keeps
  accumulating into the ring while rewinding — viewing a snapshot, not pausing the session.
- **Fidelity rule:** replay by feeding bytes through xterm.js itself (resizes replayed from
  the stream) — never diff screen text.
- **tmux limit** (same as the command lane): capture-pane replay reconstructs content, not
  history — the ring starts at attach and covers the attached lifetime.

### 7.2 Recording to disk
- **Controls:** record button on the tab (with a visible recording indicator), plus a
  per-session "always record" toggle via the Phase 0.1 override layer. Files auto-named
  `{session}-{timestamp}.cast` in a configurable directory.
- **Formats:** asciicast v2, plus a plain-text option (ANSI-stripped) for greppable logs.
- **Caveat to surface in UI:** recordings capture everything echoed to the terminal,
  including secrets a server echoes back — the recording indicator must be obvious.

### 7.3 Playback
The same player as 7.1 with a second source: open a `.cast` in a read-only terminal tab,
replay through xterm.js with pause/speed/seek (keyframes built on load for scrubbing).
Build the player once — rewind and playback ship as siblings; asciinema tooling covers
file playback in the interim if 7.1 lands first.

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

**Sequencing note:** the v1 two-group split cap is now removed. The recursive row/column
layout can supply the workspace model above. Include workspaces in the Phase 4 export archive.

---

## Phase 9 — Annotated scrollbar (scrollback overview ruler)

A slim overview ruler on the terminal's scrollbar edge mapping the whole scrollback —
"where did that command start, where were the errors" answered at a glance instead of by
blind dragging. Composes Phase 0.2 (search) and Phase 1 (highlighting) into a map; the
line/timestamp index later feeds Phase 7 playback seeking, and Phase 6.2 agent events are
a future mark type.

**Visual model:** the ruler replaces the bare viewport scrollbar (no two adjacent scroll
affordances). Left lane = structure (command marks, bookmarks; red on nonzero exit),
right lane = content (error/warning/search ticks; active match brighter). Translucent
viewport window doubles as the thumb. Ticks bucket per pixel with density→opacity —
a 10 MB `big` dump must render as an intensity band, never 10k rects.

### 9.1 Search matches via the built-in ruler ✅ shipped 2026-08-16 (superseded by 9.2 same day)
The vendored xterm.js already contains VS Code's overview-ruler renderer (decorations
API `overviewRulerOptions`), and the search addon already emits ruler decorations —
the find bar's `matchOverviewRuler` colors were silently unused because the default
`overviewRuler: {}` (zero width) disables the renderer. Enabling is one constructor
option in `terminal.html` (`overviewRuler: { width: 14 }`).

### 9.2 Custom interactive ruler ✅ shipped 2026-08-16 (live-verified: CDP hover/click in the real app)
The built-in ruler is render-only; replaced by `addon-ruler.js` + the native viewport
scrollbar hidden. Click to jump (snaps to a mark within 8 px, flashes the target line),
hover tooltip (~150 ms; region line number, match/bookmark counts, first matching line),
drag scrubs, wheel forwards, translucent viewport window doubles as the thumb.
Split view automatically uses a Calm presentation: the 14 px pointer target draws as a
10 px rail, routine marks become faint and neutral, nearby marks merge, and the inactive
group dims further. Failures, bookmarks, and active search keep priority; hover restores
the full-width, full-color ruler. Single-group presentation is unchanged.
Sources: search + user bookmarks (Ctrl+Shift+M toggles one on the cursor line; xterm
Marker API — markers survive trimming for free). Key implementation facts: the search
addon does NOT expose match positions, so the ruler runs its own line-level buffer scan
(same query/flags, 4096-line slices per frame, debounced rescan on writes so trimming
can't leave stale ticks); alternate buffer hides the strip. Live-verified via the CDP rig
against `local-test` + `big` (hover tooltip, click flash). Still untried by a human:
a real Ctrl+Shift+M keypress.

### 9.3 Content lane — highlight-rule hits ✅ shipped 2026-08-16
The Phase-1-scans-viewport-only vs ruler-needs-whole-buffer tension resolved as
planned: `onLineFeed` kicks a budgeted idle-time indexer (`requestIdleCallback` with a
500 ms timeout, rAF-slice fallback) that scans completed lines above the cursor row;
the same pass backfills existing scrollback, so tmux replay dumps just flow through
ingest. Index = Map(line → rule bitmask), ≤32 overview rules, NOT per-line decorations.
Lines are keyed by trim-stable "virtual" numbers anchored to a sentinel marker,
re-anchored near the cursor each pass (gap 4096) so trimming can't reach it; a flood
that outruns re-anchoring disposes the sentinel → full rebuild (verified: mapping
survives 6000 steady-drip trims exactly; a 8000-line flood takes the rebuild path and
re-indexes exactly). Resize reflow and rule swaps also rebuild; the not-yet-indexed
span paints as a faint veil (`pending` palette color) instead of a literal shimmer.
Per-rule `showInOverview`: builtin default on only for `state-negative`; custom rules
get an editor checkbox (builtin overview flags are code-fixed — the deltas store only
covers enabled state). Ticks paint in the content lane at 0.5 alpha of the rule color
under search ticks; multi-rule lines use the highest bit (later rule wins, matching
decoration precedence). Tooltip gains per-rule hit counts ("2× Down/error states");
click-snap includes hits. Verified pixel-exact in the stubbed harness (tick blends,
trim/re-anchor/prune, rule swap, alt buffer, resize) AND live via the CDP rig
(negative-states ticks + tooltip with rule name, proving the C# payload plumbing).

### 9.4 Command marks — OSC 133 + OSC 3008 + Enter-gated prompt discovery ✅ shipped 2026-08-16; OSC 3008 added 2026-08-19
Three sources feed one left-lane (bookmarks paint over them — explicit beats inferred):
- **Exact — OSC 133 (FinalTerm)**: A/B remember the prompt line, C commits a mark
  there (a command actually ran), D;exit colors the tick (green ok / red fail).
  Shells emitting A/D but never C still commit on D (empty Enters indistinguishable
  there; accepted). Handler registered via `parser.registerOscHandler(133)`.
- **Discovered — for the fleet of VMs/network devices that will never get a custom
  bashrc** (user decision 2026-08-16, revising this section's original "no
  prompt-regex guessing" stance; PASSIVE output scanning stays banned — discovery is
  gated on the user pressing Enter, which output that merely looks like a prompt
  never coincides with). On Enter (page forwards from `term.onData`), a probe marker
  anchors the cursor row; its text is evaluated only after the remote echo settles
  (300 ms + one 900 ms retry — typed chars echo from the REMOTE side, so a fast
  paste/laggy link puts Enter ahead of its own command's echo; found live, the sync
  version missed every SendKeys-speed command). Regex covers default Linux PS1s,
  bracketed prompts, Cisco `sw1#cmd` (no space), Junos, bare `$`/`#`/`%`/`>`, REPLs;
  requires a command after the terminator (empty prompt never marks); walks back soft
  wraps to the prompt row. Neutral gray ticks (no exit knowledge). First OSC 133
  sequence in a session disables discovery — the shell knows better than the regex.
- **Stock systemd — OSC 3008 (UAPI.15)**: systemd 258+ can install a Bash profile
  hook that reports shell/command contexts without a user bashrc change. Sessions
  accepts bounded context payloads, validates cwd/host metadata, and uses shell or
  command cwd as another current-folder signal. A command context ID links its exact
  exit status to the existing Enter-gated mark; it does not replace prompt discovery
  because the stock hook does not include command text. OSC 133 stays authoritative
  when both standards are present. Older and minimal VM/LXC images keep all existing
  title, OSC 7, prompt, and Enter-gated fallbacks.
- **No automatic remote setup**: Sessions never probes for Bash and never types a
  context hook into an interactive shell. OSC 3008 is passive input from hosts that
  already provide it. Older Linux hosts and network devices keep the existing no-input
  title, prompt, OSC 7, and Enter-gated discovery paths.
Ctrl+Shift+Up/Down jump prev/next command from the viewport center, with flash.
Tooltip: "exit N" / "command" / "N commands"; sample falls back to the command line.
tmux replay limit stands: capture-pane preserves neither OSC marks nor keystrokes, so
the command lane starts fresh on reattach. Discovery stays active because the stock
OSC 3008 hook does not include command text. Verified
in the stubbed harness (both tiers, slow-echo
probe, traps, jump keys via synthetic keydown, exit-color pixels) and live against
local-test (real SSH echo → discovered gray ticks, pixel-exact). Real human
accelerator keypresses remain untried (standing item). Next/prev **error** jump
(failed-exit marks only) deferred.

### 9.5 Timestamps ✅ shipped 2026-08-16 (render-verified in xterm harness)
The native host records Unix wall-clock time for every SSH read before the 16 ms/32 KB
WebView batch combines reads. Batch payloads retain the original byte offsets, and page-side
writes are serialized so each parsed logical line gets the correct coarse ingest time. A
compact marker-anchored virtual-line index follows scrollback trimming; logical-line snapshots
around `fit()` preserve times through resize/font reflow. Ruler hovers now answer "when did this
happen" (`exit 2 · 14:32 · 3h ago`). This is also the timing spine Phase 7 recording needs.
Verified with focused tests for async write ordering, wrapped lines, trimming, reflow, formatting,
and command tooltip composition, plus a rendered xterm harness (tooltip text and resize reflow).

---

## Phase 10 — Telnet & serial consoles

Console-cable and terminal-server access — the last standing objection from the SecureCRT
crowd. Cheap now because Phase 6.1 already did the hard part: `ITerminalBackend` +
`SessionCapabilities` mean telnet and serial are just two more backend implementations
feeding the same xterm byte path, with remote-only UI (SFTP, tmux, host keys, agent)
hidden by capability flags exactly as local shells do. Everything page-side — highlighting,
the overview ruler, Enter-gated command discovery, recording — works unchanged.

New target kinds alongside `SshTarget`/`LocalTarget` (same tagged-profile model, same
atomic store, no migration impact): `TelnetTarget` and `SerialTarget`.

### 10.1 Telnet

- **Target:** host, port (default 23). Quick connect accepts `telnet host[:port]` — terminal
  servers map console lines to high ports (`200x`-style), so port entry must be frictionless.
- **Backend:** raw TCP plus minimal IAC option negotiation — BINARY, SGA, ECHO (remote echo
  on, we never echo locally), TERMINAL-TYPE (`xterm-256color`), and NAWS wired to terminal
  resize. Politely refuse (WONT/DONT) everything else. A few hundred lines; no library needed.
- **Capabilities:** no host keys, no SFTP/SSHFS, no tmux, no auth storage. Disconnect/
  Reconnect verbs as SSH.
- **UI note in the editor:** plaintext protocol — position it for console servers and lab/
  management networks, not general remote access.

### 10.2 Serial

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
Phase 4 export archive.

---

## Field feedback — 2026-08-18

### Multi-session connect
- Pressing Enter with two or more sessions selected connects them in tabs. ✅ fixed 2026-08-18
- Concurrent connection prompts are serialized per window, preventing password, private-key,
  host-key, and tmux dialogs from colliding. ✅ fixed 2026-08-18

### Credentials
- First-class external private-key registry, shared key assignments, key-scoped passphrases,
  safe legacy migration, backup metadata, fingerprint-change warning, and explicit
  keyboard-interactive prompts. ✅ fixed 2026-08-18

### Tabs and framing
- Tabs split out of a group can be dragged back into another group's tab strip. ✅ fixed 2026-08-18
- Session content and tabs have a subtle theme-aware gray frame, similar to VS Code. ✅ fixed 2026-08-18

### Tab subtitle on plain (non-tmux) hosts
- The subtitle stuck at the login cwd ("~"): stock PS1s refresh the OSC title only when
  the NEXT prompt is drawn, and interactive programs almost never set one, so nothing ever
  said what was running. The ruler's Phase 9.4 command sources now also report the command
  TEXT (Enter-gated discovery, and OSC 133;B/C exactly), the page posts it as a `command`
  message, and the tab shows its program name (`CommandTitle`: VAR=/sudo/env prefixes
  skipped, path stripped) until a prompt-shaped title or 133;D says it ended. A title that
  arrives between Enter and the discovery probe supersedes the guess (fast commands finish
  first; full-screen apps title themselves) — the page drops stale-epoch discoveries.
  Discovery probes the normal buffer, so a full-screen app that already took the alternate
  screen still gets titled (the mark stays normal-screen-only, as before). Subtitle
  precedence: program-set title → running command → prompt cwd → endpoint.
  ✅ fixed 2026-08-18 (browser-harness + node tests; live SSH pass still pending)
- Spaced Bash prompts such as `user@host ~ $` are also recognized. Their cwd replaces the
  endpoint while idle and reports the return to the prompt, so a detected command does not
  stay in the subtitle after it ends. Fixed 2026-08-18.
- The foreground-process side channel remains in the backlog for tmux-grade names with
  zero shell cooperation.

---

## Backlog — "what else" (unordered candidates)

**Non-goal — scripting/automation API (decided 2026-08-16):** Sessions is the interactive
layer; fleet automation belongs in Ansible/Netmiko-class tooling run from a trusted host,
where it's versioned and reviewed. No embedded scripting language, ever. Command snippets
stay in scope — interactive-shaped, not automation-shaped. **Broadcast input is cut too**
(2026-08-16): never once wanted in years of ISP operations, it's the classic
multi-device-typo footgun on heterogeneous fleets, and anything worth sending to N devices
at once is worth a playbook. Power users on tmux-persistent sessions already have
`synchronize-panes` if they truly want it.

**Finish/verify v1 loose ends first:** private-key auth and keyboard-interactive fallback
have never been tested live; keyboard-interactive currently blind-echoes the saved password
to every prompt, which breaks 2FA/Duo — needs a real prompt dialog.

- **Command snippets** — saved commands with placeholders, per-folder scoping, send on
  click; optional "startup command" per session.
- **Copy output since last command** — copy the terminal output produced after the most
  recently submitted command, without manually selecting the scrollback range.
- **Quick connect** — `user@host[:port]` box in the title bar creating an ad-hoc session,
  with "save as session" afterward.
- **More importers** — PuTTY registry, mRemoteNG, OpenSSH `~/.ssh/config` (the config
  parser also helps GSSAPI/jump-host defaults).
- **Auto-reconnect UX** — backoff + toast on drop, auto-reattach when the session is
  tmux-persistent.
- **Per-session color schemes** — extend Phase 0.1 overrides + the two hardcoded palettes
  in `terminal.html`; `Session.ColorTag` already exists as the tab accent.
- **Manual shell-integration snippet** — optionally show and copy OSC 133/OSC 7 setup
  for users who choose to install it. Sessions must never send setup code automatically.
- **Foreground-process title side channel** — tmux-grade `pane_current_command` on plain
  sessions with zero shell cooperation: each tab owns its SSH connection, so an exec
  channel can locate the shell channel's process ($PPID → our sshd child → its child
  holding a tty), then watch `/proc/<shell>/stat` tpgid → `/proc/<tpgid>/comm`. Sees
  nested children (make → cc) and survives fancy prompts; Linux/procfs only, adds polling,
  and assumes one shell per connection. Back pocket unless discovery + stock context data prove
  insufficient.

---

## Suggested order

| # | Item | Size | Risk |
|---|------|------|------|
| 1 | Phase 0 foundations (overrides, search, host-key override) | S–M | Low |
| 2 | **GSSAPI spike** (parallel with anything) | S | **High — do early** |
| 3 | Phase 6 session icons | S | Low |
| 4 | Phase 6.1 local terminal profiles (model → ConPTY → parity) ✅ shipped 2026-08-16 | M–L | Medium |
| 5 | Phase 6.2 agent-aware tabs (identity → attention → adapters) ✅ shipped 2026-08-16 | M | Medium |
| 6 | Phase 1 highlighting | M | Low |
| 7 | Phase 4 export/import ✅ shipped 2026-08-16 | S–M | Low |
| 8 | Phase 5 connectivity (tunnels → agent → jump hosts) | M–L | Medium |
| 9 | Phase 10 telnet & serial consoles (telnet → serial) | S–M | Low |
| 10 | Phase 3 SFTP pane (+ cwd tracking, SSHFS-Win link) | M–L | Medium |
| 11 | Phase 9 annotated scrollbar (9.1–9.5 ✅) | S–M | Low |
| 12 | Phase 7 recording & rewind (ring/rewind → disk → playback) | M | Low |
| 13 | Phase 8 workspaces (saved layouts) | M | Low |
| 14 | Phase 2 GSSAPI productization (path chosen by spike) | M | Depends on spike |
| 15 | Backlog picks | — | — |

Session icons slot in early because they're small, self-contained, and touch the same
session-editor surface Phase 0 reworks anyway. Local terminal profiles follow them because the
shared profile/backend model must settle before agent adapters and workspace restore depend on
it. Agent-aware tabs then keep profile identity, process state, and agent identity separate.
Export/import lands before SFTP because it's
small and unblocks backing up the growing session tree (and now bundles custom icons). Within
connectivity, tunnels come first (pure SSH.NET), then agent auth, then jump hosts (which build
on the tunnel channel code). Telnet/serial rides directly behind connectivity: it reuses the
6.1 backend contract as-is, telnet before serial (no new dependency vs. port enumeration +
device-arrival plumbing), and together they close the console-cable objection cheaply. Workspaces sits late so the split model has settled, but it's
independent enough to pull forward if it itches. GSSAPI ships last only because its spike must
settle the approach first.
