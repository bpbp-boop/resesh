#!/usr/bin/env python3
"""Plays a shell with prompt marks (OSC 133) running one command, then waits at a prompt.

    python3 marked.py [command text]

For the in-app check that a reattach does not record old commands again: run it inside a
session, drop the connection, attach again, and look at the app's command history.
"""
import sys, time

ESC, ST = "\x1b", "\x1b\\"
command = sys.argv[1] if len(sys.argv) > 1 else "probe-marked-command"


def w(s):
    sys.stdout.write(s)
    sys.stdout.flush()


w(f"{ESC}]133;A{ST}probe@host:~$ {ESC}]133;B{ST}{command}\r\n")
w(f"{ESC}]133;C{ST}output of {command}\r\n")
w(f"{ESC}]133;D;0{ST}")
w(f"{ESC}]133;A{ST}probe@host:~$ {ESC}]133;B{ST}")
time.sleep(600)
