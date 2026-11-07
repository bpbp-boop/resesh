"""Helpers for driving a resesh instance during screenshot capture: launch with a
--data-dir, size the window, type into it with `winapp ui send-keys` (posted messages,
so the window never needs focus), and capture it with PrintWindow, which draws the
window's own content even when other windows cover it."""

import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import subprocess
import time

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
EXE = os.path.join(REPO, r"src\App\bin\x64\Debug\net10.0-windows10.0.19041.0\Resesh.App.exe")

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32")
dwmapi = ctypes.WinDLL("dwmapi")
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2

EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def windows_of(pid: int):
    found = []

    def cb(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found


def title(hwnd) -> str:
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


class App:
    def __init__(self, data_dir: str, width=1920, height=1080, wait=8.0):
        self.data_dir = data_dir
        self.proc = subprocess.Popen([EXE, "--data-dir", data_dir])
        self.hwnd = None
        deadline = time.time() + 40
        while time.time() < deadline and not self.hwnd:
            time.sleep(0.25)
            for h in windows_of(self.proc.pid):
                if "resesh" in title(h):
                    self.hwnd = h
        if not self.hwnd:
            raise RuntimeError("resesh window did not appear")
        self.size(width, height)
        time.sleep(wait)

    @property
    def pid(self):
        return self.proc.pid

    def size(self, width, height, x=20, y=20):
        user32.ShowWindow(self.hwnd, 9)
        user32.SetWindowPos(self.hwnd, 0, x, y, width, height, 0x0040 | 0x0004)

    def winapp(self, *args, timeout=60):
        r = subprocess.run(["winapp", "ui", *args, "-w", str(self.hwnd)], capture_output=True,
                           encoding="utf-8", errors="replace", timeout=timeout)
        return re.sub(r"\x1b\[[0-9;]*m", "", (r.stdout or "") + (r.stderr or "")).strip()

    def terminals(self):
        """Terminal panes left to right, top to bottom: [(slug, x, y)]."""
        found = re.findall(r'(\S+) Group "Terminal" \((\d+),(\d+)', self.winapp("search", "Terminal"))
        return sorted(((s, int(x), int(y)) for s, x, y in found), key=lambda t: (t[1], t[2]))

    def focus_terminal(self, index=0):
        self.winapp("focus", self.terminals()[index][0])
        time.sleep(0.2)

    def add_to_selection(self, *name_prefixes):
        """Adds list items to a multi-select list through UIA, since posted Shift and Ctrl
        clicks never reach XAML. Matches items whose name starts with each prefix."""
        names = ",".join("'" + n.replace("'", "''") + "'" for n in name_prefixes)
        script = (
            "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes;"
            f"$root=[Windows.Automation.AutomationElement]::FromHandle([IntPtr]{self.hwnd});"
            "$items=$root.FindAll([Windows.Automation.TreeScope]::Descendants,"
            "(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,"
            "[Windows.Automation.ControlType]::ListItem)));"
            f"foreach($p in @({names})){{foreach($i in $items){{if($i.Current.Name.StartsWith($p+',')){{"
            "$i.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()}}}")
        subprocess.run(["powershell.exe", "-NoProfile", "-Command", script], capture_output=True, timeout=60)

    def scrub(self, values, delay=0.066):
        """Steps the first slider in the window through values (UIA RangeValue), one per
        delay seconds: the rewind bar's slider has no automation id to target otherwise."""
        steps = ",".join(f"{v:.3f}" for v in values)
        script = (
            "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes;"
            f"$root=[Windows.Automation.AutomationElement]::FromHandle([IntPtr]{self.hwnd});"
            "$s=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,"
            "(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,"
            "[Windows.Automation.ControlType]::Slider)));"
            "$p=$s.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern);"
            f"foreach($v in @({steps})){{$p.SetValue($v);Start-Sleep -Milliseconds {int(delay * 1000)}}}")
        subprocess.run(["powershell.exe", "-NoProfile", "-Command", script], capture_output=True, timeout=600)

    def slider_range(self):
        script = (
            "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes;"
            f"$root=[Windows.Automation.AutomationElement]::FromHandle([IntPtr]{self.hwnd});"
            "$s=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,"
            "(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,"
            "[Windows.Automation.ControlType]::Slider)));"
            "$p=$s.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern);"
            "\"$($p.Current.Minimum) $($p.Current.Maximum)\"")
        r = subprocess.run(["powershell.exe", "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=60)
        lo, hi = r.stdout.split()
        return float(lo), float(hi)

    def type(self, text: str, enter=True, pause=0.4):
        if text:
            self.winapp("send-keys", text, "--verbatim")
        if enter:
            self.winapp("send-keys", "enter")
        time.sleep(pause)

    def keys(self, combo: str, pause=0.4):
        self.winapp("send-keys", combo)
        time.sleep(pause)

    def tree(self, expand=()):
        """Collapses the session tree, then expands each folder path given (parents first)."""
        self.winapp("invoke", "CollapseAllButton")
        time.sleep(0.5)
        for path in expand:
            # InvokePattern only selects a tree item; Right expands the focused one.
            self.winapp("focus", f"SessionFolder_{path}")
            self.winapp("send-keys", "right")
            time.sleep(0.3)

    def capture(self, path=None, crop=None) -> Image.Image:
        """The window without its invisible resize border; crop=(l, t, r, b) in pixels."""
        rect = wt.RECT()
        user32.GetWindowRect(self.hwnd, ctypes.byref(rect))
        frame = wt.RECT()
        dwmapi.DwmGetWindowAttribute(self.hwnd, 9, ctypes.byref(frame), ctypes.sizeof(frame))
        w, h = rect.right - rect.left, rect.bottom - rect.top
        hdc = user32.GetWindowDC(self.hwnd)
        mem = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
        gdi32.SelectObject(mem, bmp)
        user32.PrintWindow(self.hwnd, mem, 2)
        buf = ctypes.create_string_buffer(w * h * 4)

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD),
                        ("biBitCount", wt.WORD), ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                        ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
                        ("biClrImportant", wt.DWORD)]

        bih = BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
        gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bih), 0)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(self.hwnd, hdc)
        img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
        img = img.crop((frame.left - rect.left, frame.top - rect.top, frame.right - rect.left, frame.bottom - rect.top))
        if crop:
            img = img.crop(crop)
        if path:
            img.save(path, optimize=True)
        return img

    def close(self):
        try:
            self.proc.kill()
        except OSError:
            pass


def layout(data_dir, groups, node, names=None):
    """Opens these tab groups at the next launch. groups: lists of session names; node:
    {"groupIndex": i} or {"orientation": 0 columns | 1 rows, "children": [...], "sizes": [...]}"""
    with open(os.path.join(data_dir, "sessions.json"), encoding="utf-8") as f:
        ids = {s["name"]: s["id"] for s in json.load(f)["sessions"]}
    g = [dict(tabs=[dict(sessionId=ids[n]) for n in grp], activeTabIndex=0) for grp in groups]
    with open(os.path.join(data_dir, "workspaces.json"), "w") as f:
        json.dump(dict(workspaces=[], lastLayout=dict(groups=g, layout=node)), f, indent=2)
    path = os.path.join(data_dir, "settings.json")
    with open(path) as f:
        settings = json.load(f)
    settings["reopenLastLayoutAtStartup"] = True
    with open(path, "w") as f:
        json.dump(settings, f, indent=2)


def settings(data_dir, **values):
    path = os.path.join(data_dir, "settings.json")
    with open(path) as f:
        s = json.load(f)
    s.update(values)
    with open(path, "w") as f:
        json.dump(s, f, indent=2)
