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

## Frame timings (`RESESH_FRAME_STATS`)

Set `RESESH_FRAME_STATS=<folder>` before launching the app and it writes `frames-<pid>.csv`
(per drawn frame: UI-thread time for each step, rows drawn, buffer size, output parsed since
the previous frame), `gpu-<pid>.csv` (GPU time per frame from timestamp queries) and
`info-<pid>.txt` (the adapter) there. With the variable unset the hooks cost one bool check.

```powershell
# sshflood.py usernames take -x<n> (repeat the workload n times) and -r<MB/s> (paced, 10 s)
& app\framebench.ps1 -Exe <publish>\Resesh.App.exe -DataDir <data-dir> -Out <dir> `
    -Profiles @('SSH logs-x30', 'SSH unicode-x20') -Sizes @('small=1280x800', 'big=3840x2160')
python framestats.py <run dir> ...
```

Sizes are window placements written to the data dir's settings before each launch (the app
clamps them to the work area). Results: `RESULTS.md`.
