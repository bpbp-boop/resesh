# terminal-bench

Compares the terminal output path of the WebView2 surface (xterm.js + WebGL + resesh addons)
with a native libghostty-vt + Direct2D/DirectWrite spike (`tools/GhosttyVtBench`). Both sides
read the same workload bytes and receive them in the 32 KiB batches `TerminalControl` posts.

## Workloads

`node gen.mjs <dir>` writes `logs`, `color`, `tiny`, `tui` and `unicode` as raw `.bin` files.
The generators match the 2026-10-06 profiling rig byte for byte. Rates are reported on real UTF-8
bytes (the old rig divided by JS string length, which understated `unicode`).

## WebView side

```bash
node build-page.mjs ../../src/Terminal/wwwroot <out>/page.html   # the real page + host stub
node build-bare.mjs ../../src/Terminal/wwwroot <out>/bare.html   # xterm + WebGL + unicode11 only
node run-web.mjs <out>/page.html <dir> web                       # burst: best of 3
node run-web.mjs <out>/page.html <dir> webpaced logs,color,tiny,tui,unicode --paced --reps=3
node top.mjs <out>/webprof-logs.cpuprofile                        # after a --profile run
```

Runs headless Edge (same Chromium as WebView2). `--paced` replays 3 s at 2 MB/s and reports
CPU time per browser process type via `SystemInfo.getProcessInfo`. `EXTRA_EDGE_ARGS` passes
extra switches to Edge.

## Native side

```powershell
tools\GhosttyVtBench\build-native.ps1 -Root F:\resesh-spike     # Zig 0.16.0 + pinned Ghostty
dotnet build tools\GhosttyVtBench -c Release -o F:\resesh-spike\bench-bin
F:\resesh-spike\bench-bin\GhosttyVtBench.exe --native F:\resesh-spike\native-out `
    --workloads <dir> --out <results> [--modes parse,single,threaded,paced] [--reps 3]
```

Modes: `parse` (VT core only), `single` (parse and 60 Hz frames on one thread, like the WebView
main thread), `threaded` (IO thread parses under a lock; render thread holds it only for
`begin_update`), `paced` (threaded at `--rate` MB/s for `--seconds`). Final frames are saved as
PNGs for a visual check. Set `NUGET_PACKAGES` to a roomy drive before building.

Results and conclusions: `RESULTS.md`.
