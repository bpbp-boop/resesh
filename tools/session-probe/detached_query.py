#!/usr/bin/env python3
"""Runs inside a session nobody is attached to: asks two queries and saves the replies.

    ZMX_DIR=./sock ./zmx run q python3 $PWD/detached_query.py $PWD/q.out status-then-da1

A program asks whether status reports (OSC 7501) are supported together with DA1, and
takes a DA1 reply that comes first as a no.
"""
import os, select, sys, termios, tty

SEQS = {
    "status-then-da1": b"\x1b]7501;?\x1b\\" + b"\x1b[c",
    "title-then-da1": b"\x1b]0;t\x1b\\" + b"\x1b[c",
}
old = termios.tcgetattr(0)
tty.setraw(0)
os.write(1, SEQS[sys.argv[2]])
got = b""
while True:
    r, _, _ = select.select([0], [], [], 1.5)
    if not r:
        break
    got += os.read(0, 4096)
termios.tcsetattr(0, termios.TCSANOW, old)
open(sys.argv[1], "w").write(repr(got))
