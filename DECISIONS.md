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
