# Project Plan — Windows SSH Client v1 (working name: "Sessions")

A tabbed SSH client for Windows replacing SecureCRT for daily use. Core value proposition: a folder tree of saved sessions with fast search at the top, tabbed terminals, and one-time import of existing SecureCRT sessions.

## Stack (fixed — do not substitute)

- **Shell/UI:** WinUI 3 (Windows App SDK, latest stable), C#, .NET 8+
- **SSH transport:** SSH.NET (Renci.SshNet, latest stable from NuGet)
- **Terminal surface:** WebView2 hosting xterm.js (latest stable, bundled locally — no CDN at runtime)
- **Packaging:** unpackaged (self-contained) for v1; MSIX later
- **Target:** Windows 10 21H2+ and Windows 11, x64 and ARM64

## Architecture

One WinUI 3 window:

```
+--------------------------------------------------------------+
| [AutoSuggestBox: search sessions]                    _ □ X   |
+----------------+---------------------------------------------+
| TreeView       |  TabView                                    |
|  ▸ Datacenter  |   [prod-web-01] [core-sw-3] [+]             |
|  ▾ Branch      |  +---------------------------------------+  |
|     core-sw-3  |  | WebView2 (xterm.js)                   |  |
|     edge-rtr-1 |  |                                       |  |
+----------------+--+---------------------------------------+--+
| status bar: connection state, encryption, host key fp        |
+--------------------------------------------------------------+
```

Each tab owns: one `SshClient` + `ShellStream` (background), one WebView2 instance running a local xterm.js page, and a bridge marshalling bytes between them.

### Data flow (terminal bridge)

- SSH → UI: read bytes from `ShellStream` on a background task; forward to WebView2 via `CoreWebView2.PostWebMessageAsJson` with base64-encoded payloads (binary-safe). In JS, decode and `term.write(bytes)`.
- UI → SSH: xterm.js `onData` → `window.chrome.webview.postMessage` → C# handler writes to `ShellStream`.
- Batch SSH→UI writes (e.g., flush every 8–16 ms or 32 KB, whichever first) to avoid drowning the message channel during large outputs like `cat` of a big file.
- Resize: on xterm.js `fit` (use the fit addon on container resize), post cols/rows to C#. **Known risk:** SSH.NET's `ShellStream` may not expose a public window-change (resize) request. Verify against the current SSH.NET version. If absent, acceptable v1 workarounds in order of preference: (1) call the library's internal channel `SendWindowChangeRequest` via a thin reflection helper with a unit test pinning the member name; (2) create the shell only after first layout so initial size is correct, and document that live resize reflows only client-side. Do not fork SSH.NET in v1.

### Session model & storage

`Session` record: id (GUID), name, folder path (e.g. `Datacenter/Rack 4`), host, port (default 22), username, auth method (`password | privateKey | agentLike-none`), private key file path + passphrase-required flag, terminal type (`xterm-256color` default), notes, optional per-session color tag.

- Store sessions as a single JSON file: `%APPDATA%\Sessions\sessions.json`, written atomically (write temp + rename). Watch for corruption: keep one `.bak` rotation.
- **No secrets in JSON.** Passwords and key passphrases go to Windows Credential Manager (`CredWrite`/`CredRead` via P/Invoke or the `Meziantou.Framework.Win32.CredentialManager` package), keyed by session GUID. Deleting a session deletes its credential.
- Folder tree is derived from the folder path strings; support drag-and-drop of sessions between folders in the tree, and folder create/rename/delete.

### Search

`AutoSuggestBox` above the tree. Filters as you type across name, host, username, folder path, and notes (case-insensitive substring; simple fuzzy is a stretch goal). Enter on the top result connects it in a new tab. Filtering collapses the tree to matching nodes with their ancestor folders expanded.

### Host key handling

On first connect, show fingerprint (SHA256) in a dialog with accept/reject; persist accepted keys in `%APPDATA%\Sessions\known_hosts.json` keyed by host:port. On mismatch, hard-fail with a clear warning dialog (no "connect anyway" button in v1).

## SecureCRT import

File → "Import from SecureCRT…".

