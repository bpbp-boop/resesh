"""The native terminal showcase for terminal.png: a rounded box, block progress bars, a
braille graph, the underline styles, Nerd Font icons and a Powerline prompt line.

demo_server.py prints it for `./showcase.sh` on Linux hosts. It goes over SSH rather than
a local profile because ConPTY rewrites colon-separated SGR, dropping underline colours."""

import re

E = "\x1b["
R = E + "0m"
WIDTH = 58


def fg(r, g, b):
    return f"{E}38;2;{r};{g};{b}m"


def bg(r, g, b):
    return f"{E}48;2;{r};{g};{b}m"


def row(content):
    visible = len(re.sub(r"\x1b\[[0-9;:]*m", "", content))
    return f"{E}36m│{R} {content}{' ' * (WIDTH - 2 - visible)} {E}36m│{R}"


def showcase() -> str:
    out = [f"{E}36m╭─ deploy {'─' * (WIDTH - 9)}╮{R}"]
    bars = [("render  ", 1.0, "92"), ("lint    ", 1.0, "92"), ("test    ", 0.72, "93"), ("package ", 0.18, "96")]
    for label, frac, col in bars:
        cells = 34
        full = int(frac * cells)
        part = "▏▎▍▌▋▊▉"[int((frac * cells - full) * 7)] if full < cells else ""
        bar = "█" * full + part
        out.append(row(f"{label}{E}{col}m{bar:<{cells}}{R} {int(frac * 100):>3}%  {'✔' if frac == 1 else '…'}"))
    graph = "⣀⣀⣠⣤⣴⣶⣾⣿⣿⣷⣶⣴⣤⣤⣠⣀⣀⣀⣠⣤⣶⣿⣿⣿⣷⣦⣤⣀⣀⣀⣠⣴⣾⣿"
    out.append(row(f"cpu     {E}35m{graph}{R}  41%"))
    out.append(f"{E}36m╰{'─' * WIDTH}╯{R}")
    out.append("")
    out.append(f"  {E}4:3m{E}58:2::255:95:95mcurly{R}  {E}4:2m{E}58:2::120:200:255mdouble{R}  "
               f"{E}4:4m{E}58:2::250:200:80mdotted{R}  {E}4:5m{E}58:2::140:230:140mdashed{R}  "
               f"{E}53moverline{R}  {E}1;31mbold red{R}  {E}3mitalic{R}  {E}9mstrike{R}")
    out.append("  \uf07b  src   \ue7a8  rust   \ue718  node   \uf308  docker   \uf17c  linux   \ue712  ubuntu   "
               "\uf0e7  fast   \uf00c  ok   \uf071  warn")
    out.append("")
    seg = [((40, 90, 160), (230, 240, 255), " \uf17c ops "), ((70, 130, 200), (255, 255, 255), " \uf07c ~/resesh "),
           ((120, 190, 120), (20, 30, 20), " \ue0a0 main \uf00c ")]
    line = ""
    for i, (b, f, text) in enumerate(seg):
        line += bg(*b) + fg(*f) + text
        nxt = seg[i + 1][0] if i + 1 < len(seg) else None
        line += (bg(*nxt) if nxt else R) + fg(*b) + "\ue0b0"
    out.append(line + R)
    return "\n".join(out) + "\n"
