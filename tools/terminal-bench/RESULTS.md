# libghostty-vt spike vs the WebView2 terminal — 2026-10-07

Machine: Ryzen 7 7700X, Radeon RX 6800 XT (driver 32.0.21045.5002), Windows 11 26200.
Grid: 170×50 cells, Cascadia Mono 14 px, 10,000 lines of scrollback on both sides.
libghostty-vt: ghostty-org/ghostty `b699ea79` (1.3.2-dev), Zig 0.16.0, ReleaseFast,
`x86_64-windows-msvc`. WebView side: headless Edge (same Chromium as WebView2), WebGL renderer
on the real GPU (ANGLE/D3D11).

Three stacks:

- **web**: the real `terminal.html` (xterm.js 6 + WebGL + highlight, ruler, command marks,
  rewind keyframes) with a host stub.
- **bare**: xterm.js + WebGL + unicode11 only. Same message path, none of our addons.
- **native**: libghostty-vt + a 140-line C shim (`rvt.c`, flattens dirty rows) + a C#
  Direct2D/DirectWrite renderer drawing grid-aligned glyph runs into a D3D11 texture.

## Burst throughput (MB/s, best of 3; whole workload, 32 KiB batches, until last frame)

| workload | native parse only | native parse + 60 Hz frames | bare | web |
|---|---:|---:|---:|---:|
| logs (8 MB) | 329 | 308 | 61.6 | 23.5 |
| color (8 MB) | 110 | 105 | 58.3 | 43.6 |
| tiny `yes` (2 MB) | 10.1 | 9.5 | 7.8 | 5.1 |
| tui (8 MB) | 879 | 825 | 99.6 | 101 |
| unicode (7.4 MB) | 184 | 180 | 60.3 | 39.7 |

## Sustained 2 MB/s for 3 s (CPU ms per second of output)

| workload | native (all threads) | bare (renderer + GPU proc) | web (renderer + GPU proc) | web main-thread tasks |
|---|---:|---:|---:|---:|
| logs | 45 | 285 | 671 | 384 |
| color | 79 | 284 | 409 | 207 |
| tiny | 270 | 440 | 650 | 464 |
| tui | 29 | 145 | 178 | 89 |
| unicode | 55 | 191 | 375 | 186 |

Longest UI-thread block during the paced runs: native ≤ 1.4 ms (render thread; parsing runs
on the IO thread and holds the lock only for `begin_update`, max lock wait 0.05 ms). Web main
thread: 43–66 ms on the scrolling workloads (bare: 7–31 ms), i.e. keystrokes can wait that long
behind output today.

Native renders at 0.3–0.8 ms per 60 Hz frame (dirty rows only; scrolling marks all rows dirty).

## Rewind keyframes (full buffer after the workload, ~10k lines)

| workload | native snapshot encode / restore | size | web `serialize()` + base64 | size |
|---|---:|---:|---:|---:|
| logs | 0.9 / 3.3 ms | 1.2 MB | 55 ms | 1.6 MB |
| color | 3.3 / 8.7 ms | 6.6 MB | 64 ms | 2.0 MB |
| tiny | 0.3 / 2.3 ms | 41 KB | 43 ms | 39 KB |
| unicode | 2.0 / 6.5 ms | 4.3 MB | 44 ms | 1.1 MB |

Native snapshots are exact (cells, styles, modes, parser state) but uncompressed; colored
output is 3–4× larger than an ANSI keyframe. Web restore cost was not measured.

## Reading the numbers

- Like-for-like (core + renderer, no resesh features) is **native vs bare**: 1.8–8× burst
  throughput except `tiny`, and 1.6–6× less CPU at a realistic rate.
- **web vs bare** is what our addons cost today: ~60% of logs throughput and 2.4× the CPU at
  2 MB/s. A native port would reimplement highlight/ruler scans over row data in C#, and
  keyframes become a 1–3 ms snapshot instead of a 40–60 ms serialize.
- `tiny` (one short line per LF with full scrollback) is the weak spot for both cores; ghostty is
  only ~1.2× bare xterm there and spends 270 ms CPU per second at 2 MB/s.
- Architecture matters as much as raw speed: the native core lets parsing leave the UI thread.

## Caveats

