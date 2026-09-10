# resesh

A tabbed SSH and local-terminal client for Windows, built to replace SecureCRT for daily use:
a folder tree of saved sessions, fast search, tabbed xterm.js terminals, paired asciicast
and timestamped plain-text recording with instant rewind, `.cast` playback, and SecureCRT import.

See [ROADMAP.md](ROADMAP.md) for planned work and [DECISIONS.md](DECISIONS.md) for
version-specific findings and design decisions.

## Stack

- WinUI 3 (Windows App SDK 2.4.0), C#, .NET 10
- SSH.NET for transport
- WebView2 + xterm.js (bundled) as the terminal surface
- Unpackaged deployment; release builds are self-contained

## Build & run

Requires the .NET 10 SDK on Windows 10 21H2 or later.

```
dotnet build src/App/Resesh.App.csproj -p:Platform=x64
src/App/bin/x64/Debug/net10.0-windows10.0.19041.0/Resesh.App.exe
```

On ARM64, substitute `-p:Platform=ARM64`.

### Screenshot demo

Launch an isolated demo with a populated session tree:

```powershell
.\demo.ps1
```

The script builds the app for the current processor architecture and starts it with
disposable demo data. It does not read or change the normal resesh sessions, settings, or
credentials. Use `.\demo.ps1 -Platform ARM64` to override the detected architecture.

## Releases

GitHub Actions builds and tests each push and pull request. Each build produces, for x64 and Arm64:

- a `-setup.exe` bundle — the recommended download. The app is self-contained (.NET 10 and the
  Windows App Runtime ship inside it), and the bundle installs the two remaining prerequisites,
  the Visual C++ Runtime and the WebView2 Runtime, only when the computer lacks them.
- a bare `.msi` for scripted deployments. It assumes the Visual C++ and WebView2 runtimes are
  already present.
- a portable ZIP with the same assumption as the MSI.

The installers are unsigned and install for all users, so Windows requests administrator approval
and shows an unknown-publisher warning.
To create a GitHub release with all six files and SHA-256 checksums, push a numeric
semantic-version tag such as `v1.2.3`.

## Tests

```
dotnet test tests/Core.Tests
dotnet test tests/AppLogic.Tests
dotnet test tests/Terminal.Tests -p:Platform=x64
```

The app-logic tests execute the production view models without a WinUI window. They cover
live tab ownership and cleanup. Core tests cover connection cancellation, failed writes,
host-key recovery, and exact workspace pane measurements. JavaScript source checks remain
useful for UI wiring; they do not replace interactive UI tests.

## Persistent sessions

Right-click a persistent SSH tab and choose **Manage Remote Sessions…** to list the
shells belonging to that saved connection, including when the tab is disconnected.
The same action is available from the **Session** menu and the command palette for the active tab.
The manager shows session age, foreground process, current folder, and attached-client
count. Select a shell to **Resume**, or **End Session…** to terminate it after confirmation.
**Refresh** updates the list. Closing a terminal tab still only detaches its persistent shell.

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

## Data locations

| What | Where |
| --- | --- |
| Sessions + folders | `%APPDATA%\Resesh\sessions.json` (atomic writes, one `.bak` rotation) |
| Workspaces + clean-exit layout | `%APPDATA%\Resesh\workspaces.json` (atomic writes, one `.bak` rotation) |
| Secrets (passwords, key passphrases) | Windows Credential Manager, `Resesh:{session-guid}` |
| Accepted host keys | `%APPDATA%\Resesh\known_hosts.json` |
| Exported backups | User-selected `*.reseshbackup` file |
| Crash log | `%LOCALAPPDATA%\Resesh\crash.log` |

Secrets are **never** written to the normal JSON store. They are excluded from backups by
default. When included, the complete backup is encrypted with its passphrase.

## Project layout

```
src/App        WinUI 3 app (views, viewmodels)
src/Core       models, session store, importer, credential service — no UI dependencies
src/Terminal   WebView2 host + xterm.js assets
tests/Core.Tests
```

## Local test SSH server

A throwaway echo server for exercising the terminal end to end (password auth, host keys,
10 MB dumps, window-change, server-side close):

```
dotnet run --project tools/TestSshServer
```

Listens on `127.0.0.1:2200`, accepts `test` / `test123`. The seeded session **Lab/local-test**
connects to it. Commands: `big` (10 MB dump), `bye` (server-side close); anything else echoes.

## Native terminal development (WIP)

WebView2 and xterm.js remain the default for live terminals, rewind, and recording playback.
Normal builds do not enable the native renderer or include its DLL.

For native development, build the native artifacts with `eng/build-native-terminal.ps1`,
then build the app with `-p:EnableNativeTerminalWip=true` and `-p:Platform=x64`
(or `ARM64`). Set `RESESH_TERMINAL_SURFACE=native` only for the test process.
Both the build option and environment setting are required. Native snapshots remain
part of this WIP and are not used by xterm.js.

## License

[GPL-2.0](LICENSE)
