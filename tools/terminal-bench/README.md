# terminal-bench

Terminal output benchmarks: the standalone libghostty-vt + Direct2D/DirectWrite bench
(`tools/GhosttyVtBench`) and the real app on its real output paths (`app/`). The WebView2 +
xterm.js side these were first compared against was removed with that surface on 2026-10-08;
its numbers stay in `RESULTS.md`.

## Workloads

`node gen.mjs <dir>` writes `logs`, `color`, `tiny`, `tui` and `unicode` as raw `.bin` files.
The generators match the 2026-10-06 profiling rig byte for byte. Rates are reported on real UTF-8
bytes (the old rig divided by JS string length, which understated `unicode`).

## Native side

```powershell
tools\GhosttyVtBench\build-native.ps1 -Root F:\resesh-spike     # Zig 0.16.0 + pinned Ghostty
dotnet build tools\GhosttyVtBench -c Release -o F:\resesh-spike\bench-bin
F:\resesh-spike\bench-bin\GhosttyVtBench.exe --native F:\resesh-spike\native-out `
    --workloads <dir> --out <results> [--modes parse,single,threaded,paced] [--reps 3]
```

Modes: `parse` (VT core only), `single` (parse and 60 Hz frames on one thread), `threaded` (IO thread parses under a lock; render thread holds it only for
`begin_update`), `paced` (threaded at `--rate` MB/s for `--seconds`). Final frames are saved as
PNGs for a visual check. Set `NUGET_PACKAGES` to a roomy drive before building.

Results and conclusions: `RESULTS.md`.

## In-app (`app/`)

The real app on its real output paths, local (ConPTY) vs SSH. Uses an isolated `--data-dir` and a Release build:

```bash
python app/profiles.py <data-dir> <workload dir>               # "Flood <wl>", "SSH <wl>", baselines
python app/sshflood.py <workload dir> <data-dir>                # SSH runs only; pip install asyncssh
pwsh app/appbench.ps1 -Exe <build>/Resesh.App.exe -DataDir <data-dir> -Prefix Flood
pwsh app/appbench.ps1 -Exe <build>/Resesh.App.exe -DataDir <data-dir> -Prefix SSH -ServerPid <pid>
```

`sshflood.py` streams a workload's bytes untouched (Windows OpenSSH would put ConPTY back on the
server side) and trusts its own host key in the data dir. Do one SSH warm-up run first: the first
connection pays SSH.NET and server start-up. For hot paths, `dotnet-trace collect -p <pid>
--profile dotnet-sampled-thread-time --format Speedscope` (wall-clock samples: `PollGC` frames are
GC waits, `UNMANAGED_CODE_TIME` is native work or blocking) and `--profile gc-verbose` for
allocation ticks.
