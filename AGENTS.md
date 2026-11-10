# AGENTS.md

Guidance for coding agents working in this repository. Read [README.md](README.md) for what
resesh is and how users see it; this file covers how to work on it.

## Project

resesh is a tabbed SSH and local-terminal client for Windows: WinUI 3 (Windows App SDK 2.4),
C#, .NET 10, unpackaged, shipped as Native AOT. SSH runs on SSH.NET; the terminal is
libghostty-vt drawn with Direct2D/DirectWrite into a `SwapChainPanel`.

| Path | Contents |
| --- | --- |
| `src/Core` | UI-free logic: SSH, SFTP, Telnet, local shells, storage, import, shell integration, agents |
| `src/Terminal` | Terminal surface over libghostty-vt; native shim in `src/Terminal/Native/Shim` |
| `src/App` | WinUI app. `MainWindow` is a partial class split across `MainWindow.*.cs` |
| `tests/Core.Tests`, `tests/AppLogic.Tests`, `tests/Terminal.Tests` | xUnit tests |
| `tests/*.js` | Node source checks over XAML and C# (UI wiring) |
| `tools/` | Benchmarks, probes and a test SSH server; not shipped |
| `eng/` | Native build scripts and pins (`ghostty-vt.json`, session agent) |
| `installer/` | WiX MSI and setup bundle |
| `website/` | Static marketing site |

## Build and test

Everything is Windows-only. Building the Terminal project runs `eng/build-ghostty-vt.ps1` itself
when the native libraries are missing or `eng/ghostty-vt.json`, the script or
`src/Terminal/Native/Shim/` changed (a content hash in `.artifacts/ghostty-vt/<arch>/inputs.sha256`).
It is skipped on CI, which caches the DLLs, and with `-p:SkipGhosttyVtBuild=true`.

```powershell
dotnet build src/App/Resesh.App.csproj -p:Platform=x64

dotnet test tests/Core.Tests
dotnet test tests/AppLogic.Tests
dotnet test tests/Terminal.Tests -p:Platform=x64
node --test tests/*.js
```

Run the tests that cover what you changed before calling work done; CI (`.github/workflows/
build-release.yml`) runs all four suites on every push and pull request.

**In-app checks must never take over the user's screen.** The user keeps working while you
test. Launch with `--background` (windows open behind everything and never take focus) and
drive the app only with input that needs no focus: `winapp ui invoke`, `set-value`,
`get-value`, `inspect`, `wait-for`, `send-keys` with the default post-message transport, and
`winapp ui screenshot` or `tools/screenshot-rig`'s `PrintWindow` capture. Do not use `click`,
`hover`, `drag`, `touch`, `pen`, `scroll --wheel`, `send-keys --via send-input`, or
`focus`: they move the real pointer or need the window in front. Posted keys never fire
keyboard accelerators, so reach commands through menus or the command palette. If a check
can only be done with real input (drag-and-drop, hover, true focus behaviour), say so and ask
the user to do it, rather than taking the screen.

To launch the app for manual checks without touching the user's real sessions, settings,
credentials or logs, use `.\demo.ps1` or pass `--data-dir <scratch folder>` to the built exe
(its passwords are kept DPAPI-encrypted in that folder, not in Credential Manager).

## Conventions

- **Native AOT.** Release builds are AOT-compiled and trimmed. Avoid reflection; heed trimming
  and AOT analyzer warnings rather than suppressing them without a reason in a comment.
- **XAML bindings** use `x:Bind`, not `{Binding}`. Types shown through `DisplayMemberPath` or
  other runtime binding need `[GeneratedBindableCustomProperty]`.
- **Dispatcher callbacks** go through `TryPost` and `OnTick` (`DispatcherQueueReporting`), not
  `DispatcherQueue.TryEnqueue` or `DispatcherQueueTimer.Tick`, which the build bans: WinUI
  fails fast on an exception there without raising any unhandled-exception event, so only
  the wrapper gets it into crash.log.
- **Persisted JSON** goes through source-generated metadata in
  `src/Core/Storage/CoreJson.cs`. Register new persisted types on `CoreJsonContext`, add new
  enums to the camel-case enum converter's list, and use settable (not init-only) properties
  wherever an initializer matters. `CoreJsonTests` checks this.
- **Stores** never save over a file they could not read, and publish changes only after the
  write succeeds.
- **Theming**: no hard-coded colors in XAML. Define each color once as a keyed brush (in
  `App.xaml`'s theme dictionaries, or a local resource that code-behind recolors) and refer to
  it by name; `Transparent` is fine. Check Light, Dark and High Contrast.
  `tests/XamlColorLiteralTests.js` enforces this.
- **View models** hold logic that can be tested without a window (`AppLogic.Tests` runs them
  headless). Keep WinUI types out of `src/Core`.
- Match the surrounding code's comment density and naming. XML doc comments explain *why*.
- Cut taglines, helper text and secondary labels. If a screen needs them to make sense, fix the layout.

## Documentation

- [DECISIONS.md](DECISIONS.md): append a dated `## YYYY-MM-DD - Title` entry for any
  version-specific finding, workaround, or deliberate design choice a future reader would
  otherwise rediscover the hard way.
- [ROADMAP.md](ROADMAP.md): move items to **Shipped** when they land.
- README and `website/` describe user-visible features; update them when behavior changes.
- Release notes live in `.github/release-notes/vX.Y.Z.md`.

## Git

- Work on a branch and open a pull request against `master`.
- Commit subjects are short, imperative, plain English describing the effect
  ("Open SSH sessions faster", "Stop terminals flashing black when panes resize"), with no
  type prefixes. The body explains what was wrong and why the change fixes it.
- Don't commit build output, `.artifacts/`, publish profiles, or `*.user` files.
