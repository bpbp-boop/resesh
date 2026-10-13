import re, subprocess, sys, time
from probe import Term, command, INNER, TMUX, ATTACH_REPLIES, cleanup

name = "s7dfa2e1c9999"
cleanup("tmux", name)
t = Term(command("tmux", name, False))
t.replies = dict(ATTACH_REPLIES)
t.pump(2.0)
t.send(INNER + " fill\r")
t.until(b"FILL-DONE", 10)
t.pump(1.0)


def sh(c):
    p = subprocess.run(c, shell=True, capture_output=True)
    return p.returncode, p.stderr.strip()[:80], p.stdout


print(sh(f"{TMUX} list-sessions")[2])
for tgt in ("=" + name, name, "=" + name + ":", "=" + name + ":0.0"):
    rc, err, out = sh(f"{TMUX} capture-pane -e -p -t {tgt} -S -")
    print("capture -t %-22s rc=%d err=%s rows=%d" % (tgt, rc, err, sum(1 for i in range(1, 301) if b"LINE-%d the" % i in out)))
for cmd in ("has-session", "kill-session"):
    pass
print("has-session =name:", sh(f"{TMUX} has-session -t ={name}")[:2])

mark = len(t.log)
t.send("seq 1 100000; echo THRU-''DONE\r")
t.until(b"THRU-DONE", 60)
t.pump(1.0)
seg = t.log[mark:]
nums = set(int(x) for x in re.findall(rb"(?<![\d;\[])(\d{1,6})(?![\d;Hm])", seg) if int(x) <= 100000)
print("seq 100000 via tmux: bytes=%d distinct numbers that reached the terminal=%d" % (len(seg), len(nums)))
rc, err, out = sh(f"{TMUX} capture-pane -p -t {name} -S -")
print("history holds numbers:", len(set(re.findall(rb"(?m)^\d+$", out))))
t.send("exit\r")
t.pump(1)
cleanup("tmux", name)
