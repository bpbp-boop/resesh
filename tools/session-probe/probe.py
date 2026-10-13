#!/usr/bin/env python3
"""Plays the OUTER terminal: attaches to a session in a pty and logs every raw byte."""
import fcntl, json, os, pty, select, signal, struct, subprocess, sys, termios, time

HERE = os.path.dirname(os.path.abspath(__file__))
INNER = "python3 " + os.path.join(HERE, "inner.py")
ZMX = os.path.join(HERE, "zmx")
ZDIR = os.path.join(HERE, "sock")
TMUX = "tmux -L resesh-spike"
ESC = bytes([27])
BEL = bytes([7])
ST = ESC + b"\\"
SHELL = "env PS1='$ ' bash --norc --noprofile"

# The private server's options, as TmuxPersistence.BootstrapCommand sets them.
TMUX_SETUP = (
    "set -g terminal-overrides '*:smcup@:rmcup@:indn@:Tc"
    ":Smulx=\\E[4::%p1%dm"
    ":Setulc=\\E[58::2::%p1%{65536}%/%d::%p1%{256}%/%{255}%&%d::%p1%{255}%&%d%;m"
    ":Smol=\\E[53m"
    ":Sync=\\E[?2026%?%p1%{1}%-%tl%eh%;"
    ":Hls=\\E]8;%?%p1%l%tid=%p1%s%;;%p2%s\\E\\\\"
    ":smxx=\\E[9m' \\; "
    "set -sq focus-events on \\; set -sq set-clipboard on \\; "
    "set -g set-titles on \\; set -g set-titles-string '#{pane_title}'"
)


def command(target, name, resume):
    if target == "bare":
        return SHELL
    if target == "zmx":
        return f"ZMX_DIR={ZDIR} {ZMX} attach {name} {SHELL}"
    if target == "tmux":
        if resume:
            return (f"{TMUX} capture-pane -e -p -t ={name}: -S -; "
                    f"exec {TMUX} {TMUX_SETUP} \\; attach-session -t ={name}")
        return (f"exec {TMUX} -f /dev/null start-server \\; set -g history-limit 50000 \\; "
                f"set -g status off \\; set -g prefix None \\; set -s escape-time 25 \\; "
                f"{TMUX_SETUP} \\; new-session -s {name} \"{SHELL}\"")
    raise SystemExit("unknown target")


