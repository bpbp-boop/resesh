# session-probe

Measures what a persistence layer on the remote host does to the terminal stream. It was
written for the 2026-10-13 spike that compared our tmux setup with stock
[zmx](https://github.com/neurosnap/zmx) 0.8.1; findings are in [RESULTS.md](RESULTS.md) and
the plan that follows from them is in the roadmap under "Remote session agent".

Everything here runs on the remote host (Linux, Python 3, no packages). Nothing in the app
uses it.

## Parts

| File | Role |
|------|------|
| `probe.py` | Plays the outer terminal. Starts the target in a pty, logs every raw byte, answers queries with replies no real terminal would give, so an answer can be traced to its source. |
| `inner.py` | Runs inside the session. Prints the test sequences, asks the queries, reads keys, and has two reattach scenarios: `fill` (scrollback) and `hold` (a full-screen program). |
| `throughput.py` | Counts how many lines of `seq 1 N` reach the terminal live, and how long it takes. |
| `tmux_capture.py` | Tries each `capture-pane -t` target form against the private server. |
| `show.py`, `inject.py`, `step.sh` | In-app test: a stand-in agent that reports plain OSC 7501, and helpers to attach a real Resesh tab to a zmx session and drop its client. |
| `results/` | Raw output of the spike runs. `tab.py` prints the three `run-*.json` side by side. |

## Running

Copy the directory to the host. For the zmx target, put the `zmx` binary next to
`probe.py`; sockets go in `./sock`.

```sh
python3 probe.py run bare            # also: tmux, zmx
python3 probe.py reattach zmx fill   # also: tmux; scenario fill or hold
python3 throughput.py tmux
python3 tmux_capture.py
```

`run` prints a JSON report. `reattach` prints a report and writes the bytes the terminal
received on the second attach to `snap-<target>-<scenario>.bin`.

The tmux target uses its own socket (`tmux -L resesh-spike`) with the options
`TmuxPersistence.BootstrapCommand` sets, copied by hand into `probe.py`. If those options
change, update `TMUX_SETUP`.

## Things to know

- `probe.py` resumes tmux with `capture-pane -t ={name}`, the same target the app uses.
  On tmux 3.4 that form fails (see RESULTS.md). `results/snap-tmux-fill.bin` was taken with
  the target changed to `={name}:`.
- The tmux throughput figure in `results/run-tmux.json` is a timeout, not a time: tmux
  redraws the screen, so the end marker never arrives as plain text. `throughput.py`
  counts lines instead.
- `zmx run <name> <command>` takes the command as separate arguments. A quoted string
  exits 127.
- `inject.py` types into another terminal with `TIOCSTI`, which needs root on current
  kernels.
- The in-app steps in `step.sh` assume the app's shell is on `pts/0` and the files are in
  `/root/.local/share/resesh-spike`.