- The native renderer is a spike: no color emoji (monochrome fallback), ligatures, selection,
  decorations, IME or accessibility; offscreen texture, no swap-chain present or DWM
  composition. Visual check of the final frames only (`native-*-single.png`).
- Web runs are headless Edge, not inside the app: they exclude the C# base64/JSON step
  (~70 µs per 32 KiB) and WebView2's host→page IPC, so web costs are lower bounds. Headless Edge's
  browser process (excluded above) also used 3–370 ms per run, partly its own services.
- Web CPU is `SystemInfo.getProcessInfo` (OS tick-sampled); native CPU is
  `QueryProcessCycleTime` calibrated to ms. GPU execution time is not counted on either side.
- One machine, one GPU, no high-DPI run.
- libghostty-vt default scrollback is capped by bytes (~440 rows at 170 columns); the native
  harness removes the byte cap so the 10,000-line limit governs, matching xterm.

## Integration notes

- Built for Windows on the first try: one `zig build -Demit-lib-vt` (92 s cold), 2 MB DLL, no
  patches. Compare the Microsoft Terminal fork's pinned patch set.
- The C API already covers what the MS Terminal port had to add through ABI 1.2–3.1: dirty-row
  render state with a two-phase update for threaded renderers, exact snapshots, search,
  selection, formatter (text/VT/HTML), semantic prompt marks, title/pwd/bell/clipboard/
  progress/notification callbacks, an unknown-sequence callback (for OSC 7377 and 3008),
  key and mouse encoders, kitty graphics.
- Per-cell reads are one C call per field; a small native shim (or the raw cells view) is
  needed to keep P/Invoke out of the per-cell path.
- The header states the API is not yet stable and breaking changes are expected. Pin a commit
  and keep the C# surface behind our own interface.
- Windows-side work the library will not do: DirectWrite shaping/fallback/color glyphs,
  TSF/IME, UIA text provider, SwapChainPanel hosting.

## In-app floods, ConPTY vs SSH (2026-10-08, after b1aa030)

App CPU for the flood minus the idle run for the same surface and connection, mean of 2,
Release build, recording on (`alwaysRecord`), `app/appbench.ps1`:

| workload | ghostty local | ghostty SSH | webview local | webview SSH | conhost (local) |
|---|---|---|---|---|---|
| logs (8 MB) | 3.4 s | 1.4 s | 5.7 s | 2.2 s | 3.0 s |
| color | 3.0 s | 1.3 s | 2.9 s | 2.1 s | 3.1 s |
| unicode | 3.0 s | 0.9 s | 4.0 s | 1.1 s | 3.1 s |
| tiny | 2.8 s | 1.5 s | 3.8 s | 3.4 s | 5.9 s |
| tui | 0.5 s | 0.6 s | 0.9 s | 0.9 s | 2.1 s |

- ConPTY doubles the work: conhost parses everything into its own buffer and re-renders it as
  VT, which costs ~3 s of its own CPU per 8 MB, and its re-rendered output (cursor moves, many
  small reads) costs the app ~2 s more to consume than the original bytes over SSH.
- Ghostty's logs flood reaches idle ~1.1 s after start-up over SSH (~7 MB/s) vs ~3.6 s locally
  (~2.3 MB/s): local tabs are capped by conhost, not by the surface.
- Private memory: ghostty ~240-310 MB, WebView2 ~480-750 MB.
- The same session's before/after for b1aa030 (local, ghostty, CPU incl. start-up): logs
  5.9 s -> 3.8 s, tiny 9.0 s -> 3.2 s; WebView2 logs 9.2 s -> 8.1 s (it shares the recorder).
- Baselines drift between sessions (the producer tracking polls the process list): compare
  within one table only.

## Frame timings in the shipping renderer (2026-10-29)

`RESESH_FRAME_STATS=<dir>` makes the app log every drawn frame (UI-thread time per step, rows
drawn, GPU time from D3D11 timestamp queries, output parsed since the last frame).
`app/framebench.ps1` runs profiles at given window sizes; `framestats.py` summarizes. AOT
publish, SSH floods from `sshflood.py` (no ConPTY), built-in highlight rules on (the default),
4K 144 Hz display (6.9 ms budget), RX 6800 XT. Medians unless noted; µs.

