# Decisions

Running log of version-specific findings and deliberate deviations, per the project plan.

## 2026-08-14 — Windows App SDK 2.4.0
The plan says "latest stable"; NuGet's latest stable line is 2.x. Pinned **2.4.0**. It restores and
builds unpackaged WinUI 3 (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`) with the
plain `dotnet` CLI on .NET SDK 10.0.101, targeting `net8.0-windows10.0.19041.0`. No 1.x fallback was
needed.

## 2026-08-14 — No `required` members on `Session`
The WinUI XAML type-info generator (`XamlTypeInfo.g.cs`) emits a parameterless activator for any
type used as `x:DataType` in a template. `required` members break that with CS9035. `Session`
therefore uses defaulted init-only properties instead of `required`.

## 2026-08-14 — TreeView drag & drop strategy (M1)
Using the built-in `CanDragItems` + `CanReorderItems` + `AllowDrop` behavior and committing the move
in `DragItemsCompleted` (`NewParentItem` → target folder), then rebuilding the tree from the model.
The model (alphabetical order, folder paths) is the source of truth; any visual reorder the TreeView
performed is discarded by the rebuild. Folders are not draggable in v1 (cancelled in
`DragItemsStarting`).

## 2026-08-14 — TreeView expansion state across rebuilds
Two-layered problem, diagnosed with a trace log (`%LOCALAPPDATA%\Sessions\trace.log`, DEBUG only):

1. `Collapsed` can be raised for nodes being *removed* during a rebuild (sometimes after the
   rebuild returns), which corrupted the persisted expansion map. Fix: expansion changes are only
   recorded for node instances belonging to the current tree generation (`_currentNodes` set).
2. `TreeViewItem.IsExpanded` set via template binding is ignored when the item's children aren't
   realized yet (container recycling) — folders stayed collapsed after a search was cleared even
   though the VM said expanded. Fix: `IsExpanded` binds OneWay, and after every rebuild the window
   pushes VM state onto realized containers, retrying on a 50 ms timer (containers realize lazily
   across layout passes; same-tick retries burn out before realization).

## 2026-08-14 — SSH.NET 2026.0.0 resize (M2)
`ShellStream` still has no public window-change request. `ShellStreamResizer` reaches the private
`_channel` field and invokes `SendWindowChangeRequest(uint, uint, uint, uint)` via reflection;
`ShellStreamResizerTests` pin both member names so a package bump fails tests instead of silently
breaking resize. Verified live against a local server: resizing the window produced server-side
window-change requests at the correct dimensions.

## 2026-08-14 — Connect timeout vs. the in-handshake host key dialog (M2)
The first-connect host key confirmation happens *inside* the SSH handshake, so a short
`ConnectionInfo.Timeout` expires while the user reads the fingerprint (observed live). Design: a
10-second raw TCP pre-flight classifies unreachable hosts quickly, then the real connect runs with
a 2-minute timeout so the human decision has room.

## 2026-08-14 — Terminals are not TabViewItem.Content (M2)
A UIElement assigned to `TabViewItem.Content` via `x:Bind` in `TabItemTemplate` parents into the
~48 px tab-strip item, not the TabView content region (measured: the terminal got arranged at
height 48). Terminals therefore live in a `TerminalHost` grid below the TabView (strip only), with
visibility synced to the selected tab. This also keeps every WebView2 alive and un-reparented —
which M4's tab groups need anyway.

## 2026-08-14 — M4: tab groups, context menu, reparenting (verified live)
- Moving a connected tab between groups (Split Right / Move to Other Group) re-parents the
  TerminalTabView between group hosts. Verified: the SSH connection and scrollback survive, and
  the size change propagates to the server as a window-change. The suspected "reparent kills SSH"
  bug was actually the FxSsh idle timeout (below).
- The tab context menu is built once in code and opened from a RightTapped handler (x:Name fields
  aren't generated for items inside a MenuFlyout resource, and x:Bind inside flyouts in templates
  is unreliable); per-tab enablement is set in a configure step before ShowAt.
- Middle-click close is implemented with paired PointerPressed/Released handlers checking the
  press and release landed on the same TabViewItem; all close paths (X, Ctrl+F4 — forwarded from
  the xterm page since WebView2 swallows accelerators —, menu, middle-click) converge on
  MainWindow.RequestCloseTabAsync, the single always-confirm pathway.
- Split Right is disabled for a lone tab (moving the only tab right would leave an empty left
  group that immediately collapses — a visual no-op).
- Explicit 2-slot group grid (left | GridSplitter | right) instead of a generic N-group host;
  the plan's TabGroupHost abstraction lives in MainWindow's group management methods.

## 2026-08-14 — FxSsh idle timeout masquerading as a client bug
Idle connections to the FxSsh test server die after ~30 s —*without* client keepalive. The app's
30-second `KeepAliveInterval` races that timeout, which made connections appear to drop on tab
moves. Isolated with tools/KeepaliveProbe: no-keepalive dies at ~30 s idle, 5-second keepalive
survives. Fix in the rig: `session.ConfigureKeepalive(10s)` server-side. Client keepalive stays
at the plan's 30 s (correct for real hosts).

## 2026-08-14 — Remote-disconnect detection (post-v1 fix, user-reported)
Two real-host findings: (1) input written to a dead `ShellStream` throws
`SshConnectionException("Client not connected.")` — now swallowed in `Write` (the reader path
reports the disconnect; input to a dead session is meaningless). (2) SSH.NET's blocked
`ShellStream.Read` and its keepalive never notice a dead peer (observed: killed server left the
session "connected" for 35+ s indefinitely). `Closed` is now raised (exactly once) by whichever
detector fires first: `SshClient.ErrorOccurred` (instant on TCP RST), a 5-second `IsConnected`
watchdog timer (fallback), or the read loop. Reconnect also disposes the previous dead session,
which used to leak its blocked reader thread. Tabs got a green/amber/red state dot.

## 2026-08-14 — Local end-to-end test rig (tools/TestSshServer)
FxSsh 1.4.0 echo server on 127.0.0.1:2200 (`test`/`test123`), persistent host key in
`bin/.../hostkey.txt`. Verified against it: password auth, first-connect host key accept + persist
to known_hosts.json, host key MISMATCH hard-fail (no bypass), 10 MB dump through the batched bridge
without UI freeze, live resize, clipboard paste, server-side close → in-place reconnect with
scrollback divider. Caveats found while building it: FxSsh's `DataReceived` runs on the session's
receive pump (big sends must go via a worker task, chunked), and `RsaKey`'s ctor takes the SHA-2
bit length. Still needing a real Linux host to verify: `top`/`vim` rendering, private-key auth,
keyboard-interactive fallback.

## 2026-08-14 - Phase 0: per-session overrides, terminal search, host-key override
- Session gains optional `TerminalOverrides` (theme/font/size/scrollback; null = inherit app
  setting), resolved via `AppSettings.WithOverrides`. Editor UI is an "Appearance overrides"
  expander; an all-null overrides object is stored as null to keep sessions.json clean.
- Initial terminal options now travel via an init handshake: the page posts `init`, the host
  replies `initOptions`, and only then is the xterm Terminal constructed - born with the right
  theme/fonts instead of restyled by a `setOptions` push right after load. `setOptions` remains
  for live settings changes and applies theme before the layout-affecting setters (fenced in
  try/catch, errors reported via `pageError`).
- Terminal search: vendored @xterm/addon-search 0.16.0 UMD (compatible with the bundled xterm's
  decorations API); find bar in terminal.html with case/regex toggles, match counter
  (`onDidChangeResults`, 999+ cap), Enter/Shift+Enter, Esc. Copy-on-select is suppressed while
  the find bar is open because the addon selects each active match.
- Host-key mismatch is no longer a hard fail: `HostKeyDecision` is now consulted for Mismatch
  too (with the previously trusted key in `HostKeyInfo.Previous`); the dialog shows old/new
  fingerprints and requires typing the host name to enable "Replace Key and Connect".
  Default-deny stands when no handler is wired. Verified live: tampered fingerprint ->
  dialog -> typed confirm -> key replaced -> clean reconnect.
- WebView2 gotcha (REAL, cost hours): Chromium heuristic-caches virtual-host-mapped files
  (they carry Last-Modified but no Cache-Control), so a rebuilt terminal.html can be served
  STALE - the new find bar silently missing while the exe is current. Fix:
  `Profile.ClearBrowsingDataAsync(DiskCache)` before Navigate (assets are local; re-read is
  free). Also killed lingering msedgewebview2 processes lock %LOCALAPPDATA%\Sessions\WebView2.
- Debug diagnostics: TerminalControl.TraceHook mirrors SshTerminalSession's (wired to
  trace.log); the page reports JS errors via `pageError` messages and keeps a rolling
  `window.__msgLog` of option pushes.
- Verification note for the future: judging terminal THEME from downscaled screenshots is
  unreliable - an entire phantom "theme not applying" hunt came from misread screenshots while
  DOM state was correct the whole time. Sample pixels (Bitmap.GetPixel) or query the DOM.

## 2026-08-15 - Phase 1: keyword highlighting
- Rule model/persistence: built-in packs live in code (`BuiltinHighlights`, stable ids) so app
  updates can fix patterns; highlights.json stores only deviations - global enable/disable
  deltas plus full custom rules (`HighlightsStore`, same atomic-write/.bak scheme as
  SessionStore). Per-session state is `EnabledRules`/`DisabledRules` id-deltas on
  `TerminalOverrides` (never rule copies), resolved as: session delta > global delta > shipped
  default. Toggled from the tab context menu ("Highlighting" submenu); the session editor
  carries the deltas through untouched.
- Rendering (addon-highlight.js, our code, not vendored): scans only viewport rows on
  onRender - the 16 ms/32 KB stream batch path is untouched - with a per-line text cache so
  the render feedback loop (decoration paint -> render event -> rescan) settles immediately.
  Matches capped at 40/row. Alternate buffer (vim/htop) is never scanned: markers only exist
  in the normal buffer, and highlighting full-screen apps would be wrong anyway. Patterns are
  written in the .NET/JS-common regex subset: validated host-side in .NET, executed in JS.
- Decorations API findings (bundled xterm.js, DOM renderer):
  - `foregroundColor` recolors cells reliably (verified via CDP span inspection).
  - `backgroundColor` STRIPS ALPHA (`rgba>>8` in the cell resolver) - a translucent tint
    becomes an opaque block the same color as the foreground, making text invisible. Caught
    live. Tints/underlines go on the decoration overlay element instead (translucent CSS
    background / bottom border); overlay divs render at z-index 6 ABOVE the text plane, so
    translucency is mandatory.
  - Glyphs cannot be re-weighted through decorations, so a rule's `bold` renders as a 22%
    background tint of the rule color (kept in the schema for intent + forward compat).
  - IPv6 regex: compressed (`::`) alternative must precede the uncompressed one or leftmost-
    first alternation matches only the prefix of compressed addresses ("2001:db8:0:1::1" would
    highlight as "2001:db8:0:1"); uncompressed form requires 3+ groups so hh:mm:ss timestamps
    stay unmatched; `(?<![\w:])` keeps C++ `std::name` tokens out.
- Verified live against the local rig (CDP DOM inspection, per the Phase 0 lesson): all 10
  default packs color correctly on typed+echoed rows, negative-state tint translucent, 10 MB
  dump + scrollback scrolling with zero pageErrors, decorations pruned to 0 on non-matching
  viewport. Not yet exercised live: per-session toggle UI and the custom-rule editor dialog
  (logic unit-tested; 73 Core tests green).

## 2026-08-15 - Phase 3: file transfer & remote browsing (SFTP pane)

- New `Sessions.Core.Sftp`: `SftpSession` wraps SSH.NET's `SftpClient` as a second,
  independent connection per tab. Shared plumbing was extracted from `SshTerminalSession`
  into `SshConnectionFactory` (auth methods incl. the keyboard-interactive quirk, failure
  classification, TCP preflight) rather than duplicated. Auth methods are stateful in
  SSH.NET, so the factory builds a fresh set per client.
- Host-key policy for the SFTP channel: trust ONLY on `KnownHostsStore.Check == Match`.
  Unknown/changed keys fail with a pointer to the terminal path - the user already made
  that decision once; a second surprise dialog on the file channel would be wrong.
- The tab caches the resolved connect secret (`TerminalTabView._secret`) so opening the
  file pane never re-prompts for a password that was typed but not saved.
- Transfers are cancellable stream copies (64 KB chunks) rather than SSH.NET's
  `UploadFile`/`DownloadFile` (no cancellation there); partial targets are deleted on
  cancel. One remote operation at a time per pane - the pane serializes and shows a
  progress strip (per-file bytes + N-of-M).
- Pure helpers (`RemotePath`, `UnixPermissions`, `RemoteFileEntry.Sort`) carry the
  path/permission/collision logic so it unit-tests without a server (TestSshServer speaks
  neither SFTP nor exec channels; live verification needs a real host). Permission modes
  use SSH.NET's octal-as-decimal `short` convention (755 = rwxr-xr-x).
- Pane UI (`SftpPaneView`) is fully code-built (HighlightEditorDialog precedent): composed
  ListView rows with `Tag` carrying the entry, compact 26px rows, toolbar
  (up/home/path/refresh/mkdir/upload/close), context menu, Explorer drag-IN via
  StorageItems. Drag-OUT (temp-file materialization on DragStarting) is deferred -
  "Download & Open" covers the common case.
- Layout: `TerminalTabView` grew columns [terminal | splitter | pane]; the lock overlay
  spans all three. Pane width persists as `AppSettings.FilePaneWidth`. Ctrl+Shift+E
  toggles - registered BOTH as a window accelerator and forwarded from the xterm page
  (WebView2 swallows accelerators), same dual-path as Ctrl+F4.
- Cwd tracking ("Open File Pane at Current Folder", persistent sessions only): new
  `SshTerminalSession.TryRunCommandForOutput` (TryRunCommand discarded stdout) +
  `TmuxPersistence.CurrentPathCommand` - `display-message -p -t =<name>
  '#{pane_current_path}'`; explicit `-t` because an exec channel has no attached tmux
  client. Plain sessions fall back to home (OSC 7 reporting = future work).
- SSHFS-Win: detection only (`sshfs-win.exe` under Program Files or the registry key),
  UNC `\sshfs.r\user@host[!port]\path` launched via explorer.exe. Toolbar button appears
  only when installed; nothing bundled.
- 111 Core tests green (38 new). Not yet exercised live: everything SFTP needs a real
  host - the whole pane, cwd query, and SSHFS link are untested against actual servers.
- Live-testing fixes (same day): (1) explorer.exe handed an unmounted sshfs UNC cannot
  authenticate — it silently opens Documents (observed; WinFsp provider + all four
  sshfs prefixes were registered and running, so it is purely a credential-path issue).
  Fix: mount first via WNetAddConnection2 (deviceless connection, password from the tab's
  cached secret — never on a command line; 1219 credential-conflict = already connected =
  success), THEN launch Explorer; key-auth sessions use \sshfs.kr\ (default-profile key).
  (2) "Open at current folder" opened home: the cwd query was rebuilt from
  `display-message -p -t =<name>` to `list-panes -a -F` + client-side matching
  (ParseCurrentPath, unit-tested) — no server-side target resolution to go wrong — and the
  fallback now says WHY in the pane status (stderr surfaced via new
  SshTerminalSession.RunCommand, which replaced TryRunCommandForOutput). Root cause on the
  droplet not yet confirmed; the status message will name it on the next retest.
- SSHFS retest root cause (confirmed live on this machine with WNet probes): the error was
  1203 ERROR_NO_NET_OR_BAD_PATH, and probing showed \sshfs.kr\... returns 1203 while every
  \sshfs.r\... form mounts fine (error 5 = local sshd auth-refused, i.e. the full chain
  worked) — this install (SSHFS-Win 3.7.21011 / WinFsp 2.0) registers the .k/.kr launcher
  services but its network provider never claims those prefixes. So key-auth sessions had
  picked \sshfs.kr\ and died before reaching sshfs. Fix: Connect() tries prefixes in order
  (kr -> r for key auth), returns the root that mounted for the Explorer launch, stops
  falling back on non-1203 errors, and maps 5/1326 ("login refused"; sshfs uses its own
  %USERPROFILE%\.ssh default key, NOT the session's PrivateKeyPath) and 1203 ("install
  doesn't accept this prefix") to readable messages.
- Key-auth sessions and SSHFS (the "why can't it use my key" answer, now designed around):
  the UNC/WNet mount API only carries user+password, and sshfs-win hard-wires the auth mode
  per prefix (\sshfs.r\ forces PreferredAuthentications=password; the .k prefixes are the
  ones this install never claims). No UNC route can use the session's PrivateKeyPath.
  Fix: key-auth sessions bypass the network provider entirely — spawn the bundled
  sshfs.exe directly (`user@host:/ X: -f -o IdentityFile=<session key> -o BatchMode=yes
  -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null` + sshfs-win's
  Explorer-friendly defaults) on a free drive letter, wait for the drive to appear
  (20 s), then open Explorer there. The -f process IS the mount: the tab owns an SshfsMount
  and kills it on dispose to unmount. Host-key checking is off for this channel because the
  app's own KnownHostsStore already pinned the host via the terminal; ssh's prompt would be
  unanswerable from a windowless child. Passphrase-protected keys are refused with a clear
  message (no askpass channel). Windows-style C:/ paths confirmed accepted by the cygwin
  sshfs via local probes; the full option set was validated against the real binary
  (each option parses; run reaches the ssh connect stage). Password sessions keep the
  WNetAddConnection2 UNC route. Live droplet verification still pending.
- "read: Connection reset by peer" on the direct sshfs mount, root-caused and FIXED
  (verified live against the droplet — mounted, listed /, clean unmount on kill): sshfs.exe
  resolves `ssh` from PATH, which in the app's environment finds Windows' native OpenSSH
  (9.x) instead of the bundled cygwin ssh (8.4). A win32 ssh can't inherit the cygwin
  socketpair sshfs hands it, the remote sftp-server sees instant EOF and exits 0, and sshfs
  reports the reset. (sshfs-win's own launcher avoids this via its wrapper; we bypass the
  wrapper deliberately to control IdentityFile.) Fix: `-o ssh_command=/usr/bin/ssh`
  (/usr/bin maps to the SSHFS-Win bin dir in cygwin's view). Diagnosis tell: the ssh debug
  lines in the failure had 9.x-style formatting while bin\ssh.exe is 8.4 — version-skewed
  debug output means the wrong binary is talking.
- Same reset AGAIN from the app after the ssh_command fix (worked from PowerShell, failed
  in-app). Isolated with a /target:winexe probe replicating the app's process context: a
  CONSOLE-LESS parent that redirects stdout/stderr passes hStdInput=NULL to the child, and
  cygwin's fd0 init then breaks the sshfs->ssh pipe chain — identical "read: Connection
  reset by peer" symptom as the wrong-ssh bug. Console-attached shells can't reproduce this
  (their stdin handle is valid even when closed - both held-open and closed-pipe stdin
  mounted fine). Fix: RedirectStandardInput=true so the child gets a real pipe handle.
  Verified: winexe probe fails without it, mounts with it. Lesson: two different root
  causes shared one symptom; a GUI-app process context (null std handles) must be part of
  reproducing child-process bugs — console shells lie.

## 2026-08-15 - Phase 6: session icons (base phase; 6.1 agent-aware tabs not started)
- `Session.Icon` is a string key with three-state semantics: null = unset (auto-suggest may
  fill it on first connect), `"none"` = explicitly no icon (blocks suggestion forever), else
  a built-in key ("ubuntu") or a custom-icon filename ("router-lab.png"). The sentinel exists
  so clearing a suggested icon doesn't just re-suggest it on the next connect — suggestion
  must never override any manual choice, including "no icon".
- Assets are badge-style: official brand marks (Simple Icons, CC0) recolored white and composed
  onto colored rounded squares, so every icon reads on both the dark (#181818) and light
  (#F3F3F3) tab strips. Direct2D's SvgImageSource handles Simple Icons' SVGO-compacted arc
  flags fine — verified live in the running app (cisco/debian render correctly in the tree).
- Exceptions to Simple Icons: **juniper** — the set only has the full "Juniper NETWORKS"
  logotype (illegible at 16 px) and juniper.net's favicon now serves HPE branding
  post-acquisition, so it keeps a hand-drawn "J" badge; **arista/vyos/aruba** — not in
  Simple Icons, their site favicons are composed onto white badges as PNGs (aruba's favicon
  is HPE's mark, which is correct — the entry is "HPE / Aruba"); **windows** + the generic
  router/switch/firewall/server glyphs are hand-drawn SVGs.
- Custom icons: files dropped in `%APPDATA%\Sessions\icons\` appear in the picker, key =
  filename. Built-in keys contain no dot, custom keys always do, so they can't collide.
  A session whose custom icon file was deleted round-trips its key through the editor
  ("(missing)" entry) instead of silently losing it.
- Auto-suggest maps SSH.NET's `ConnectionInfo.ServerVersion` banner (e.g. "SSH-2.0-OpenSSH_8.9p1
  Ubuntu-…", "SSH-2.0-Cisco-1.25", MikroTik's "SSH-2.0-ROSSSH") to a key. A bare OpenSSH banner
  suggests nothing. The window re-checks the *stored* session still has Icon == null before
  persisting, so a choice made while connecting wins.
- Title-bar icon (mentioned in the roadmap) deferred: swapping AppWindow.SetIcon per active
  tab needs runtime SVG→ICO rasterization for marginal value; revisit with 6.1.

## 2026-08-16 - Merged title bar (mockup 1a): menus + quick connect in the title bar
- Layout: content extends into a Tall (48 epx) title bar. Row hosts app icon + MenuBar
  (File/View/Session), a centered AutoSuggestBox quick connect (Ctrl+K), and + Session /
  always-on-top pin / settings on the right; system draws only the caption buttons. The old
  toolbar row is gone (Import/New Folder moved into the File menu), so the filter box and
  the tab strips sit directly under the title bar at full width.
- KEY GOTCHA: `AppWindow.TitleBar.ExtendsContentIntoTitleBar = true` alone gives NO drag
  region on a WinUI 3 window - the window renders right but cannot be dragged. The
  Window-level `ExtendsContentIntoTitleBar = true` is what installs the fallback drag region
  across the top strip. Interactive controls are then carved out via
  `InputNonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, ...)`,
  recomputed on Loaded/SizeChanged of the bar and each interactive block (physical px:
  multiply XAML bounds by XamlRoot.RasterizationScale). LeftInset/RightInset (divided by
  scale) pad the row's outer columns so content clears the caption buttons.
- Caption buttons ignore XAML theming: ApplyTitleBarButtonColors() sets transparent
  backgrounds + theme-matched foregrounds and is re-run from ApplySettingsToApp() on theme
  change. Win10 guard: if AppWindowTitleBar.IsCustomizationSupported() is false the row
  stays a plain toolbar under the stock title bar.
- Quick connect semantics: plain text searches saved sessions (SessionSearch.Rank, top 8
  as suggestions; Enter = connect best match, same as the filter box). "user@host[:port]"
  or an explicit "ssh " prefix adds a "Connect to ..." suggestion that builds an ad-hoc
  Session (not saved; username defaults to the local user) and opens it through the normal
  ConnectSession path, which already tolerates not-in-store sessions. A bare hostname only
  counts as a target with the "ssh " prefix so single words keep meaning "search".
- Verified live (screenshots + synthetic input): drag moves the window, passthrough clicks
  focus the quick connect box, saved + ad-hoc suggestions render, Enter connected
  local-test end-to-end, pin toggles WS_EX_TOPMOST both ways, File menu opens. Not yet
  human-verified: Ctrl+K (accelerator only reaches XAML focus, not the terminal WebView2),
  MenuBar Alt-key navigation, light theme rendering.
- Post-ship fixes from first real use: (1) drag region died right after Split Right and
  came back "after a while" - the fallback caption region is disturbed transiently when the
  second group's WebView2 enters the tree. Fix: stop relying on the fallback; set an
  explicit Caption region rect for the full title bar strip in UpdateTitleBarRegions
  (passthrough rects win where they overlap) and re-assert on window Activated plus a
  low-priority dispatch after SplitRight/CollapseGroupIfEmpty. Verified: drag works
  immediately after splitting. (2) Split view showed two equally-"active" tabs (each
  group's SelectedTab got the accent). TabViewModel.IsGroupFocused now gates the accent
  and full-bright text to the focused group's selected tab; the other group's selected tab
  keeps the terminal background but no accent + mid-gray text (VS Code treatment). Synced
  from MainViewModel.FocusedGroup setter, Connect, and MoveTabBetweenGroups (explicitly,
  since the setter no-ops when the target group already had focus). Verified pixel-exact:
  exactly one accent run in the strip, flipping groups on click.

## 2026-08-16 - Recursive tab-group splits
- The fixed two-column group grid is replaced by a recursive split tree. A branch contains
  equal-size column or row children, and a leaf owns one TabGroupView. Splitting a leaf in
  the same direction as its parent adds a sibling to that branch, so repeated column or row
  creation gives every sibling one equal star share.
- Dragging a tab over terminal content resolves the nearest edge: left/right create columns,
  and top/bottom create rows. The translucent VS Code-style rectangle covers the half that
  the new group will occupy. Dropping on a tab strip still moves or reorders without a split.
- Empty leaves are removed from the split tree. One-child branches collapse, and adjacent
  branches with the same orientation flatten. Live TerminalTabView instances are detached
  and re-parented during layout rebuilds; their SSH sessions and scrollback remain owned by
  the tab rather than the layout container.
- Verified by the Release build and focused split-tree tests. Live multi-row dragging and
  WebView2 re-parenting have not yet been checked in a running app.

## 2026-08-16 - Local terminal profiles (6.1)
- **Tagged model via a `kind` discriminator, not a new Profile type.** `Session` gains
  `kind` (`ssh`/`local`), an optional `local` target (executable, argument list, starting
  directory, env overrides), and `builtIn`. Pre-6.1 JSON deserializes unchanged (missing
  kind → ssh), which satisfies "records with no target kind are SSH" with zero migration
  code; the existing host/port/username fields serve as the inline SSH target. Local
  folders are a separate namespace (`localFolders` in sessions.json) rooted under the
  virtual Local tree node, so an SSH folder and a local folder may share a name. The
  Local root itself is never serialized; tree expansion keys for the local scope get a
  NUL-prefixed key ("\0local\0<path>") that no user folder path can collide with.
- **Backend contract.** `ITerminalBackend` (output event, Write/Resize/Stop) is the
  post-connect surface shared by `SshTerminalSession` and the new ConPTY-backed
  `LocalTerminalSession`; `SessionCapabilities.For(session)` drives menu naming
  (Disconnect/Stop, Reconnect/Restart) and hides remote-only UI (SFTP pane, host keys,
  tmux) so the UI has one switch point instead of scattered kind checks.
- **ConPTY std-handle gotcha (load-bearing).** With only the pseudoconsole attribute,
  CreateProcess *duplicates the parent's redirected std handles* into a console child
  (bInheritHandles=FALSE notwithstanding), and the child's output bypasses the pty —
  observed under the xunit host: the pty delivered only init/teardown VT sequences while
  `cmd /c echo` printed to the test console. Fix, same as Windows Terminal: set
  STARTF_USESTDHANDLES with null std handles so the client opens its console's handles.
- **Flush-before-close.** conhost renders a fast-exiting client's output asynchronously;
  closing the pseudoconsole immediately on process exit dropped the final line. The exit
  watcher waits for the output stream to go quiet (200 ms quiet, 2 s cap), then closes the
  console (EOFs the reader), drains, and only then raises Exited — so the neutral
  "exited (code N)" notice always lands after the process's last output.
- **No orphans.** The child starts CREATE_SUSPENDED, is assigned to a kill-on-close Job
  Object, then resumes; Stop/Dispose terminates the job, so the entire descendant tree
  dies with the tab. Verified by test (interactive cmd killed, no Exited event on
  user-initiated stop).
- **Discovery.** pwsh (Program Files/PATH), Windows PowerShell, cmd, WSL distros (Lxss
  registry, no wsl.exe spawn), Git Bash (GitForWindows registry). Ids are MD5-derived
  stable GUIDs of the discovery key ("sessions-local:cmd"), so pins survive restarts;
  sync adds missing built-ins but never overwrites user edits, and built-ins whose shell
  disappeared are hidden (App.AvailableLocalShells), not deleted. Verified live: two app
  restarts against the real sessions.json — 11 SSH records untouched, 3 built-ins added
  once, ids identical across runs.
- **Post-ship fix (same day, user-reported): blank terminal after closing the active tab.**
  Closing the selected tab left the surviving tab's terminal invisible (WebView2 host HWND
  0x0) even though the tab strip showed it selected. Root cause (trace-proven): when the
  selected item is removed, TabView auto-selects a neighbor and raises SelectionChanged
  BEFORE the TwoWay x:Bind writes SelectedTab back to the view model — the handler's
  SyncTerminalVisibility read the stale (null) VM value and collapsed every terminal, and
  nothing re-ran when the write-back landed. Fix: TabGroupView also subscribes to the
  group VM's SelectedTab PropertyChanged and re-syncs visibility/status/focus there.
  Surfaced by 6.1 only by coincidence (ssh + local, close local); the race was
  kind-agnostic. Verified by the hands-off UI rig: `Sessions.App.exe --open <name>`
  (new launch arg) + UIA-only invokes + PrintWindow(PW_RENDERFULLCONTENT) screenshots —
  no synthetic keyboard/mouse, safe to run while the user works. UIA gotcha: the window
  caption's X is also a Button named "Close"; scope dialog-button searches by position.

## 2026-08-16 - Export, import, and backup (Phase 4)
- A plain `.sessionsbackup` is a versioned ZIP with `manifest.json`, the session/folder
  snapshot, settings, known hosts, highlight state, PNG/SVG custom icons, and
  `workspaces.json` when present. Recordings remain outside the archive. A folder-scoped
  export limits only the session tree; global settings and shared assets stay complete.
- Secrets remain excluded by default. Including them puts `secrets.json` inside an
  AES-256-GCM envelope authenticated with a fixed format marker. The 256-bit key comes
  from PBKDF2-SHA256 with a random 128-bit salt and 600,000 iterations; the nonce is
  random per export. No plaintext temporary archive is written to disk.
- Import matches by session id first, then SSH host, port, and username. Each conflict has
  Keep existing, Replace, or Keep both. Replace preserves the destination id; Keep both
  generates a new id, which also maps imported secrets and pinned-session settings.
- Known hosts merge only when an endpoint is absent. An import cannot replace a different
  trusted host key. Highlight deltas and custom rules merge by rule id; imported settings
  replace app settings, with invalid pin/default references removed.
- Archive reads limit entry count, per-entry size, and total expanded size. Custom icon
  paths must be one PNG/SVG filename. Secret-bearing archives are rejected unless the
  complete file uses the authenticated encryption envelope.