- Default location to scan: `%APPDATA%\VanDyke\Config\Sessions\` (let the user browse to a different Config folder — some installs relocate it). Recurse subdirectories; the directory structure **is** the folder tree — mirror it.
- Each session is an `.ini` file. Lines look like `S:"Hostname"=10.0.0.1`, `D:"[SSH2] Port"=00000016` (D: values are 8-digit hex), `S:"Username"=admin`, `S:"Protocol Name"=SSH2`. Parse defensively: unknown keys are ignored; missing port → 22.
- Import: hostname, port, username, protocol (import only SSH2/SSH1 sessions; list skipped Telnet/serial sessions in the summary), session name (filename without `.ini`), folder path (relative directory). Skip `__FolderData__.ini` and `Default.ini`.
- **Passwords are intentionally not imported.** SecureCRT stores them encrypted; do not implement decryption. Imported sessions are marked "credential needed" and prompt on first connect, offering to save to Credential Manager.
- Show a preview dialog before committing: table of sessions found, target folder, checkboxes to include/exclude, duplicate handling (skip if same name+host+port already exists).
- Ship at least 6 fixture `.ini` files (SSH2 with/without port, SSH1, Telnet, folder nesting, weird characters in names) and unit-test the parser against them.

## Terminal features (v1 scope)

Included: xterm-256color emulation via xterm.js; true color; scrollback 10,000 lines; copy on select (toggleable) and right-click paste (SecureCRT muscle memory — make both defaults match SecureCRT: copy-on-select ON, right-click-paste ON); Ctrl+Shift+C/V as well; clickable URLs (web-links addon); font family/size setting (global); a dark theme and a light theme; per-session color tag shown on the tab; keepalive (SSH.NET `KeepAliveInterval` 30 s); clean disconnect notice rendered in the terminal with a "Reconnect" affordance (Enter or button); tab context menu — see "Tab context menu" below.

## Tab context menu (v1 scope)

Right-click on a tab opens a `MenuFlyout` modeled on SecureCRT (order and separators as listed):

- **Rename** — inline-edit the tab title (display-only override; does not rename the saved session; cleared by "Reset Name").
- **Reset Name** — revert to the session name (disabled when no override set).
- **Reconnect** — enabled only when disconnected; reconnects in place, preserving scrollback with a visual divider line.
- **Disconnect** — enabled only when connected; clean SSH disconnect, tab stays open showing the disconnect notice.
- *(separator)*
- **Close** (`Ctrl+F4`) — always shows an "Are you sure?" confirmation before closing, connected or not. No "don't ask again" checkbox and no settings toggle to disable it (deliberate — do not add one).
- **Close Disconnected Tabs** — closes all disconnected tabs in this group.
- **Close Other Tabs** — closes all other tabs in this group (one confirm dialog listing how many are still connected).
- **Close Tabs to the Right** — same confirm behavior.
- **Close Tab Group** — closes every tab in this group; the group collapses (see split view section).
- **Close All Tabs** — every tab in every group.
- *(separator)*
- **Lock Session…** — prompts for a lock password (not stored anywhere; held in memory, compared on unlock). While locked: input is blocked, the terminal is visually obscured (blur/overlay with a lock icon), output continues to buffer, tab shows a padlock glyph. Unlock prompts for the password; three failed attempts adds a 30-second delay. Lock state is per-tab and does not survive app restart.
- **Clone Session** — opens a new tab in the same group with a *new* SSH connection to the same session (fresh credentials from Credential Manager, no scrollback copy).
- *(separator)*
- **Split Right** (`Ctrl+Shift+\`) / **Move to Other Group** — see split view section; "Move to Other Group" replaces "Split Right" when two groups already exist.
- *(separator)*
- **Session Options…** — opens the session edit dialog for this tab's saved session (same dialog as tree edit). Changes to host/port/auth apply on next connect, not to the live connection; note this in the dialog when opened from a connected tab. Disabled for tabs whose session was deleted from the tree while connected.

Explicitly not in v1 (present in SecureCRT, do not build): Send to New Window / Clone in New Window (single-window app for v1), Connect SFTP Session, Send Commands to This Group (chat/broadcast), Font… per-tab (font is global in v1), Save Session from an ad-hoc connection.

Additional tab interactions:

- **Middle-click on a tab closes it**, through the same "Are you sure?" confirmation as Close. Implement via `PointerPressed`/`PointerReleased` on the tab item checking `IsMiddleButtonPressed` (WinUI `TabView` has no built-in middle-click close); only close if press and release happen on the same tab. Also suppress the default `TabView` close-button path so the X button routes through the same confirmed-close code — there must be exactly one close pathway (X button, Ctrl+F4, context menu, middle-click all converge on it).
- Bulk closes (Close Others / to the Right / Tab Group / All / Disconnected) show one confirmation dialog for the whole operation, stating how many tabs will close and how many of those are still connected.

Excluded from v1 (do not build): SFTP browser, port forwarding UI, scripting, button bar, logging to file, splitting a single session's terminal into panes, serial/Telnet, jump hosts, keyboard-interactive beyond simple password prompt (verify: if the server demands keyboard-interactive, answer with the stored password once — SSH.NET supports this via `KeyboardInteractiveAuthenticationMethod`).

## Split view — tab groups (v1 scope)

SecureCRT-style tab groups, not per-session terminal panes: the tab area can split into **two side-by-side groups**, each with its own `TabView` and its own set of session tabs (reference: user's current workflow has one group per topic, e.g. NOC hosts on the left, a firewall on the right).

- Layout: `Grid` with two columns hosting a `TabView` each, separated by a `GridSplitter` (CommunityToolkit.WinUI.Controls.Sizers). Splitter position persisted across restarts.
- Commands: "Split right" (tab context menu + `Ctrl+Shift+\`) moves the current tab into a new right-hand group; when the last tab in a group closes or is dragged out, the group collapses and the splitter disappears.
- Drag tabs between groups using `TabView`'s built-in tab drag/drop (`CanDragTabs`, `TabDroppedOutside`/`TabDragCompleted` handlers wired between the two TabViews). Opening a session from the tree targets the **last-focused** group.
- Focus: exactly one terminal has keyboard focus; clicking anywhere in a group's terminal or tab strip focuses that group. Status bar reflects the focused session.
- Hard limits for v1: max 2 groups, vertical split only (side by side), no nested splits. Keep the tab-group container abstracted (a `TabGroupHost` control owning N groups) so horizontal/nested splits can be added later without rework.
- Each terminal is its own WebView2 instance already (one per tab), so no additional terminal work is needed — this is purely layout/ownership. Verify WebView2 visuals survive being re-parented when a tab moves between groups; if re-parenting is flaky, move the session object (bridge + SshClient) into a freshly created WebView2 in the target group and dispose the old one, replaying the xterm.js scrollback buffer from a retained ring buffer (retain last 10,000 lines of raw output per session for this purpose).

## Milestones (each independently runnable and demoable)

**M1 — Shell.** WinUI 3 app with TreeView + TabView + AutoSuggestBox wired to a JSON-backed session store with CRUD dialogs and drag-and-drop foldering. Tabs open a placeholder page. Search filtering works. Acceptance: create/edit/delete/move sessions; restart app and everything persists; search narrows tree correctly.

**M2 — Terminal.** WebView2 + bundled xterm.js page; SSH.NET connect with password and private-key auth; bridge both directions; resize; host key dialog; credentials in Credential Manager. Acceptance: connect to a real Linux host, run `top`, `vim`, resize the window without visual corruption, paste 100 KB of text, `cat` a 10 MB file without the UI freezing, disconnect and reconnect.

**M3 — SecureCRT import.** Parser + preview dialog + fixtures/tests. Acceptance: import the fixture tree; folder structure mirrors directories; Telnet sessions listed as skipped; re-import produces zero duplicates.

**M4 — Tab groups & tab context menu.** Split right, drag between groups, group collapse, splitter persistence, focus handling, and the full tab context menu (rename, reconnect/disconnect, close variants, lock, clone, session options). Acceptance: split with two live sessions, run `top` in both simultaneously, drag a connected tab across the splitter without dropping the SSH connection or corrupting output, close all right-hand tabs and watch the split collapse, restart and confirm splitter position restored; additionally — lock a session, verify keystrokes don't reach the host and output is obscured, unlock and see buffered output; clone a connected session and get an independent second connection; "Close Disconnected Tabs" leaves connected tabs untouched; middle-clicking a tab prompts and closes it; every close path (X, Ctrl+F4, menu, middle-click) shows the confirmation, including on disconnected tabs.

**M5 — Polish.** Themes, settings page (font, scrollback, copy/paste toggles), keepalive/reconnect UX, status bar, error dialogs with actionable text (auth failed vs. unreachable vs. host key mismatch), app icon, first-run empty state that points at the import feature.

## Project layout

```
/src/App            WinUI 3 project (views, viewmodels — MVVM, CommunityToolkit.Mvvm)
/src/Core           session store, models, SecureCRT importer, credential service (no UI deps)
/src/Terminal       WebView2 host control, bridge, /wwwroot with xterm.js assets
/tests/Core.Tests   xUnit: importer fixtures, session store atomicity, search filter
```

## Engineering notes for the agent

- Keep `Core` free of WinUI references so the importer and store are unit-testable.
- All SSH I/O off the UI thread; marshal to UI via `DispatcherQueue`.
- WebView2: use a fixed user-data folder under `%LOCALAPPDATA%\Sessions\WebView2`; disable default context menu, dev tools off in release, `Ctrl+Shift+I` enables them in debug builds.
- Serve the xterm.js page via `SetVirtualHostNameToFolderMapping` (e.g. `https://app.local/terminal.html`), not `file://`.
- Dispose order on tab close: stop reader task → dispose ShellStream → disconnect/dispose SshClient → close WebView2. Guard against double-dispose; closing the app with 10 live tabs must not hang.
- Pin package versions in the csproj; note any version-specific SSH.NET findings (especially resize) in a `DECISIONS.md`.
- Definition of done per milestone: acceptance criteria pass, tests green, no compiler warnings, README updated with build/run steps.