| window / workload | grid | fps | UI thread per frame (p50 / p99) | read | prep | record | EndDraw | Present | GPU p50 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1280×800 logs | 97×36 | 137–141 | 870 / 1,900 | 50 | 280 | 285 | 21 | 200 | 125 |
| 1280×800 color | 97×36 | 76–90 | 875 / 2,000–5,000 | 87 | 187 | 89 | 174 | 186 | 155 |
| 1280×800 tui | 97×36 | 128–143 | 470 / 970 | 77 | 8 | 67 | 120 | 170 | 300 |
| 1280×800 unicode | 97×36 | 46–50 | 20,400 / 25,700 | 49 | 225 | 19,700 | 49 | 287 | 249 |
| 1280×800 tiny | 97×36 | 16–23 | 1,300 / 2,800 | 970 | 84 | 44 | 62 | 176 | 88 |
| full screen logs | 381×109 | 82–97 | 3,500 / 7,500–9,400 | 530 | 1,260 | 1,250 | 110 | 310 | 1,770 |
| full screen color | 381×109 | 89–94 | 3,170 / 6,600–8,300 | 615 | 900 | 1,240 | 43 | 315 | 1,785 |
| full screen tui | 381×109 | 126–139 | 880 / 2,000–2,400 | 244 | 8 | 135 | 135 | 345 | 977 |
| full screen unicode | 381×109 | 10–16 | 60,000–106,000 | 650 | 675 | 58,000–105,000 | 135 | 325 | 40,000–73,000 |
| full screen tiny | 381×109 | 7–8 | 6,100 / 8,600–11,800 | 4,700 | 340 | 290 | 166 | 647 | 1,570 |

