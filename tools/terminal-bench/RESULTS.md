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
