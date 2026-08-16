# Sessions

A tabbed SSH client for Windows, built to replace SecureCRT for daily use: a folder tree of saved
sessions with fast search on top, tabbed xterm.js terminals, and one-time import of existing
SecureCRT sessions.

See [ssh-client-v1-plan.md](ssh-client-v1-plan.md) for the full v1 plan and
[DECISIONS.md](DECISIONS.md) for version-specific findings.

## Stack

- WinUI 3 (Windows App SDK 2.4.0), C#, .NET 8
- SSH.NET for transport
- WebView2 + xterm.js (bundled) as the terminal surface
- Unpackaged, self-contained deployment

## Build & run

Requires the .NET SDK (8+) on Windows 10 21H2 or later.

```
dotnet build src/App/Sessions.App.csproj -p:Platform=x64
src/App/bin/x64/Debug/net8.0-windows10.0.19041.0/Sessions.App.exe
```

On ARM64, substitute `-p:Platform=ARM64`.

## Tests

```
dotnet test tests/Core.Tests
```

## Data locations

| What | Where |
| --- | --- |
| Sessions + folders | `%APPDATA%\Sessions\sessions.json` (atomic writes, one `.bak` rotation) |
| Secrets (passwords, key passphrases) | Windows Credential Manager, `Sessions:{session-guid}` |
| Accepted host keys | `%APPDATA%\Sessions\known_hosts.json` (M2) |
| Exported backups | User-selected `*.sessionsbackup` file (Phase 4) |
| Crash log | `%LOCALAPPDATA%\Sessions\crash.log` |

Secrets are **never** written to the normal JSON store. They are excluded from backups by
default. When included, the complete backup is encrypted with its passphrase.

## Project layout

```
src/App        WinUI 3 app (views, viewmodels)
src/Core       models, session store, importer, credential service — no UI dependencies
src/Terminal   WebView2 host + xterm.js assets (M2)
tests/Core.Tests
```

## Milestones

- **M1 — Shell:** tree + search + tabs + JSON store with CRUD and drag-and-drop foldering ✅
- **M2 — Terminal:** SSH.NET + WebView2/xterm.js bridge, host keys, credentials ✅
- **M3 — SecureCRT import** ✅
- **M4 — Tab groups & tab context menu** ✅
- **M5 — Polish:** themes, settings, icon, empty state ✅

Verified against a real Linux host (2026-08-14): full-screen `htop`/`vim` rendering with colors,
split view with two live sessions, ed25519 host keys. Still unverified: private-key auth and the
keyboard-interactive fallback. Everything else was exercised live against the local test server
below.

## Local test SSH server

A throwaway echo server for exercising the terminal end to end (password auth, host keys,
10 MB dumps, window-change, server-side close):

```
dotnet run --project tools/TestSshServer
```

Listens on `127.0.0.1:2200`, accepts `test` / `test123`. The seeded session **Lab/local-test**
connects to it. Commands: `big` (10 MB dump), `bye` (server-side close); anything else echoes.
