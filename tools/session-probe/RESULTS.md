# Spike results: tmux vs zmx as the persistence layer (2026-10-13)

**Question:** tmux keeps remote sessions alive but sits in the stream as a second terminal
emulator. Would a small pty-holder that forwards raw bytes fix the escape-sequence
problems, and is zmx a usable base for one?

**Answer:** yes to both. Stock zmx passed every passthrough test. It has three gaps on
reattach, all of which look fixable in a fork.

## Setup

- Host: Ubuntu 24.04.5, x86_64, glibc 2.39, tmux 3.4, Python 3.12.
- zmx 0.8.1 release binary, static, bundling `ghostty_vt` 1.3.2-dev (the same libghostty-vt
  version `eng/ghostty-vt.json` pins for the app).
- Three targets: bare bash, tmux on a private socket with our options, `zmx attach`.
- Outer terminal: 40 rows by 120 columns, `TERM=xterm-256color`.

Raw reports are in `results/`.

## What a program prints (21 sequences)

| | tmux | zmx |
|---|---|---|
| Arrive unchanged | 0 | 20 |
| Rewritten or moved | 6 | 1 |
| Missing where the program wrote them | 15 | 0 |

- **zmx:** the one change is `OSC 133;A`, which gains `;redraw=0`.
- **tmux, lost outright:** OSC 7501, 7377, 777, 9, 9;4, 22, 133;A, 133;D, 7, 1337, 3008,
  kitty graphics, XTWINOPS `CSI 22;0t`.
- **tmux, sent again in its own form:** title (OSC 0) and clipboard (OSC 52).
- **tmux, rewritten:** OSC 8 gains `id=tmux1`; curly underline, underline colour and
  truecolor are re-encoded; the synchronized-output brackets are removed. DECSCUSR arrives
  at a different position.

## What a program asks (11 queries)

Queries: XTVERSION, DA1, DA2, OSC 10, OSC 11, kitty keyboard `CSI ? u`, DECRQM 2026 and
2027, CPR, XTGETTCAP, OSC 52 read.

| | tmux | zmx |
|---|---|---|
| Reach the terminal, reply returns to the program | 0 | 11 |
| Answered by the layer itself | 5 | 0 |
| No answer | 6 | 0 |

tmux answers XTVERSION (`tmux 3.4`), DA1, DA2, CPR and the OSC 52 read. It gives no answer
to OSC 10, OSC 11, kitty keyboard, both DECRQM queries and XTGETTCAP.

## What a program is sent

Input: `CSI 97;5u` (Ctrl+A), `CSI 13;2u` (Shift+Enter), focus-in, an SGR mouse press, a
bracketed paste.

- **zmx:** identical. All five mode requests (kitty keyboard, paste, focus, mouse 1000 and
  1006) reach the terminal.
- **tmux:** Ctrl+A arrives as `0x01`, Shift+Enter and focus-in are lost. The kitty, paste
  and focus requests do not reach the terminal; the two mouse ones do.

## Fast output

| | bare | tmux | zmx |
|---|---|---|---|
| `seq 1 100000`, lines reaching the terminal live | all | 6,745 | all |
| `seq 1 400000`, seconds | 2.85 | 1.22 | 3.53 |

tmux is fast because it skips output: about 12% of the bytes are sent. The lines are in
tmux's own history but not in the terminal's scrollback until a replay. zmx costs about
24% over bare.

## Reattach

The client's terminal is closed, more output is produced, then a new client attaches.

Scrollback scenario (`fill`):

| | tmux | zmx |
|---|---|---|
| 300 lines restored | 300 (see the bug below) | 300 |
| Output made while detached | yes | yes |
| Truecolor, curly underline, title, bracketed paste | yes | yes |
| OSC 8 link | yes | **no** |
| Working directory (OSC 7) | no | yes |
| Prompt marks (OSC 133) | no | **no** |
| Status (OSC 7501) | no | **no** |

Full-screen scenario (`hold`):

| | tmux | zmx |
|---|---|---|
| Content, including what was drawn while detached | yes | yes |
| Alternate screen | no (by design: `smcup@`) | yes |
| Cursor hidden, paste, focus, mouse modes, title | yes | yes |
| Kitty keyboard mode | no | yes |
| Status (OSC 7501) | no | **no** |

## In the app

A scratch Resesh instance opened a tab to the host and ran `zmx attach` in it. A stand-in
agent inside the session sent a plain `OSC 7501;state=blocked`. The tab showed the Claude
icon and the attention badge with no title bridge and no wrapper.

After the client was dropped, the agent ran while detached, and the tab attached again:
scrollback, colours, curly underline and the title came back, including the line printed
while detached. The badge did not. That matches the byte probe.

## Gaps in zmx

1. **Status is not restored.** OSC 7501 is not screen state, so the snapshot has no place
   for it. The daemon has to remember the last report and send it after the snapshot.
2. **Links are lost on reattach.** Our own `rvt_format_vt` asks the libghostty-vt formatter
   for hyperlinks, so this looks like an option zmx leaves off.
3. **Prompt marks are lost on reattach.** Not checked whether the formatter can emit them.
4. **Size.** The release binary is 15.7 MB with debug info (4.3 MB as tar.gz). A stripped
   build was not measured.
5. **No version handshake.** Tags are frozen and unknown tags are ignored, but the README
   warns that protocol changes kill sessions on upgrade.

## Bug found in the current tmux path

On tmux 3.4, `capture-pane -t =name` fails with `can't find pane: =name`. `-t name`,
`-t =name:` and `-t =name:0.0` work, and `has-session -t =name` works.

`TmuxPersistence.BootstrapCommand` and `ResumeCommand` use the failing form for the replay,
so on this host the replay prints the error and only the visible screen comes back: 30 of
300 lines in the test. Checked with the command on the host, not through the app. Fixed
on 2026-10-14 by adding the colon. With it the probe's tmux reattach restores 300 of 300
lines on the same tmux 3.4. The fix has not been run through the
app. The bug has been seen there: the trace log of 2026-10-14 shows `can't find pane` on a
reattach.

## The patched build (2026-10-14)

`eng/build-agent.ps1` output, x86_64, on rct-keep (Ubuntu 24.04.5, tmux 3.4). One run of
each.

| | stock zmx | patched |
|---|---|---|
| 21 output sequences unchanged | 20 | 20 |
| 11 queries answered by the terminal | 11 | 11 |
| Input identical | yes | yes |
| Reattach restores OSC 8 link | no | yes |
| Reattach restores a status that is still current | no | yes |
| Reattach leaves out a status that a prompt ended | n/a | yes |
| Reattach restores prompt marks (OSC 133) | no | yes, without the exit code |
| Snapshot marked as a replay, status sent after it | no | yes |
| Status support query answered with no client, before DA1 | no | yes |
| `seq 1 400000`, seconds | 3.53 | 4.16 |

Everything else in the two reattach scenarios is unchanged. The `fill` scenario gained a
second status report after its prompt, because the first one is ended by that prompt and
is rightly not sent again. The two times are single runs on different days, and the
patched build is `ReleaseSmall` where the stock release is `ReleaseSafe`.

## Not tested

- macOS, aarch64, musl hosts, old glibc.
- Two clients on one session, and sizes that differ between them.
- Memory per session with a large scrollback.
- A real agent (Claude Code, Codex) for a long run.
