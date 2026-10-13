import re, subprocess, sys, time
import probe
from probe import Term, command, INNER, TMUX, ATTACH_REPLIES, cleanup

target = sys.argv[1]
name = "dbg"
cleanup(target, name)
t = Term(command(target, name, False))
t.replies = dict(ATTACH_REPLIES)
t.pump(2.0)
mark = len(t.log)
t.send(INNER + " fill\r")
t.until(b"FILL-DONE", 10)
t.pump(1.0)
seg = t.log[mark:]
print("live: LINE rows seen by the terminal:", sum(1 for i in range(1, 301) if b"LINE-%d the" % i in seg), "of 300")
if target == "tmux":
    cap = subprocess.run(f"{TMUX} capture-pane -e -p -t ={name} -S -", shell=True, capture_output=True)
    print("capture-pane rc", cap.returncode, cap.stderr[:200], "rows:",
          sum(1 for i in range(1, 301) if b"LINE-%d the" % i in cap.stdout))
    print(subprocess.run(f"{TMUX} display -p -t ={name} '#{{history_size}} #{{history_limit}}'", shell=True, capture_output=True).stdout)

for n in (20000, 100000, 400000):
    t.pump(0.3)
    mark = len(t.log)
    start = time.time()
    t.send("seq 1 %d; echo THRU-''DONE\r" % n)
    ok = t.until(b"THRU-DONE", 60)
    seg = t.log[mark:]
    nums = set(int(x) for x in re.findall(rb"(?m)^(\d+)\r?$", seg.replace(b"\r\n", b"\n")))
    print("seq %d: finished=%s %.2fs, bytes to terminal=%d, distinct numbers seen=%d"
          % (n, ok, time.time() - start, len(seg), len(nums)))
    if not ok:
        print("  tail:", repr(seg[-200:]))
        break
t.send("exit\r")
t.pump(1)
cleanup(target, name)
