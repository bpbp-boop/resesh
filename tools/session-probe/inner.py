#!/usr/bin/env python3
"""Runs INSIDE the persistent session: emits sequences, asks queries, reads keys."""
import os, select, sys, termios, time, tty

ESC = bytes([27])
BEL = bytes([7])
ST = ESC + b"\\"
out = sys.stdout.buffer


def w(b):
    out.write(b)
    out.flush()


EMIT = [
    ("osc7501", ESC + b"]7501;state=working:app=probe" + ST),
    ("osc7377", ESC + b"]7377;agent;id=p1;state=working;label=probe" + BEL),
    ("osc777", ESC + b"]777;notify;probe-title;probe-body" + ST),
    ("osc9", ESC + b"]9;probe-notification" + ST),
    ("osc9_4", ESC + b"]9;4;1;42" + ST),
    ("osc8", ESC + b"]8;;https://example.com/probe" + ST + b"link" + ESC + b"]8;;" + ST),
    ("osc22", ESC + b"]22;pointer" + ST),
    ("osc52", ESC + b"]52;c;cHJvYmU=" + ST),
    ("osc133A", ESC + b"]133;A" + ST),
    ("osc133D", ESC + b"]133;D;0" + ST),
    ("osc7", ESC + b"]7;file://probehost/srv/probe" + ST),
    ("osc0", ESC + b"]0;probe-title-xyz" + BEL),
    ("osc1337", ESC + b"]1337;SetUserVar=probe=dmFs" + BEL),
    ("osc3008", ESC + b"]3008;start=abc;type=shell" + ST),
    ("curly", ESC + b"[4:3m" + b"u" + ESC + b"[0m"),
    ("ulcolor", ESC + b"[4m" + ESC + b"[58:2::255:0:128m" + b"c" + ESC + b"[0m"),
    ("truecolor", ESC + b"[38;2;1;2;3m" + b"t" + ESC + b"[0m"),
    ("sync", ESC + b"[?2026h" + b"s" + ESC + b"[?2026l"),
    ("kittygfx", ESC + b"_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA" + ST),
    ("decscusr", ESC + b"[4 q"),
    ("xtwinops", ESC + b"[22;0t"),
]

QUERIES = [
    ("xtversion", ESC + b"[>q"),
    ("da1", ESC + b"[c"),
    ("da2", ESC + b"[>c"),
    ("osc11", ESC + b"]11;?" + ST),
    ("osc10", ESC + b"]10;?" + ST),
    ("kittykbd", ESC + b"[?u"),
    ("decrqm2026", ESC + b"[?2026$p"),
    ("decrqm2027", ESC + b"[?2027$p"),
    ("cpr", ESC + b"[6n"),
    ("xtgettcap", ESC + b"P+q544e" + ST),
    ("osc52q", ESC + b"]52;c;?" + ST),
]


def read_for(fd, seconds, quiet=0.25):
    """Read until `seconds` pass with nothing, or `quiet` seconds after the last byte."""
    buf = b""
    while True:
        r, _, _ = select.select([fd], [], [], quiet if buf else seconds)
        if not r:
            return buf
        buf += os.read(fd, 4096)


def main():
    mode = sys.argv[1]
    fd = sys.stdin.fileno()
    if mode == "emit":
        for name, seq in EMIT:
            w(b"<<" + name.encode() + b">>" + seq + b"<</" + name.encode() + b">>\r\n")
        w(b"EMIT-DONE\r\n")
    elif mode == "query":
        old = termios.tcgetattr(fd)
        results = []
        try:
            tty.setraw(fd)
            for name, q in QUERIES:
                w(q)
                results.append((name, read_for(fd, 1.2)))
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old)
        for name, r in results:
            w(b"RES " + name.encode() + b"=" + r.hex().encode() + b"\r\n")
        w(b"QUERY-DONE\r\n")
    elif mode == "keys":
        old = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)
            w(ESC + b"[>1u" + ESC + b"[?2004h" + ESC + b"[?1004h" + ESC + b"[?1000h" + ESC + b"[?1006h")
            w(b"KEYS-READY\r\n")
            got = read_for(fd, 4.0, quiet=1.0)
            w(ESC + b"[<u" + ESC + b"[?2004l" + ESC + b"[?1004l" + ESC + b"[?1000l" + ESC + b"[?1006l")
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old)
        w(b"RES keys=" + got.hex().encode() + b"\r\n")
        w(b"KEYS-DONE\r\n")
    elif mode == "fill":
        for i in range(1, 301):
            w(b"LINE-%d the quick brown fox\r\n" % i)
        w(ESC + b"[38;2;10;200;30mGREEN-TRUECOLOR" + ESC + b"[0m\r\n")
        w(ESC + b"[4:3mCURLY" + ESC + b"[0m\r\n")
        w(ESC + b"]8;;https://example.com/fill" + ST + b"FILL-LINK" + ESC + b"]8;;" + ST + b"\r\n")
        w(ESC + b"]0;fill-title-abc" + BEL)
        w(ESC + b"]7501;state=blocked:kind=permission:app=probe" + ST)
        w(ESC + b"]7;file://probehost/srv/fill" + ST)
        w(ESC + b"]133;A" + ST + b"prompt> " + ESC + b"]133;B" + ST + b"\r\n")
        # The prompt above ends the blocked report. This one is still current at the detach.
        w(ESC + b"]7501;state=working:app=probe:id=bg" + ST)
        w(b"FILL-DONE\r\n")
    elif mode == "hold":
        # A full-screen program with modes set, left running across the detach.
        w(ESC + b"[?1049h" + ESC + b"[2J" + ESC + b"[H" + ESC + b"[?2004h" + ESC + b"[?1004h"
          + ESC + b"[?1000h" + ESC + b"[?1006h" + ESC + b"[>1u" + ESC + b"[?25l")
        w(ESC + b"[5;10H" + b"ALT-SCREEN-CONTENT" + ESC + b"[7;3H" + ESC + b"[7mREVERSE" + ESC + b"[0m")
        w(ESC + b"]0;hold-title" + BEL)
        w(ESC + b"]7501;state=working:app=hold" + ST)
        time.sleep(float(sys.argv[2]))
        w(ESC + b"[9;1H" + b"DREW-WHILE-DETACHED")
        time.sleep(600)


if __name__ == "__main__":
    main()