- **Color emoji are the outlier.** `DrawCluster` sends each one through `ID2D1DeviceContext::
  DrawText` with a color font: a new layout plus color-glyph rasterization per emoji per frame,
  ~0.55 ms of UI thread each (one 👍 per line in `unicode`), and tens of ms of GPU at full
  screen. A rasterized-glyph cache (what Ghostty's atlas does) would remove almost all of it.
- **Plain text at full screen costs ~3.5 ms of UI thread and ~1.8 ms GPU per frame**, within
  the 144 Hz budget at p50 but not at p99, so scrolling floods run at 80–100 fps. Recording is
  ~11 µs per row; Direct2D's GPU work grows with the pixels drawn.
- **prep is the highlighter** (it skips the alternate screen, so `tui` shows ~8 µs): ~11 µs per
  dirty row while scrolling, because every new line misses the match cache. The scroll bar
  update (~40 µs) and IME box placement (~8 µs) are small.
- **read in `tiny`** is the UI thread waiting on the parser lock: libghostty-vt parses one-short-
  line output at 5–18 MB/s (slower on wider grids), so frames drop to 7–23 fps. That is the
  shared core, not our renderer.
- Copy of the window-sized canvas to the back buffer is ~10–45 µs of CPU and not visible in GPU
  time; Present is ~0.2–0.35 ms and never blocked for a vsync.
- The paced (`-r1`) runs drew at 64 fps because asyncio's sleep on Windows has 15.6 ms
  granularity; use floods for frame-rate numbers.

### After the emoji atlas (same day)

Color emoji are now rasterized once per (cluster, width, color) into a 2048² atlas and copied
with a nearest-neighbor `DrawBitmap`; screenshots of the `unicode` workload are pixel-identical
to before. Combining-mark clusters reuse their text layout.

| unicode workload | before | after |
|---|---:|---:|
| 1280×800 fps | 46–50 | 138–141 |
| 1280×800 UI thread per frame (p50) | 20.4 ms | 0.94 ms |
| full screen fps | 10–16 | 109–122 |
| full screen UI thread per frame (p50) | 60–106 ms | 2.9–3.0 ms |
| full screen GPU per frame (p50) | 40–73 ms | 1.75 ms |

`logs` and `color` are unchanged within run-to-run noise.

### UI-thread stalls (same day)

Frame logs now also carry GC pause time, allocation, the annotation pass (ruler, overview index)
and keyframe capture. The slow frames and 150–500 ms gaps between frames were not the renderer:

- **Overview indexing** (the ruler's highlight lane) scanned up to 2000 scrollback lines per pass
  on the UI thread. Each line read takes the terminal lock, so during a flood a pass took
  50–500 ms and passes ran back to back (up to 950 ms of every second in `tiny`). A pass now
  stops after 4 ms; indexing catches up when output calms.
- **The terminal lock**: `rvt_write` parsed a whole SSH read (32 KB+) under one lock hold, ~6 ms
  in `tiny`, and every UI read (the frame, line text) waited behind it. Writes are now parsed in
  4 KB slices, and the writer yields while anyone waits for the lock.
- GC pauses are ~2–3% of the time (2–3 MB allocated per frame, mostly outside the renderer);
  the highlighter's row text no longer allocates. Highlighting still costs ~11 µs per new row
  in a flood: the regex, not the text building.

Full screen (381×109), before → after:

| workload | fps | longest annotation pass | gaps > 50 ms | UI per frame p50 |
|---|---|---|---|---|
| logs | 99–109 → 117–132 | 150–175 → 7–8 ms | 7–9 → 1 | 3.5–3.9 → 3.5 ms |
| color | 99 → 120–133 | 136–163 → 7–11 ms | 6–10 → 1 | 3.1–3.2 → 3.2 ms |
| unicode | 92–111 → 130–131 | 26–45 → 6–9 ms | 1 → 1 | 2.7–2.9 → 2.8 ms |
| tiny | 7 → 122–124 | 446–496 → 23–24 ms | 11–14 → 1 | 6.3–6.8 → 2.2 ms |

(The one remaining gap per run is at start-up.)

### Render thread (same day)

Frames are now drawn on a per-surface render thread (like Ghostty) instead of the UI thread's
`CompositionTarget.Rendering`. The frame log's times are now render-thread times; `ui lag`
(from `ui-<pid>.csv`) is how late a callback posted every 10 ms ran on the UI thread, which is
what typing and clicking wait for. Same session, same machine, 2 runs each, before → after:

| workload | fps | UI lag p50 | UI lag p99 | UI lag max |
|---|---|---|---|---|
| full screen logs | 117–130 → 126–138 | 0.15–0.6 → 0.06–0.11 ms | 5.6–8.1 → 0.8–0.9 ms | 13–17 → 4.3 ms |
| full screen color | 88–132 → 128–134 | 0.07–0.16 → 0.06–0.07 ms | 3.2–3.9 → 0.4–1.3 ms | 7–8 → 0.5–4.3 ms |
| full screen unicode | 133–137 → 136–138 | 0.16 → 0.05 ms | 3.3–6.5 → 2.9–3.9 ms | 6.4–7.3 → 5.4–7.7 ms |
| full screen tui | 107–134 → 135–140 | 0.08–0.1 → 0.06 ms | 1.1–1.7 → 0.4–2.1 ms | 3–8.5 → 1.5–2.6 ms |
| full screen tiny | 120–122 → 142 | 0.13 → 0.06–0.08 ms | 4.6–6.2 → 5.5–6.0 ms | 7.5–7.9 → 7.4–19 ms |
| 1280×800 logs | 79–88 → 131 | 0.08–0.11 → 0.06–0.07 ms | 0.6–1.1 → 0.4–3.4 ms | 3.9–17 → 2.4–4.4 ms |

What is left on the UI thread during a flood is the annotation pass (command marks, ruler, the
overview index's scan of the live screen): up to ~10 ms, and ~23 ms in `tiny`, where each of the
109 live-row reads waits behind a 4 KB parse slice. That, not drawing, is now the UI's worst case.

### Batched line reads for the overview index (same day)

`rvt_lines_text` reads a run of lines under one hold of the terminal lock; the overview index
fetches its scrollback slice in 64-line chunks (plus the next line, which wrap detection reads)
and the live screen in one call. Full-screen `tiny`, render-thread build → batched:

| | before | after |
|---|---|---|
| UI lag p99 | 5.5–6.0 ms | 2.1–3.5 ms |
| UI lag max | 7.4–19 ms | 4.9–14 ms |
| longest annotation pass | 22.7–22.9 ms | 17–20 ms |

Other workloads are unchanged. What remains of a `tiny` pass is a dozen single native calls
(the anchor marker, line times, command tracking), each of which can wait 1–4 ms for the lock,
mostly behind the ~4 ms rewind keyframe capture that runs once a second during a flood.

`framestats.py` now leaves out UI stalls that began before the measured window (connecting).
