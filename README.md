# Resesh

A tabbed SSH client for Windows, built to replace SecureCRT for daily use: a folder tree of saved
sessions with fast search on top, tabbed xterm.js terminals, and one-time import of existing
SecureCRT sessions.

See [ROADMAP.md](ROADMAP.md) for planned work and [DECISIONS.md](DECISIONS.md) for
version-specific findings and design decisions.

## Stack

- WinUI 3 (Windows App SDK 2.4.0), C#, .NET 8
- SSH.NET for transport
- WebView2 + xterm.js (bundled) as the terminal surface
- Unpackaged deployment; release builds are self-contained

## Build & run

Requires the .NET SDK (8+) on Windows 10 21H2 or later.

```
dotnet build src/App/Resesh.App.csproj -p:Platform=x64
src/App/bin/x64/Debug/net8.0-windows10.0.19041.0/Resesh.App.exe
```

On ARM64, substitute `-p:Platform=ARM64`.

## Releases

GitHub Actions builds and tests each push and pull request. Each build produces, for x64 and Arm64:

- a `-setup.exe` bundle — the recommended download. The app is self-contained (.NET 8 and the
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
```

## Data locations

| What | Where |
| --- | --- |
| Sessions + folders | `%APPDATA%\Resesh\sessions.json` (atomic writes, one `.bak` rotation) |
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
