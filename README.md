# resesh

A tabbed SSH and local-terminal client for Windows, built to replace SecureCRT for daily use:
a folder tree of saved sessions, fast search, tabbed native terminals, paired asciicast
and timestamped plain-text recording with instant rewind, `.cast` playback, and import from
SecureCRT, PuTTY, and OpenSSH `~/.ssh/config`.

See [ROADMAP.md](ROADMAP.md) for planned work and [DECISIONS.md](DECISIONS.md) for
version-specific findings and design decisions.

## Stack

- WinUI 3 (Windows App SDK 2.4.0), C#, .NET 10
- SSH.NET for transport
- A native terminal: [libghostty-vt](https://github.com/ghostty-org/ghostty) for parsing, drawn
  with Direct2D/DirectWrite into a XAML `SwapChainPanel`
- Unpackaged deployment; release builds are self-contained

## Build & run

Requires the .NET 10 SDK on Windows 10 21H2 or later, and Git. Build the terminal's native
libraries once per architecture first (the script downloads the pinned Zig and Ghostty commit;
`-BuildRoot` holds about 2 GB of toolchain and caches):

```
.\eng\build-ghostty-vt.ps1 -Architecture x64
dotnet build src/App/Resesh.App.csproj -p:Platform=x64
src/App/bin/x64/Debug/net10.0-windows10.0.19041.0/Resesh.App.exe
```

On ARM64, substitute `-Architecture arm64` and `-p:Platform=ARM64`. Rerun the script when
`eng/ghostty-vt.json` or `src/Terminal/Native/Shim/` changes.

### Screenshot demo

Launch an isolated demo with a populated session tree:

```powershell
.\demo.ps1
```

The script builds the app for the current processor architecture and starts it with
disposable demo data. It does not read or change the normal resesh sessions, settings, or
credentials. Use `.\demo.ps1 -Platform ARM64` to override the detected architecture.

## Releases

GitHub Actions builds and tests each push to `master` and each pull request. Each build produces,
for x64 and Arm64:

- a `-setup.exe` bundle — the recommended download. The app is self-contained (.NET 10 and the
  Windows App Runtime ship inside it), and the bundle installs the one remaining prerequisite,
  the Visual C++ Runtime, only when the computer lacks it.
- a bare `.msi` for scripted deployments. It assumes the Visual C++ Runtime is already present.
- a portable ZIP with the same assumption as the MSI.

The installers install for all users, so Windows requests administrator approval.

To create a GitHub release, push a numeric semantic-version tag such as `v1.2.3`. Tagged builds
sign the app binaries, the MSI, and the setup bundle with the Certum code-signing certificate
(the `code-signing` environment), then publish all six files with SHA-256 checksums. Branch and
pull-request builds are unsigned, so Windows shows an unknown-publisher warning for them.

## Tests

```
dotnet test tests/Core.Tests
dotnet test tests/AppLogic.Tests
dotnet test tests/Terminal.Tests -p:Platform=x64
node --test tests/*.js
```

The app-logic tests execute the production view models without a WinUI window. They cover
live tab ownership and cleanup. Core tests cover connection cancellation, failed writes,
host-key recovery, and exact workspace pane measurements. JavaScript source checks remain
useful for UI wiring; they do not replace interactive UI tests.

Open `tests/Fixtures/Terminal/webgl-resize.html` in a WebGL2-capable browser for the
GPU canvas-resize regression. It checks rendered pixels and reports PASS/FAIL.

## Persistent sessions

Right-click a persistent SSH tab and choose **Manage Remote Sessions…** to list the
shells belonging to that saved connection, including when the tab is disconnected.
The same action is available from the **Session** menu and the command palette for the active tab.
The manager lists each shell by its current folder and foreground program, with its age and
whether it is detached, open in a tab, or attached somewhere else. Select a shell to **Resume**
(or double-click it), **End Session…** to terminate it after confirmation, or
**End All Detached…** to clear out every shell that no tab or other client is using.

Closing a terminal tab only detaches its persistent shell; the close dialog, including
**Close Others** and **Close All**, offers to end the sessions instead. What a new tab does
with shells that are still running is set per connection in **Session Settings › Terminal ›
When earlier shells are running**:

- **Resume it, or ask when there are several** (default) — a single detached shell resumes
  directly; otherwise a picker offers each shell, a new session, and
  an **End detached shells and start new** button.
- **Start a new shell, keep the others.**
- **End detached shells, then start a new one.**

Shells open in another tab or attached from another client are never ended automatically
and never resumed without asking.

## Optional shell integration (experimental)

Shell integration adds prompt and command boundaries, exit results, and current-folder
reports to the existing command marks and file pane. It is **off by default for every
session**, including existing profiles and imported connections.

- For a local profile, turn on **Enable shell integration (experimental)** in its editor.
  Recognized Bash, Zsh, Fish, and PowerShell executables are supported. WSL, Command Prompt,
  and custom command/script launches keep their ordinary startup and show a skip notice.
- For SSH, select **Shell integration (experimental)** in the session's **Connection**
  settings: Bash, Zsh, Fish, or PowerShell (Windows). Choose the shell explicitly; keep
  **Off** for Cisco, Juniper, and other network CLIs. There is no platform discovery probe.

Changes apply to new shells. A resumed persistent shell keeps its original hooks; start a
new remote session to apply a changed preference. PowerShell integration cannot be combined
with tmux persistence. PowerShell's effective execution policy still applies and may block
the script; resesh shows a short notice and leaves the shell usable. To permit locally
created scripts for a particular local PowerShell profile, append `-ExecutionPolicy` and
`RemoteSigned` on separate lines in its Arguments box. This affects that terminal process
and its children; resesh does not change saved user or machine execution policies.

Bundled scripts load after the user's startup configuration without editing profile files.
SSH setup uses a separate, bounded exec channel and stores the scripts in the remote user's
cache. Rejected setup falls back to ordinary startup when the connection remains usable.
See [the implementation and test notes](SHELL_INTEGRATION_PLAN.md) for shell coverage and
current limitations.

## Notify when a command finishes

While a command with shell integration is running, click the bell beside its tab subtitle
to request one completion notification. The command palette offers the same action for
the active tab. Click again to cancel. Each request applies only to that command;
notifications are not automatically armed for later commands.

If you are watching that terminal when it finishes, resesh stays quiet and keeps the
program name, elapsed time, and exit result in the subtitle tooltip. Otherwise it sends
a Windows notification; clicking it returns to the tab and the command's output when it
remains in scrollback. Command
arguments are not included. An unavailable exit status is reported as unknown, not success.
Disconnecting or starting another command cancels an outstanding request. This requires
shell integration's command lifecycle reports; there are no automatic notification rules.
If Windows notifications are unavailable, resesh shows an in-app completion notice and
flashes the taskbar instead.

Notification clicks target the live originating app; they never reopen a closed session.
Clicks may not route when separate app processes with different `--data-dir` values run
concurrently. Multiple windows within one app process are supported.

## Command history

Turn on **Settings → Recording → Command history** (or the button in the history view) to
keep every finished command with its output, folder, session, time, and exit result.
Press Ctrl+Shift+H, or use View → Command History, to search it across all sessions.

- Words must all match the command, session, folder, or output. Quote a phrase with
  `"…"`. Narrow with `host:web01`, `in:/etc`, `exit:fail`, `exit:ok`, or `exit:127`, or
  use the session, result, and time filters.
- Enter types the selected command at the active tab's prompt without running it.
  Ctrl+Enter opens its session in a new tab. F3 and Shift+F3 step through matches in the
  output.
- **Compare runs:** when a command has run more than once on the same host, the detail
  pane offers "Compare with Previous" and a list of every run with how it differs. The
  comparison shows added and removed lines with changed words marked, unified or side by
  side. Timestamps are ignored by default; numbers can be ignored too. F7 and Shift+F7 step
  between changes. Runs that went through a pager (`--More--`) or were cut at 64 KB are
  flagged, because their output is incomplete.
- Commands are found from shell integration (with exit codes) or from shell prompts
  (without). Output keeps its first 64 KB; output that leaves the scrollback before the
  command ends is not kept.
- History is off by default because output can contain secrets. A saved session can turn
  it off in its options. Entries older than the retention period (90 days by default)
  are deleted at startup; Settings can clear all history, and the view deletes single
  entries. Deleted history is overwritten on disk, not just unlinked. History is local
  only and is not included in backups.
- Search uses a SQLite full-text index, so history does not have to fit in memory. Words
  of three or more characters use the index; shorter words are matched by a scan.

## Data locations

| What | Where |
| --- | --- |
| Sessions + folders | `%APPDATA%\Resesh\sessions.json` (atomic writes, one `.bak` rotation) |
| Workspaces + clean-exit layout | `%APPDATA%\Resesh\workspaces.json` (atomic writes, one `.bak` rotation) |
| Secrets (passwords, key passphrases) | Windows Credential Manager, `Resesh:{session-guid}` |
| Accepted host keys | `%APPDATA%\Resesh\known_hosts.json` |
| Exported backups | User-selected `*.reseshbackup` file |
| Command history (when on) | `%LOCALAPPDATA%\Resesh\history\history.db` (SQLite with a full-text index; deletes overwrite the removed data) |
| Crash log | `%LOCALAPPDATA%\Resesh\crash.log` |

Secrets are **never** written to the normal JSON store. They are excluded from backups by
default. When included, the complete backup is encrypted with its passphrase.

## Project layout

```
src/App               WinUI 3 app (views, viewmodels, dialogs)
src/Core              models, session store, importers, SSH/SFTP, shell integration,
                      credential service — no UI dependencies
src/Terminal          the terminal surface (libghostty-vt shim, renderer, ruler, find)
tests/Core.Tests      core unit tests
tests/AppLogic.Tests  view-model tests without a WinUI window
tests/*.js            JavaScript source checks (run with `node --test`)
installer/            WiX MSI and setup bundle
eng/                  MSBuild targets, patches and the libghostty-vt build
tools/                TestSshServer, KeepaliveProbe and terminal benchmarks
website/              project website
```

## Local test SSH server

A throwaway echo server for exercising the terminal end to end (password auth, host keys,
10 MB dumps, window-change, server-side close):

```
dotnet run --project tools/TestSshServer
```

Listens on `127.0.0.1:2200`, accepts `test` / `test123`. The seeded session **Lab/local-test**
connects to it. Commands: `big` (10 MB dump), `bye` (server-side close); anything else echoes.

## License

[GPL-2.0](LICENSE)