class Term:
    def __init__(self, cmd, rows=40, cols=120):
        self.log = b""
        self.answered = 0
        self.replies = {}
        pid, fd = pty.fork()
        if pid == 0:
            fcntl.ioctl(0, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
            os.environ["TERM"] = "xterm-256color"
            os.execvp("sh", ["sh", "-c", cmd])
        self.pid, self.fd = pid, fd

    def pump(self, seconds):
        end = time.time() + seconds
        while time.time() < end:
            r, _, _ = select.select([self.fd], [], [], max(0, end - time.time()))
            if not r:
                break
            try:
                data = os.read(self.fd, 65536)
            except OSError:
                return False
            if not data:
                return False
            self.log += data
            self.respond()
        return True

    def respond(self):
        """Answer queries seen since the last answer, each once per occurrence."""
        tail = self.log[self.answered:]
        best = None
        for q, a in self.replies.items():
            i = tail.find(q)
            if i >= 0 and (best is None or i < best[0]):
                best = (i, q, a)
        if best:
            i, q, a = best
            os.write(self.fd, a)
            self.answered += i + len(q)
            self.respond()

    def until(self, marker, seconds):
        start = len(self.log)
        end = time.time() + seconds
        while time.time() < end:
            if marker in self.log[start:]:
                return True
            if not self.pump(0.2):
                break
        return marker in self.log[start:]

    def send(self, data):
        os.write(self.fd, data if isinstance(data, bytes) else data.encode())

    def drop(self):
        """The connection dies: the client loses its terminal."""
        os.close(self.fd)
        try:
            os.kill(self.pid, signal.SIGHUP)
        except ProcessLookupError:
            pass
        os.waitpid(self.pid, 0)


ATTACH_REPLIES = {ESC + b"[c": ESC + b"[?62;22c", ESC + b"[>c": ESC + b"[>1;10;0c"}
QUERY_REPLIES = {
    ESC + b"[>q": ESC + b"P>|ResQ 9.9" + ST,
    ESC + b"[>0q": ESC + b"P>|ResQ 9.9" + ST,
    ESC + b"[c": ESC + b"[?65;4;22c",
    ESC + b"[0c": ESC + b"[?65;4;22c",
    ESC + b"[>c": ESC + b"[>77;88;99c",
    ESC + b"[>0c": ESC + b"[>77;88;99c",
    ESC + b"]11;?" + ST: ESC + b"]11;rgb:1234/5678/9abc" + ST,
    ESC + b"]11;?" + BEL: ESC + b"]11;rgb:1234/5678/9abc" + BEL,
    ESC + b"]10;?" + ST: ESC + b"]10;rgb:aaaa/bbbb/cccc" + ST,
    ESC + b"]10;?" + BEL: ESC + b"]10;rgb:aaaa/bbbb/cccc" + BEL,
    ESC + b"[?u": ESC + b"[?5u",
    ESC + b"[?2026$p": ESC + b"[?2026;2$y",
    ESC + b"[?2027$p": ESC + b"[?2027;3$y",
    ESC + b"[6n": ESC + b"[33;44R",
    ESC + b"P+q544e" + ST: ESC + b"P1+r544e=52657351" + ST,
    ESC + b"]52;c;?" + ST: ESC + b"]52;c;UkVTUQ==" + ST,
}


def between(log, name):
    a, b = f"<<{name}>>".encode(), f"<</{name}>>".encode()
    i = log.rfind(a)
    j = log.find(b, i)
    if i < 0 or j < 0:
        return None
    return log[i + len(a):j]


def results(log):
    res = {}
    for line in log.replace(b"\r", b"\n").split(b"\n"):
        k = line.find(b"RES ")
        if k >= 0 and b"=" in line[k:]:
            key, _, val = line[k + 4:].partition(b"=")
            try:
                res[key.decode()] = bytes.fromhex(val.strip().decode())
            except ValueError:
                pass
    return res


def run(target):
    sys.path.insert(0, HERE)
    import inner
    name = "probe"
    t = Term(command(target, name, False))
    t.replies = dict(ATTACH_REPLIES)
    t.pump(2.0)
    report = {"target": target, "attach_bytes": len(t.log)}

    # 1. what a program prints
    mark = len(t.log)
    t.send(INNER + " emit\r")
    t.until(b"EMIT-DONE\r\n", 8)
    t.pump(0.5)
    seg = t.log[mark:]
    emit = {}
    for ename, seq in inner.EMIT:
        got = between(seg, ename)
        emit[ename] = "intact" if got == seq else ("DROPPED" if not got else "changed: " + repr(got)[:90])
        if got != seq and seq in seg:
            emit[ename] = "intact (moved)"
    report["emit"] = emit

    # 2. what a program asks
    t.replies = dict(QUERY_REPLIES)
    t.answered = len(t.log)
    mark = len(t.log)
    t.send(INNER + " query\r")
    t.until(b"QUERY-DONE\r\n", 30)
    t.pump(0.5)
    seg = t.log[mark:]
    res = results(seg)
    query = {}
    for qname, q in inner.QUERIES:
        reached = q in seg
        ours = QUERY_REPLIES.get(q)
        got = res.get(qname, b"")
        if got == ours:
            verdict = "end to end"
        elif got:
            verdict = "answered by middle: " + repr(got)[:60]
        else:
            verdict = "no answer"
        query[qname] = {"reached_terminal": reached, "result": verdict}
    report["query"] = query

    # 3. what a program is sent
    t.replies = {}
    mark = len(t.log)
    t.send(INNER + " keys\r")
    keys_in = ESC + b"[97;5u" + ESC + b"[13;2u" + ESC + b"[I" + ESC + b"[<0;10;5M" + ESC + b"[200~paste" + ESC + b"[201~"
    if t.until(b"KEYS-READY", 8):
        t.pump(0.5)
        t.send(keys_in)
    t.until(b"KEYS-DONE\r\n", 10)
    seg = t.log[mark:]
    got = results(seg).get("keys", b"")
    report["keys"] = {
        "sent": repr(keys_in),
        "received": repr(got),
        "identical": got == keys_in,
        "modes_reaching_terminal": {
            n: (s in seg) for n, s in [("kitty >1u", ESC + b"[>1u"), ("paste 2004", ESC + b"[?2004h"),
                                       ("focus 1004", ESC + b"[?1004h"), ("mouse 1000", ESC + b"[?1000h"),
                                       ("mouse 1006", ESC + b"[?1006h")]},
    }

    # 4. throughput
    t.pump(0.5)
    start = time.time()
    t.send("seq 1 400000; echo THRU-''DONE\r")
    ok = t.until(b"THRU-DONE\r\n", 120)
    report["throughput"] = {"finished": ok, "seconds": round(time.time() - start, 2)}

    t.send("exit\r")
    t.pump(1.5)
    cleanup(target, name)
    print(json.dumps(report, indent=1))


def cleanup(target, name):
    if target == "zmx":
        subprocess.run(f"ZMX_DIR={ZDIR} {ZMX} kill {name} --force", shell=True, capture_output=True)
    elif target == "tmux":
        subprocess.run(f"{TMUX} kill-server", shell=True, capture_output=True)


def reattach(target, scenario):
    """Fill or hold, drop the connection, let output arrive unseen, attach again."""
    name = "re" + scenario
    cleanup(target, name)
    t = Term(command(target, name, False))
    t.replies = dict(ATTACH_REPLIES)
    t.pump(2.0)
    if scenario == "fill":
        t.send(INNER + " fill\r")
        t.until(b"FILL-DONE\r\n", 10)
        t.pump(0.5)
        t.send("(sleep 3; echo WHILE-''DETACHED-1; echo WHILE-''DETACHED-2) &\r")
        t.pump(0.7)
    else:
        t.send(INNER + " hold 3\r")
        t.until(b"REVERSE", 8)
        t.pump(0.7)
    before = len(t.log)
    t.drop()
    time.sleep(5)

    t2 = Term(command(target, name, True))
    t2.replies = dict(ATTACH_REPLIES)
    t2.pump(3.0)
    snap = t2.log
    path = os.path.join(HERE, f"snap-{target}-{scenario}.bin")
    open(path, "wb").write(snap)

    def has(b):
        return b in snap

    # The agent tells the app where the old screen starts and ends, and sends status after it.
    begin, end = snap.find(b"]7377;replay;state=begin"), snap.find(b"]7377;replay;state=end")
    status = snap.find(b"]7501;")
    rep = {"target": target, "scenario": scenario, "first_attach_bytes": before, "snapshot_bytes": len(snap),
           "replay bracket, status after it": 0 <= begin < end < status}
    if scenario == "fill":
        rep.update({
            "LINE rows replayed": sum(1 for i in range(1, 301) if b"LINE-%d the" % i in snap),
            "output while detached": has(b"WHILE-DETACHED-1") and has(b"WHILE-DETACHED-2"),
            "truecolor kept": has(b"38;2;10;200;30") or has(b"38:2::10:200:30") or has(b"38:2:10:200:30"),
            "curly underline kept": has(b"4:3"),
            "hyperlink kept": has(b"https://example.com/fill"),
            "title re-sent": has(b"fill-title-abc"),
            "cwd (OSC 7) re-sent": has(b"/srv/fill"),
            "status (OSC 7501) re-sent": has(b"7501;state=working:app=probe:id=bg"),
            "status ended by a prompt left out": not has(b"kind=permission"),
            "prompt marks (OSC 133) kept": has(b"]133;A"),
            "bracketed paste re-enabled": has(b"[?2004h"),
        })
    else:
        rep.update({
            "alt screen entered": has(b"[?1049h") or has(b"[?47h") or has(b"[?1047h"),
            "alt content redrawn": has(b"ALT-SCREEN-CONTENT"),
            "drawn while detached": has(b"DREW-WHILE-DETACHED"),
            "reverse video kept": has(b"REVERSE"),
            "cursor hidden": has(b"[?25l"),
            "bracketed paste": has(b"[?2004h"),
            "focus reporting": has(b"[?1004h"),
            "mouse 1000": has(b"[?1000h"),
            "mouse 1006": has(b"[?1006h"),
            "kitty keyboard": has(b"[>1u") or has(b"[=1"),
            "title re-sent": has(b"hold-title"),
            "status (OSC 7501) re-sent": has(b"7501"),
        })
    t2.send(bytes([3]))
    t2.pump(0.5)
    cleanup(target, name)
    print(json.dumps(rep, indent=1))


if __name__ == "__main__":
    if sys.argv[1] == "run":
        run(sys.argv[2])
    else:
        reattach(sys.argv[2], sys.argv[3])
