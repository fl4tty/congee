#!/usr/bin/env python3
"""congee - a conky-style system monitor that draws on the Windows desktop.

Reads a conky-like config (legacy "settings + TEXT" format, or conky 1.10+
Lua-style conky.config / conky.text), evaluates $variables against live
system stats from psutil, and draws the result in a borderless, transparent
window pinned just above the desktop wallpaper.

Usage:
    pythonw congee.py                 # uses congee.conf next to this file
    pythonw congee.py -c other.conf   # run another config (multiple instances OK)
    python  congee.py --once          # print one evaluated frame as text and exit
    python  congee.py --interfaces    # list network interface names
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import datetime
import getpass
import os
import platform
import re
import shlex
import socket
import subprocess
import sys
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
from collections import deque
from dataclasses import dataclass

import psutil

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.join(HERE, "congee.conf")  # bundled default / template
USER_CONFIG = os.path.join(os.environ.get("APPDATA") or os.path.expanduser("~"), "congee", "congee.conf")
# Searched in order when no -c is given, like conky's ~/.config/conky/conky.conf then ~/.conkyrc
CONFIG_SEARCH = [
    USER_CONFIG,
    os.path.join(os.path.expanduser("~"), ".config", "congee", "congee.conf"),
    os.path.join(os.path.expanduser("~"), ".congeerc"),
    DEFAULT_CONFIG,
]


def find_config() -> str:
    return next((p for p in CONFIG_SEARCH if os.path.isfile(p)), DEFAULT_CONFIG)
KEY_COLOR = "#010101"  # pixels of exactly this colour become see-through
IS_WINDOWS = sys.platform == "win32"


class Log:
    r"""congee.log next to the user's config (%APPDATA%\congee): starts and stops with the reason,
    crashes, sleep/wake, lock/unlock and display errors, plus an hourly "still running" line, so
    that after congee disappears you can tell what happened and roughly when. Shared with the
    .NET edition (lines are tagged with the edition and process id). Kept under ~1 MB: the older
    half is moved to congee.log.1. Logging never raises."""
    PATH = os.path.join(os.environ.get("APPDATA") or os.path.expanduser("~"), "congee", "congee.log")
    MAX_BYTES = 1 << 20
    STOP_MARKER = "stopped: "
    OFF_MARKER = "logging turned off"
    enabled = True  # the "logging" setting; while False nothing is written
    _lock = threading.Lock()
    _seen: set = set()
    _reason: str | None = None
    _stopped = False
    _started = time.monotonic()

    @classmethod
    def write(cls, message: str) -> None:
        if not cls.enabled:
            return
        with cls._lock:
            try:
                os.makedirs(os.path.dirname(cls.PATH), exist_ok=True)
                if os.path.exists(cls.PATH) and os.path.getsize(cls.PATH) > cls.MAX_BYTES:
                    os.replace(cls.PATH, cls.PATH + ".1")
                stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
                with open(cls.PATH, "a", encoding="utf-8") as f:
                    f.write(f"{stamp} [Python {os.getpid()}] {message}\n")
            except OSError:
                pass

    @classmethod
    def once(cls, message: str) -> None:
        """Log a message once per run (for errors that would otherwise repeat every tick)."""
        if message not in cls._seen:
            cls._seen.add(message)
            cls.write(message)

    @classmethod
    def stop_reason(cls, reason: str) -> None:
        """Remember why congee is about to stop; the first reason given wins."""
        if cls._reason is None:
            cls._reason = reason

    @classmethod
    def uptime(cls) -> str:
        t = int(time.monotonic() - cls._started)
        return f"{t // 3600}h {t % 3600 // 60}m" if t >= 3600 else f"{t // 60}m {t % 60}s"

    @classmethod
    def set_enabled(cls, on: bool) -> None:
        """Turn logging on or off (from the "logging" setting), noting the change in the log."""
        if on == cls.enabled:
            return
        if on:
            cls.enabled = True
            cls.write("logging turned on")
        else:
            cls.write(cls.OFF_MARKER)
            cls.enabled = False

    @classmethod
    def started(cls, config_path: str, enabled: bool = True) -> None:
        cls.enabled = enabled
        if enabled:
            cls._report_unclean_previous_run()
        cls.write(f"started: congee (Python {platform.python_version()}), config {config_path}, Windows {platform.version()}")

        def crash(kind, exc, tb):
            import traceback
            cls.stop_reason("crashed")
            cls.write("CRASH: " + "".join(traceback.format_exception(kind, exc, tb)).rstrip())
        sys.excepthook = crash
        threading.excepthook = lambda a: cls.once(
            f"error in thread {a.thread.name if a.thread else '?'}: {a.exc_type.__name__}: {a.exc_value}")

    @classmethod
    def stopped(cls, exit_code: int = 0) -> None:
        """Write the "stopped:" line (once; on sign-out it's written early, as Windows may end the
        process before the main loop returns)."""
        if cls._stopped:
            return
        cls._stopped = True
        cls.write(f"{cls.STOP_MARKER}{cls._reason or 'widget window closed'} (exit code {exit_code}, up {cls.uptime()})")

    @classmethod
    def _report_unclean_previous_run(cls) -> None:
        """If the last run's final line isn't a "stopped:" line, it was killed or the PC lost
        power: say so, with its last sign of life. Skipped when that process is still running."""
        try:
            with open(cls.PATH, "rb") as f:
                f.seek(max(0, os.path.getsize(cls.PATH) - 4096))
                lines = [ln.strip() for ln in f.read().decode("utf-8", "replace").splitlines() if ln.strip()]
            m = re.match(r"^(\S+ \S+) \[(\S+) (\d+)\] (.*)$", lines[-1]) if lines else None
            if not m or m.group(4).startswith(cls.STOP_MARKER) or m.group(4) == cls.OFF_MARKER:
                return
            prev = int(m.group(3))
            try:
                name = psutil.Process(prev).name().lower()
                if name.startswith(("congee", "python")):
                    return  # still running (e.g. the other edition)
            except psutil.Error:
                pass
            cls.write(f"previous run ({m.group(2)} {prev}) ended without logging a stop: it was killed "
                      f"(e.g. End task) or the PC lost power. Its last entry was at {m.group(1)}: {m.group(4)}")
        except (OSError, ValueError):
            pass

DEFAULTS = {
    "alignment": "top_right",
    "gap_x": "20",
    "gap_y": "40",
    "minimum_width": "0",
    "minimum_height": "0",
    "update_interval": "1.0",
    "font": "Consolas:size=10",
    "default_color": "white",
    "default_shade_color": "black",
    "draw_shades": "yes",
    "default_outline_color": "black",
    "draw_outline": "no",
    "draw_graph_borders": "yes",
    "own_window_type": "desktop",  # desktop | normal | override (always on top)
    "own_window_transparent": "yes",
    "own_window_colour": "#101418",
    "own_window_argb_value": "200",  # 0-255 window opacity when not transparent
    "border_inner_margin": "8",
    "default_bar_height": "6",
    "default_graph_height": "24",
    "click_through": "no",
    "draggable": "yes",
    "monitor": "0",  # 0 = primary, 1.. = others
    "uppercase": "no",
    "dpi_scale": "auto",  # auto, or a number like 1.0 / 1.5
    "sensors_url": "http://localhost:8085/data.json",  # LibreHardwareMonitor web server
    "temp_bar_max": "100",  # °C that count as a full tempbar/tempgraph
    "temperature_unit": "celsius",  # or fahrenheit: how temperature values are shown
    "gpu": "auto",  # which GPU the gpu_* variables read: auto, 0/1/..., or part of its name
    "start_lhm": "ask",  # start LibreHardwareMonitor if it isn't running: ask, yes or no
    "stop_lhm": "ask",  # on Exit congee, also stop LibreHardwareMonitor: ask, yes or no
    "logging": "yes",  # write congee.log (starts, stops, crashes) to %APPDATA%\congee
    "rendering": "software",  # .NET edition only: draw with the CPU or the graphics card; Tk always uses GDI
}

ALIGN_ALIASES = {
    "tl": "top_left", "tr": "top_right", "tm": "top_middle",
    "bl": "bottom_left", "br": "bottom_right", "bm": "bottom_middle",
    "ml": "middle_left", "mr": "middle_right", "mm": "middle_middle",
}


# --------------------------------------------------------------------------
# Config parsing
# --------------------------------------------------------------------------
def set_setting(path: str, key: str, value: str, comment: str | None = None) -> None:
    """Set "key value" in the settings part of a config file (above TEXT): change the existing
    line, or add one (with an optional comment) just above TEXT. Keeps BOM and line endings."""
    with open(path, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw[3 if bom else 0:].decode("utf-8")
    crlf = "\r\n" in text
    lines = text.replace("\r\n", "\n").split("\n")
    end = next((i for i, ln in enumerate(lines) if ln.strip() == "TEXT"), len(lines))
    at = next((i for i in range(end) if re.match(rf"^\s*{re.escape(key)}(\s|$)", lines[i])), None)
    if at is not None:
        lines[at] = f"{key} {value}"
    else:
        lines[end:end] = ([f"# {comment}"] if comment else []) + [f"{key} {value}", ""]
    out = ("\r\n" if crlf else "\n").join(lines).encode("utf-8")
    with open(path, "wb") as f:
        f.write((b"\xef\xbb\xbf" if bom else b"") + out)


def parse_config(path: str) -> tuple[dict, str]:
    with open(path, encoding="utf-8-sig") as f:  # -sig: tolerate Notepad-style BOM
        src = f.read()
    settings = dict(DEFAULTS)

    if "conky.text" in src:  # conky 1.10+ Lua syntax
        m = re.search(r"conky\.config\s*=\s*\{(.*?)\}", src, re.S)
        if m:
            body = re.sub(r"--[^\n]*", "", m.group(1))
            for k, v in re.findall(r"(\w+)\s*=\s*(\"[^\"]*\"|'[^']*'|[^,\n]+)", body):
                v = v.strip().strip("\"'")
                settings[k] = {"true": "yes", "false": "no"}.get(v, v)
        m = re.search(r"conky\.text\s*=\s*\[(=*)\[\n?(.*?)\]\1\]", src, re.S)
        text = m.group(2) if m else ""
    else:  # legacy: "key value" lines, then TEXT, then the template
        lines = src.splitlines()
        text = ""
        for i, line in enumerate(lines):
            s = line.strip()
            if s == "TEXT":
                text = "\n".join(lines[i + 1:])
                break
            if not s or s.startswith("#"):
                continue
            parts = s.split(None, 1)
            settings[parts[0]] = parts[1].strip() if len(parts) > 1 else "yes"
    return settings, text.rstrip("\n")


TOKEN_RE = re.compile(r"\$\$|\$\{([^}]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)")


def tokenize(text: str) -> list[tuple]:
    out, pos = [], 0
    for m in TOKEN_RE.finditer(text):
        if m.start() > pos:
            out.append(("lit", text[pos:m.start()]))
        if m.group(0) == "$$":
            out.append(("lit", "$"))
        elif m.group(1) is not None:
            name, _, args = m.group(1).strip().partition(" ")
            out.append(("var", name, args.strip(), m.group(0)))
        else:
            out.append(("var", m.group(2), "", m.group(0)))
        pos = m.end()
    if pos < len(text):
        out.append(("lit", text[pos:]))
    return out


def split_args(args: str) -> list[str]:
    try:
        parts = shlex.split(args, posix=False)
    except ValueError:
        parts = args.split()
    return [p[1:-1] if len(p) >= 2 and p[0] == p[-1] and p[0] in "\"'" else p for p in parts]


def yes(v) -> bool:
    return str(v).strip().lower() in ("yes", "true", "1", "on")


HEX6_RE = re.compile(r"^#?[0-9a-fA-F]{6}$")


def norm_color(c: str) -> str:
    c = c.strip()
    if HEX6_RE.match(c) and not c.startswith("#"):
        return "#" + c
    return c


# --------------------------------------------------------------------------
# Formatting helpers
# --------------------------------------------------------------------------
def fmt_bytes(n: float) -> str:
    n = float(n)
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if n < 1024 or unit == "TiB":
            return f"{n:.0f}{unit}" if unit == "B" else f"{n:.1f}{unit}"
        n /= 1024
    return f"{n:.1f}TiB"


def fmt_duration(sec: float, short: bool = False) -> str:
    sec = int(sec)
    d, r = divmod(sec, 86400)
    h, r = divmod(r, 3600)
    m, s = divmod(r, 60)
    if short:
        return f"{d}d {h}h" if d else f"{h}h {m}m"
    return (f"{d}d " if d else "") + f"{h}h {m}m" + ("" if d else f" {s}s")


def read_cpu_name() -> str:
    if IS_WINDOWS:
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                                r"HARDWARE\DESCRIPTION\System\CentralProcessor\0") as k:
                return " ".join(winreg.QueryValueEx(k, "ProcessorNameString")[0].split())
        except OSError:
            pass
    return platform.processor() or "Unknown CPU"


# --------------------------------------------------------------------------
# Segments produced by evaluating the template
# --------------------------------------------------------------------------
@dataclass
class Text:
    s: str
    color: str
    font: str


@dataclass
class Bar:
    frac: float
    h: int
    w: int  # 0 = fill remaining width
    color: str


@dataclass
class Graph:
    vals: list
    h: int
    w: int
    color: str
    color2: str | None


@dataclass
class HR:
    h: int
    color: str
    dash: bool = False


@dataclass
class Goto:
    x: int


@dataclass
class Offset:
    x: int


ALIGNR, ALIGNC = "alignr", "alignc"
SIZE_RE = re.compile(r"^\d+(,\d+)?$")


# --------------------------------------------------------------------------
# Hardware sensors (temperatures, fans, ...) from LibreHardwareMonitor
# --------------------------------------------------------------------------
LHM_GROUP_TYPES = {
    "temperatures": "temperature", "fans": "fan", "load": "load", "clocks": "clock",
    "powers": "power", "voltages": "voltage", "currents": "current", "controls": "control",
    "data": "data", "smalldata": "data", "throughput": "throughput", "levels": "level",
    "factors": "factor", "energy": "energy", "noise": "noise",
}
NUM_RE = re.compile(r"-?\d+(?:[.,]\d+)?")
SENSOR_WIDGET_TYPES = {
    "tempbar": "temperature", "tempgraph": "temperature", "fangraph": "fan",
    "loadbar": "load", "loadgraph": "load", "controlbar": "control",
}
# Device-scoped variables: (sensor type, names to try in order). The lists cover
# how LibreHardwareMonitor names things on AMD, NVIDIA and Intel hardware;
# "" means "first sensor of that type on the device".
DEVICE_VARS = {
    "gpu_temp": ("temperature", ["GPU Core", "GPU", ""]),
    "gpu_hotspot": ("temperature", ["GPU Hot Spot", "Hot Spot"]),
    "gpu_memtemp": ("temperature", ["GPU Memory Junction", "GPU Memory"]),
    "gpu_load": ("load", ["GPU Core", "D3D 3D", ""]),
    "gpu_clock": ("clock", ["GPU Core", ""]),
    "gpu_memclock": ("clock", ["GPU Memory"]),
    "gpu_power": ("power", ["GPU Package", "GPU Power", ""]),
    "gpu_fan": ("fan", ["GPU Fan", ""]),
    "gpu_fan_perc": ("control", ["GPU Fan", ""]),
    # AMD Zen2+: "Core (Tctl/Tdie)", Zen1: "Core (Tctl)"; Intel: "CPU Package"
    "cpu_temp": ("temperature", ["Core (Tctl/Tdie)", "Core (Tctl)", "CPU Package", "Package", "Core Max", "Core Average", ""]),
    # AMD: "Package"; Intel: "CPU Package"
    "cpu_power": ("power", ["Package", "CPU Package", ""]),
}
DEVICE_WIDGETS = {  # widget -> the variable it draws
    "gpu_loadbar": "gpu_load", "gpu_loadgraph": "gpu_load",
    "gpu_tempbar": "gpu_temp", "gpu_tempgraph": "gpu_temp",
    "gpu_fangraph": "gpu_fan", "cpu_tempbar": "cpu_temp", "cpu_tempgraph": "cpu_temp",
}


class Sensors:
    """Polls LibreHardwareMonitor's built-in web server (Options > Remote Web
    Server) in a background thread. Reading needs no admin rights; LHM itself
    runs elevated and does the hardware access."""

    def __init__(self):
        self.url = "http://localhost:8085/data.json"
        self.interval = 1.0
        self.items: list[dict] = []
        self.ok = False
        self.error = "not polled yet"
        self.last = -1e9
        self.busy = False
        self.seen: dict[tuple, tuple[dict, float]] = {}

    STALE_SECONDS = 10

    def configure(self, url: str, interval: float):
        if url != self.url:
            self.items, self.ok, self.last, self.seen = [], False, -1e9, {}
        self.url, self.interval = url, max(0.5, interval)

    def get(self) -> list[dict]:
        """Latest readings; kicks off a background refresh when stale."""
        if not self.busy and time.monotonic() - self.last >= self.interval:
            self.busy, self.last = True, time.monotonic()
            threading.Thread(target=self._refresh, daemon=True).start()
        return self.items

    def refresh_now(self):
        self.busy = True
        self._refresh()
        self.last = time.monotonic()

    def _refresh(self):
        import json
        import urllib.request
        try:
            with urllib.request.urlopen(self.url, timeout=2) as r:
                tree = json.loads(r.read().decode("utf-8", "replace"))
            fresh: list[dict] = []
            self._walk(tree, [], None, fresh)
            # Some sensors (e.g. AMD GPU ones) briefly vanish from LHM's feed;
            # keep a sensor's last reading for a few seconds instead of N/A.
            now = time.monotonic()
            for it in fresh:
                self.seen[(it["type"], it["path"])] = (it, now)
            self.seen = {k: v for k, v in self.seen.items() if now - v[1] <= self.STALE_SECONDS}
            self.items, self.ok, self.error = [it for it, _ in self.seen.values()], True, ""
        except Exception as e:  # noqa: BLE001 - LHM not running, port closed, etc.
            self.items, self.ok, self.error = [], False, str(e)
        finally:
            self.busy = False

    def _walk(self, node: dict, path: list[str], group: str | None, out: list[dict],
              hw: tuple[str, str] = ("", "")):
        text = str(node.get("Text", ""))
        if node.get("HardwareId"):  # LHM hardware node, e.g. /gpu-nvidia/0
            hw = (str(node["HardwareId"]).lower(), text)
        children = node.get("Children") or []
        if children:
            gtype = LHM_GROUP_TYPES.get(text.lower().replace(" ", ""), group)
            for ch in children:
                self._walk(ch, path + [text] if text else path, gtype, out, hw)
            return
        raw = node.get("RawValue") or node.get("Value") or ""
        m = NUM_RE.search(str(raw))
        if not m or not (node.get("Type") or group):
            return
        stype = str(node.get("Type") or group).lower()
        hw_id, hw_name = hw
        if not hw_id and node.get("SensorId"):  # older LHM: derive from /gpu-amd/0/temperature/1
            hw_id = "/".join(str(node["SensorId"]).lower().split("/")[:3])
        if not hw_name:
            hw_name = next((p for p in reversed(path) if p.lower().replace(" ", "") not in LHM_GROUP_TYPES), "")
        out.append({
            "type": stype,
            "name": text,
            "hardware": hw_name,
            "hw_id": hw_id,
            "path": "/".join(path[1:] + [text]),  # drop the root machine-name node
            "value": float(m.group(0).replace(",", ".")),
            "display": str(node.get("Value", "")),
        })

    def find(self, stype: str, query: str | list[str], hw_id: str | None = None) -> dict | None:
        """First sensor of a type whose name (exact) or path (substring) matches.
        `query` may be a list of fallbacks; `hw_id` limits the search to one device."""
        cands = [s for s in self.get() if s["type"] == stype and (hw_id is None or s["hw_id"] == hw_id)]
        for q in ([query] if isinstance(query, str) else query):
            q = q.strip().lower()
            if not q:
                if cands:
                    return cands[0]
                continue
            for s in cands:
                if s["name"].lower() == q:
                    return s
            for s in cands:
                if q in s["path"].lower():
                    return s
        return None

    # ---- device selection (GPU / CPU) -------------------------------------
    def devices(self, prefix: str) -> list[tuple[str, str]]:
        """(hw_id, name) of every device whose id starts with prefix, in LHM order."""
        out: dict[str, str] = {}
        for s in self.get():
            if s["hw_id"].startswith(prefix):
                out.setdefault(s["hw_id"], s["hardware"])
        return list(out.items())

    INTEGRATED_GPU_RE = re.compile(r"integrated|\b(uhd|iris|hd) graphics|radeon\(tm\) graphics$|radeon graphics$|vega \d+ graphics", re.I)

    def vram(self, hw_id: str, which: str) -> float | None:
        """GPU memory 'used' / 'total' in bytes. LHM reports it as SmallData (MB)
        on some cards and Data (GB) on others, so accept either."""
        for name in (f"GPU Memory {which.title()}", f"D3D Dedicated Memory {which.title()}"):
            for stype, mult in (("smalldata", 1024**2), ("data", 1024**3)):
                s = self.find(stype, name, hw_id)
                if s and s["name"].lower() == name.lower():
                    return s["value"] * mult
        return None

    def select_gpu(self, pref: str) -> tuple[str, str] | None:
        """Pick a GPU: `auto` prefers a discrete card (most VRAM), else a
        number (0 = first GPU) or text matched against the GPU's name/id."""
        gpus = self.devices("/gpu")
        if not gpus:
            return None
        pref = (pref or "auto").strip().lower()
        if pref.isdigit():
            return gpus[int(pref)] if int(pref) < len(gpus) else None
        if pref != "auto":
            return next((g for g in gpus if pref in g[1].lower() or pref in g[0]), None)

        def score(g):
            integrated = bool(self.INTEGRATED_GPU_RE.search(g[0] + " " + g[1]))
            return (not integrated, self.vram(g[0], "total") or 0)
        return max(gpus, key=score)  # max() keeps the first of equal scores


# --------------------------------------------------------------------------
# Drives and GPU drivers
# --------------------------------------------------------------------------
DRIVE_KINDS = {2: "removable", 3: "local", 4: "network", 5: "optical", 6: "ram"}
LOCAL_KINDS = ("removable", "local", "ram")


def network_path(letter_colon: str) -> tuple[str, bool]:
    r"""(\\server\share, connected) for a mapped drive (SMB, NFS, WebDAV...). Windows also
    lists remembered mappings that aren't connected right now; asking those for volume
    info only times out after ~10 s, so the caller skips that for them."""
    buf, size = ctypes.create_unicode_buffer(1024), wt.DWORD(1024)
    rc = ctypes.windll.mpr.WNetGetConnectionW(letter_colon, buf, ctypes.byref(size))
    if rc == 1201:  # ERROR_CONNECTION_UNAVAIL: remembered, not connected
        return buf.value, False
    return (buf.value if rc == 0 else ""), True


def scan_drives(slow: bool = True) -> list[dict]:
    r"""Every usable drive letter, as dicts with root ("C:\"), kind (local, removable,
    network, optical, ram) and label (for mapped drives the \\server\share path). Mapped
    drives that are disconnected or unreachable, empty card readers and optical drives
    without a disc are left out. The same local
    volume under a second letter (subst, extra mount point) is listed once.
    slow=False skips network/optical drives entirely: they can take seconds to answer."""
    if not IS_WINDOWS:
        return [{"root": p.mountpoint, "kind": "local", "label": ""} for p in psutil.disk_partitions()]
    k32 = ctypes.windll.kernel32
    mask = k32.GetLogicalDrives()
    out, seen = [], set()
    old_mode = k32.SetErrorMode(0x0001)  # SEM_FAILCRITICALERRORS: no "insert a disk" dialogs
    try:
        for i in range(26):
            if not (mask >> i) & 1:
                continue
            root = chr(65 + i) + ":\\"
            kind = DRIVE_KINDS.get(k32.GetDriveTypeW(root))
            if kind is None or (not slow and kind not in LOCAL_KINDS):
                continue
            label, serial = ctypes.create_unicode_buffer(261), wt.DWORD()
            if kind == "network":
                unc, connected = network_path(root[:2])
                if connected and k32.GetVolumeInformationW(root, label, 261, ctypes.byref(serial), None, None, None, 0):
                    out.append({"root": root, "kind": kind, "label": unc or label.value})
                continue  # disconnected or unreachable: not shown
            ready = bool(k32.GetVolumeInformationW(root, label, 261, ctypes.byref(serial), None, None, None, 0))
            if not ready:
                continue
            if kind in LOCAL_KINDS and serial.value:
                if serial.value in seen:
                    continue
                seen.add(serial.value)
            out.append({"root": root, "kind": kind, "label": label.value})
    finally:
        k32.SetErrorMode(old_mode)
    return out


class DriveIO:
    r"""Read/write bytes per second per drive letter, from Windows' performance counters:
    LogicalDisk(C:) for local, USB and DVD drives and SMB Client Shares(\server\share) for
    mapped SMB drives. NFS and WebDAV drives have no such counter (rate() gives None).
    A drive's counters are added the first time it's asked for; all are read once per tick."""
    NO_INSTANCE = 0x800007D1  # PDH_CSTATUS_NO_INSTANCE

    class _Value(ctypes.Structure):  # PDH_FMT_COUNTERVALUE with PDH_FMT_DOUBLE
        _fields_ = [("status", wt.DWORD), ("pad", wt.DWORD), ("value", ctypes.c_double)]

    def __init__(self):
        self.pdh = None
        self.query = wt.HANDLE()
        if IS_WINDOWS:
            try:
                pdh = ctypes.windll.pdh
                if pdh.PdhOpenQueryW(None, 0, ctypes.byref(self.query)) == 0:
                    self.pdh = pdh
            except OSError:
                pass
        self.counters: dict[tuple, tuple | None] = {}  # (root, share) -> (read, write) counter handles
        self.rates: dict[tuple, tuple[float, float] | None] = {}

    def _add(self, path: str):
        h = wt.HANDLE()
        return h if self.pdh.PdhAddEnglishCounterW(self.query, path, 0, ctypes.byref(h)) == 0 else None

    def rate(self, root: str, share: str = "") -> tuple[float, float] | None:
        r"""(read, write) bytes/s for a drive root ("C:\"); share is the \\server\share of a mapped drive."""
        if self.pdh is None:
            return None
        key = (root, share)
        if key not in self.counters:
            if share:
                obj, inst, names = "SMB Client Shares", share[1:], ("Read Bytes/sec", "Write Bytes/sec")
            else:
                obj, inst, names = "LogicalDisk", root[:2], ("Disk Read Bytes/sec", "Disk Write Bytes/sec")
            r, w = (self._add(f"\\{obj}({inst})\\{n}") for n in names)
            self.counters[key] = (r, w) if r and w else None
            self.rates[key] = (0.0, 0.0) if r and w else None  # real values from the next tick on
            # Rates need two samples. Take the first now; PDH divides by the real time between
            # samples, so this extra one doesn't skew the other drives' numbers.
            self.pdh.PdhCollectQueryData(self.query)
        return self.rates.get(key)

    def collect(self):
        if self.pdh is None or not self.counters:
            return
        self.pdh.PdhCollectQueryData(self.query)
        for key, handles in self.counters.items():
            if handles is None:
                continue
            vals = []
            for h in handles:
                v = self._Value()
                rc = self.pdh.PdhGetFormattedCounterValue(h, 0x200, None, ctypes.byref(v))  # PDH_FMT_DOUBLE
                if v.status == self.NO_INSTANCE:  # e.g. an NFS/WebDAV drive, or a share that went away
                    vals = None
                    break
                vals.append(v.value if rc == 0 else 0.0)
            self.rates[key] = tuple(vals) if vals else None


class DriveScanner:
    """Re-scans the drive letters every 5 s on a background thread, together with the
    space on network and optical drives, so a slow or unreachable NAS never freezes the
    widget. Until the first scan finishes, local drives are listed directly (fast)."""
    INTERVAL = 5.0

    def __init__(self):
        self.drives: list[dict] | None = None
        self.usage: dict = {}  # root -> psutil usage, network/optical drives only
        self.busy = False
        self.last = -1e9
        self.scanned = threading.Event()  # set once the first full scan is done

    def wait(self, timeout: float) -> None:
        """Start a scan if none ran yet and wait for it (for --once, which prints a single frame)."""
        self.get()
        self.scanned.wait(timeout)

    def get(self) -> list[dict]:
        now = time.monotonic()
        if not self.busy and now - self.last >= self.INTERVAL:
            self.busy, self.last = True, now
            threading.Thread(target=self._scan, name="congee-drives", daemon=True).start()
        return self.drives if self.drives is not None else scan_drives(slow=False)

    def _scan(self):
        try:
            drives = scan_drives(slow=True)
            usage = {}
            for d in drives:
                if d["kind"] not in LOCAL_KINDS:
                    try:
                        usage[d["root"]] = psutil.disk_usage(d["root"])
                    except OSError:
                        pass
            self.drives, self.usage = drives, usage  # replace both at once; readers never see a mix
        finally:
            self.last = time.monotonic()  # next scan starts 5 s after this one ends, however long it took
            self.busy = False
            self.scanned.set()


DISPLAY_CLASS_KEY = r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
DRIVER_FIELDS = ("DriverDesc", "ProviderName", "DriverVersion", "DriverDate", "RadeonSoftwareVersion", "MatchingDeviceId")


def read_gpu_drivers() -> list[dict]:
    """Installed display drivers, from the registry's display-adapter class key.
    Works for any vendor and needs no admin rights or LibreHardwareMonitor."""
    if not IS_WINDOWS:
        return []
    import winreg
    out = []
    try:
        cls = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, DISPLAY_CLASS_KEY)
    except OSError:
        return out
    with cls:
        i = 0
        while True:
            try:
                sub = winreg.EnumKey(cls, i)
            except OSError:
                break
            i += 1
            if not sub.isdigit():  # 0000, 0001, ... (skips "Properties", "Configuration")
                continue
            try:
                with winreg.OpenKey(cls, sub) as k:
                    d = {}
                    for f in DRIVER_FIELDS:
                        try:
                            d[f] = str(winreg.QueryValueEx(k, f)[0]).strip()
                        except OSError:
                            d[f] = ""
            except OSError:
                continue
            if d["DriverDesc"] and d["DriverVersion"]:
                out.append(d)
    return out


def driver_release(d: dict) -> str:
    """The version number people know: AMD Software (Adrenalin) version, NVIDIA's release
    number (Windows version 32.0.15.8157 -> 581.57), otherwise the Windows driver version."""
    if d.get("RadeonSoftwareVersion"):
        return d["RadeonSoftwareVersion"]
    ver = d.get("DriverVersion", "")
    if "nvidia" in d.get("ProviderName", "").lower() or "VEN_10DE" in d.get("MatchingDeviceId", "").upper():
        parts = ver.split(".")
        if len(parts) == 4 and parts[2].isdigit() and parts[3].isdigit():
            digits = (parts[2] + parts[3].zfill(4))[-5:]
            return f"{int(digits[:3])}.{digits[3:]}"
    return ver


def driver_date(d: dict) -> str:
    """Registry dates are M-D-YYYY; show them as YYYY-MM-DD."""
    m = re.fullmatch(r"(\d{1,2})-(\d{1,2})-(\d{4})", d.get("DriverDate", ""))
    return f"{m[3]}-{int(m[1]):02d}-{int(m[2]):02d}" if m else d.get("DriverDate", "")


# --------------------------------------------------------------------------
# System stats, sampled once per tick
# --------------------------------------------------------------------------
class Stats:
    def __init__(self):
        self.tick = 0
        self.last_t = time.monotonic()
        psutil.cpu_percent(percpu=True)
        self.cpu_per: list[float] = [0.0] * (psutil.cpu_count() or 1)
        self.cpu_total = 0.0
        self.net_prev = psutil.net_io_counters(pernic=True)
        self.net_totals = self.net_prev
        self.net_rate: dict[str, tuple[float, float]] = {}
        self.disk_prev = psutil.disk_io_counters()
        self.disk_rate = (0.0, 0.0)
        self.history: dict[tuple, deque] = {}
        self.history_tick: dict[tuple, int] = {}
        self.procs: dict[int, psutil.Process] = {}
        self.exec_cache: dict[str, dict] = {}
        self.cpu_name = read_cpu_name()
        self.boot = psutil.boot_time()
        self.sensors = Sensors()
        self._cache: dict = {}
        self._timed: dict = {}
        self.drive_scanner = DriveScanner()
        self.drive_io = DriveIO()

    def sample(self):
        now = time.monotonic()
        dt = max(now - self.last_t, 1e-3)
        self.last_t = now
        self.tick += 1
        self._cache = {}

        per = psutil.cpu_percent(percpu=True)
        if per:
            self.cpu_per = per
            self.cpu_total = sum(per) / len(per)

        net = psutil.net_io_counters(pernic=True)
        rates = {}
        for nic, c in net.items():
            p = self.net_prev.get(nic)
            if p:
                rates[nic] = (max(c.bytes_recv - p.bytes_recv, 0) / dt,
                              max(c.bytes_sent - p.bytes_sent, 0) / dt)
        self.net_prev, self.net_totals, self.net_rate = net, net, rates

        d = psutil.disk_io_counters()
        if d and self.disk_prev:
            self.disk_rate = (max(d.read_bytes - self.disk_prev.read_bytes, 0) / dt,
                              max(d.write_bytes - self.disk_prev.write_bytes, 0) / dt)
        self.disk_prev = d
        self.drive_io.collect()

    def cached(self, key, fn):
        if key not in self._cache:
            self._cache[key] = fn()
        return self._cache[key]

    def timed(self, key, ttl: float, fn):
        """Like cached(), but keeps the value for ttl seconds (for things that rarely change)."""
        now = time.monotonic()
        hit = self._timed.get(key)
        if hit is None or now - hit[0] >= ttl:
            hit = self._timed[key] = (now, fn())
        return hit[1]

    def disk_io(self, path: str) -> tuple[float, float] | None:
        """(read, write) bytes/s of one drive ("D:" or "D:\\"), or None if Windows can't tell."""
        if not IS_WINDOWS or len(path) < 2 or path[1] != ":":
            return None
        root = path[:2].upper() + "\\"
        entry = next((d for d in self.drives() if d["root"] == root), None)
        kind = entry["kind"] if entry else DRIVE_KINDS.get(ctypes.windll.kernel32.GetDriveTypeW(root))
        share = ""
        if kind == "network":
            share = entry["label"] if entry and entry["label"].startswith("\\\\") else network_path(root[:2])[0]
            if not share:
                return None
        return self.drive_io.rate(root, share)

    def drives(self) -> list[dict]:
        """All drive letters (see scan_drives); USB sticks and mapped drives come and go on their own."""
        return self.drive_scanner.get()

    def gpu_drivers(self) -> list[dict]:
        return self.timed("gpu_drivers", 60.0, read_gpu_drivers)

    # ---- helpers -------------------------------------------------------
    def mem(self):
        return self.cached("mem", psutil.virtual_memory)

    def swap(self):
        return self.cached("swap", psutil.swap_memory)

    def battery(self):
        return self.cached("bat", lambda: psutil.sensors_battery() if hasattr(psutil, "sensors_battery") else None)

    def disk(self, path):
        path = path or "C:\\"
        if IS_WINDOWS and len(path) >= 2 and path[1] == ":":
            root = path[:2].upper() + "\\"
            kind = self.cached(("kind", root), lambda: DRIVE_KINDS.get(ctypes.windll.kernel32.GetDriveTypeW(root)))
            if kind in ("network", "optical"):
                return self.drive_scanner.usage.get(root)  # fetched in the background: never block on a NAS
        def get():
            try:
                return psutil.disk_usage(path)
            except OSError:
                return None
        return self.cached(("fs", path), get)

    def cpu_value(self, arg: str) -> float:
        arg = (arg or "").strip().lower()
        if arg.startswith("cpu") and arg[3:].isdigit():
            n = int(arg[3:])
            if n == 0:
                return self.cpu_total
            if 1 <= n <= len(self.cpu_per):
                return self.cpu_per[n - 1]
        return self.cpu_total

    @staticmethod
    def _real_nic(name: str) -> bool:
        return "loopback" not in name.lower()

    def net(self, iface: str) -> tuple[float, float]:
        if iface:
            return self.net_rate.get(iface, (0.0, 0.0))
        down = sum(r[0] for n, r in self.net_rate.items() if self._real_nic(n))
        up = sum(r[1] for n, r in self.net_rate.items() if self._real_nic(n))
        return down, up

    def net_total(self, iface: str) -> tuple[int, int]:
        if iface:
            c = self.net_totals.get(iface)
            return (c.bytes_recv, c.bytes_sent) if c else (0, 0)
        cs = [c for n, c in self.net_totals.items() if self._real_nic(n)]
        return sum(c.bytes_recv for c in cs), sum(c.bytes_sent for c in cs)

    def addr(self, iface: str) -> str:
        addrs = self.cached("addrs", psutil.net_if_addrs)
        stats = self.cached("ifstats", psutil.net_if_stats)
        names = [iface] if iface else [n for n in addrs if self._real_nic(n) and stats.get(n) and stats[n].isup]
        for n in names:
            for a in addrs.get(n, []):
                if a.family == socket.AF_INET and not a.address.startswith("169.254."):
                    return a.address
        return "No address"

    def top(self) -> list[tuple]:
        def get():
            ncpu = psutil.cpu_count() or 1
            rows, seen = [], set()
            for p in psutil.process_iter(["pid", "name"]):
                pid = p.info["pid"]
                if pid == 0:  # System Idle Process
                    continue
                proc = self.procs.setdefault(pid, p)
                try:
                    cpu = proc.cpu_percent(None) / ncpu
                    rss = proc.memory_info().rss
                except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
                    continue
                seen.add(pid)
                name = p.info["name"] or "?"
                if name.lower().endswith(".exe"):
                    name = name[:-4]
                rows.append({"cpu": cpu, "rss": rss, "pid": pid, "name": name})
            for pid in list(self.procs):
                if pid not in seen:
                    del self.procs[pid]
            total = self.mem().total
            for r in rows:
                r["mem"] = r["rss"] * 100.0 / total
            return rows
        return self.cached("top", get)

    def push_history(self, key: tuple, value: float, maxlen: int = 2000) -> deque:
        h = self.history.get(key)
        if h is None:
            h = self.history[key] = deque(maxlen=maxlen)
        if self.history_tick.get(key) != self.tick:
            h.append(value)
            self.history_tick[key] = self.tick
        return h

    def run_exec(self, cmd: str, interval: float) -> str:
        ent = self.exec_cache.setdefault(cmd, {"t": -1e9, "out": "", "busy": False})
        now = time.monotonic()
        if not ent["busy"] and now - ent["t"] >= interval:
            ent["busy"], ent["t"] = True, now
            threading.Thread(target=self._exec_worker, args=(cmd, ent), daemon=True).start()
        return ent["out"]

    @staticmethod
    def _exec_worker(cmd: str, ent: dict):
        try:
            r = subprocess.run(cmd, shell=True, capture_output=True, text=True, timeout=30,
                               creationflags=0x08000000 if IS_WINDOWS else 0)  # CREATE_NO_WINDOW
            ent["out"] = r.stdout.rstrip("\r\n")
        except Exception as e:  # noqa: BLE001 - show any failure in the widget
            ent["out"] = f"[exec error: {e}]"
        finally:
            ent["busy"] = False


# --------------------------------------------------------------------------
# Template evaluation
# --------------------------------------------------------------------------
class Evaluator:
    def __init__(self, stats: Stats, settings: dict, scale: float, is_color):
        self.st = stats
        self.cfg = settings
        self.scale = scale
        self.is_color = is_color  # callable(str) -> bool
        self.fahrenheit = settings.get("temperature_unit", "celsius").strip().lower() in ("fahrenheit", "f")
        self.drive: dict | None = None  # the current drive inside ${for_each_drive}

    def deg(self, v: float, stype: str) -> float:
        """Sensor value for display: temperatures follow temperature_unit (LHM reports °C).
        Bars and graphs keep using °C, so temp_bar_max stays in °C."""
        return v * 9 / 5 + 32 if self.fahrenheit and stype == "temperature" else v

    def px(self, v) -> int:
        return int(round(float(v) * self.scale))

    # ---- conditions ----------------------------------------------------
    def condition(self, name: str, args: str) -> bool:
        a = args.strip()
        if name == "if_up":
            s = self.st.cached("ifstats", psutil.net_if_stats).get(a)
            return bool(s and s.isup)
        if name == "if_running":
            want = a.lower().removesuffix(".exe")
            return any(r["name"].lower() == want for r in self.st.top())
        if name in ("if_existing", "if_mounted"):
            return os.path.exists(a)
        if name == "if_battery":
            return self.st.battery() is not None
        if name == "if_sensors":
            self.st.sensors.get()
            return self.st.sensors.ok
        if name == "if_gpu":
            return self.gpu() is not None
        if name == "if_gpu_driver":
            return self.gpu_driver() is not None
        if name in ("if_sensor", "if_gpu_sensor"):
            stype, _, q = a.partition(" ")
            if name == "if_gpu_sensor":
                g = self.gpu()
                return bool(g and self.st.sensors.find(stype.lower(), q, g[0]))
            return self.sensor(stype.lower(), q) is not None
        return False

    def sensor(self, stype: str, query: str) -> dict | None:
        return self.st.sensors.find(stype, query)

    def drive_root(self) -> str | None:
        return self.drive["root"] if self.drive else None

    def gpu_driver(self) -> dict | None:
        """Driver of the GPU the gpu_* variables use (matched by name). Without
        LibreHardwareMonitor: the "gpu" setting if it's a name, else a dedicated
        AMD/NVIDIA card first and Microsoft's basic display driver last."""
        drivers = self.st.gpu_drivers()
        if not drivers:
            return None
        names = []
        g = self.gpu()
        if g:
            names.append(g[1].lower())
        pref = self.cfg.get("gpu", "auto").strip().lower()
        if pref and pref != "auto" and not pref.isdigit():
            names.append(pref)
        for n in names:
            for d in drivers:
                desc = d["DriverDesc"].lower()
                if desc in n or n in desc:
                    return d

        def rank(d):
            who = (d["ProviderName"] + " " + d["DriverDesc"]).lower()
            if "microsoft" in who:
                return 3
            if "intel" in who:
                return 2  # most often the integrated GPU
            return 0 if any(v in who for v in ("nvidia", "advanced micro", "amd")) else 1
        return min(drivers, key=rank)

    # ---- device-scoped sensors (any GPU / CPU brand) ----------------------
    def gpu(self) -> tuple[str, str] | None:
        """(hw_id, name) of the GPU picked by the `gpu` setting."""
        return self.st.cached("gpu", lambda: self.st.sensors.select_gpu(self.cfg.get("gpu", "auto")))

    def cpu_hw(self) -> str | None:
        def pick():
            cpus = self.st.sensors.devices("/amdcpu") + self.st.sensors.devices("/intelcpu") \
                + self.st.sensors.devices("/cpu")
            return cpus[0][0] if cpus else None
        return self.st.cached("cpu_hw", pick)

    def device_sensor(self, var: str, override: str = "") -> dict | None:
        """Sensor for a gpu_*/cpu_* variable, trying each brand's naming in turn."""
        stype, names = DEVICE_VARS[var]
        hw = self.cpu_hw() if var.startswith("cpu_") else (self.gpu() or (None,))[0]
        if hw is None:
            return None
        return self.st.sensors.find(stype, [override] if override.strip() else names, hw)

    # ---- widgets (bars / graphs) ---------------------------------------
    def widget_args(self, args: str, default_h: int):
        h, w, colors, rest, size_seen = default_h, 0, [], [], False
        for t in split_args(args):
            if t.startswith("-"):
                continue
            if SIZE_RE.match(t) and not size_seen:
                parts = t.split(",")
                h = self.px(parts[0])
                w = self.px(parts[1]) if len(parts) > 1 else 0
                size_seen = True
            elif size_seen and self.is_color(norm_color(t)):
                colors.append(norm_color(t))
            else:
                rest.append(t)
        return h, w, colors, " ".join(rest)

    def widget(self, name: str, args: str, color: str):
        bar_h = self.px(self.cfg["default_bar_height"])
        graph_h = self.px(self.cfg["default_graph_height"])
        st = self.st

        if name.endswith("bar"):
            h, w, _, src = self.widget_args(args, bar_h)
            if name == "cpubar":
                frac = st.cpu_value(src) / 100
            elif name == "membar":
                frac = st.mem().percent / 100
            elif name == "swapbar":
                frac = st.swap().percent / 100
            elif name in ("fs_bar", "fs_bar_free"):
                d = st.disk(src or self.drive_root())
                frac = (d.percent / 100) if d else 0
                if name == "fs_bar_free":
                    frac = 1 - frac
            elif name == "battery_bar":
                b = st.battery()
                frac = (b.percent / 100) if b else 0
            elif name in ("tempbar", "loadbar", "controlbar"):
                s = self.sensor(SENSOR_WIDGET_TYPES[name], src)
                top = float(self.cfg["temp_bar_max"]) if name == "tempbar" else 100.0
                frac = (s["value"] / top) if s else 0
            elif name in DEVICE_WIDGETS:
                s = self.device_sensor(DEVICE_WIDGETS[name], src)
                top = float(self.cfg["temp_bar_max"]) if "temp" in name else 100.0
                frac = (s["value"] / top) if s else 0
            elif name == "gpu_vrambar":
                g = self.gpu()
                used, total = (st.sensors.vram(g[0], "used"), st.sensors.vram(g[0], "total")) if g else (None, None)
                frac = (used / total) if used is not None and total else 0
            else:
                return None
            return Bar(max(0.0, min(1.0, frac)), h, w, color)

        if name.endswith("graph"):
            h, w, colors, src = self.widget_args(args, graph_h)
            c1 = colors[0] if colors else color
            c2 = colors[1] if len(colors) > 1 else None
            if name == "cpugraph":
                key, val, top = ("cpu", src.lower() or "cpu0"), st.cpu_value(src), 100
            elif name == "memgraph":
                key, val, top = ("mem",), st.mem().percent, 100
            elif name in ("downspeedgraph", "upspeedgraph"):
                down, up = st.net(src)
                key, val, top = (name, src), (down if name.startswith("down") else up), None
            elif name == "diskiograph":
                key, val, top = ("diskio",), sum(st.disk_rate), None
            elif name in ("tempgraph", "fangraph", "loadgraph"):
                s = self.sensor(SENSOR_WIDGET_TYPES[name], src)
                key = (name, src.lower())
                val = s["value"] if s else 0.0
                top = {"tempgraph": float(self.cfg["temp_bar_max"]), "loadgraph": 100.0}.get(name)
            elif name in DEVICE_WIDGETS:
                s = self.device_sensor(DEVICE_WIDGETS[name], src)
                key = (name, src.lower())
                val = s["value"] if s else 0.0
                top = float(self.cfg["temp_bar_max"]) if "temp" in name else (100.0 if "load" in name else None)
            else:
                return None
            hist = st.push_history(key, val)
            peak = top or max(max(hist), 1.0 if name.endswith("fangraph") else 1024.0)
            return Graph([v / peak for v in hist], h, w, c1, c2)
        return None

    # ---- plain text variables -----------------------------------------
    def text_value(self, name: str, args: str) -> str | None:
        st, a = self.st, args.strip()
        if name == "cpu":
            return f"{st.cpu_value(a):.0f}"
        if name == "freq":
            f = psutil.cpu_freq()
            return f"{f.current:.0f}" if f else "N/A"
        if name == "freq_g":
            f = psutil.cpu_freq()
            return f"{f.current / 1000:.2f}" if f else "N/A"
        if name == "cpu_name":
            return st.cpu_name
        if name == "cpu_cores":
            return str(psutil.cpu_count(logical=False) or "?")
        if name == "cpu_threads":
            return str(psutil.cpu_count() or "?")
        if name == "mem":
            return fmt_bytes(st.mem().used)
        if name == "memmax":
            return fmt_bytes(st.mem().total)
        if name in ("memfree", "memeasyfree"):
            return fmt_bytes(st.mem().available)
        if name == "memperc":
            return f"{st.mem().percent:.0f}"
        if name == "swap":
            return fmt_bytes(st.swap().used)
        if name == "swapmax":
            return fmt_bytes(st.swap().total)
        if name == "swapperc":
            return f"{st.swap().percent:.0f}"
        if name.startswith("fs_"):
            d = st.disk(a or self.drive_root())
            if d is None:
                return "N/A"
            return {
                "fs_used": lambda: fmt_bytes(d.used),
                "fs_size": lambda: fmt_bytes(d.total),
                "fs_free": lambda: fmt_bytes(d.free),
                "fs_used_perc": lambda: f"{d.percent:.0f}",
                "fs_free_perc": lambda: f"{100 - d.percent:.0f}",
            }.get(name, lambda: None)()
        if name in ("diskio", "diskio_read", "diskio_write"):
            target = a or self.drive_root()  # a drive given, or the loop's drive; else all disks
            rate = st.disk_io(target) if target else st.disk_rate
            if rate is None:
                return "N/A"
            return fmt_bytes({"diskio": sum(rate), "diskio_read": rate[0], "diskio_write": rate[1]}[name])
        if name == "downspeed":
            return fmt_bytes(st.net(a)[0])
        if name == "upspeed":
            return fmt_bytes(st.net(a)[1])
        if name == "downspeedf":
            return f"{st.net(a)[0] / 1024:.1f}"
        if name == "upspeedf":
            return f"{st.net(a)[1] / 1024:.1f}"
        if name == "totaldown":
            return fmt_bytes(st.net_total(a)[0])
        if name == "totalup":
            return fmt_bytes(st.net_total(a)[1])
        if name == "addr":
            return st.addr(a)
        if name == "uptime":
            return fmt_duration(time.time() - st.boot)
        if name == "uptime_short":
            return fmt_duration(time.time() - st.boot, short=True)
        if name == "time":
            return datetime.datetime.now().strftime(args or "%c")
        if name == "utime":
            return datetime.datetime.now(datetime.timezone.utc).strftime(args or "%c")
        if name == "nodename":
            return platform.node()
        if name == "user_names":
            return getpass.getuser()
        if name == "sysname":
            return platform.system()
        if name == "kernel":
            return platform.version()
        if name == "os":
            return f"{platform.system()} {platform.release()}"
        if name == "machine":
            return platform.machine()
        if name == "processes":
            return str(len(psutil.pids()))
        if name in ("battery_percent", "battery_status", "battery_time"):
            b = st.battery()
            if b is None:
                return "N/A"
            if name == "battery_percent":
                return f"{b.percent:.0f}"
            if name == "battery_status":
                if b.power_plugged:
                    return "full" if b.percent >= 99 else "charging"
                return "discharging"
            if b.power_plugged or b.secsleft in (psutil.POWER_TIME_UNLIMITED, psutil.POWER_TIME_UNKNOWN):
                return ""
            return fmt_duration(b.secsleft, short=True)
        if name in ("top", "top_mem"):
            parts = a.split()
            if len(parts) != 2 or not parts[1].isdigit():
                return "?"
            field, n = parts[0], int(parts[1])
            rows = sorted(st.top(), key=lambda r: r["cpu" if name == "top" else "rss"], reverse=True)
            if not 1 <= n <= len(rows):
                return ""
            r = rows[n - 1]
            return {
                "name": lambda: r["name"][:18],
                "pid": lambda: str(r["pid"]),
                "cpu": lambda: f"{r['cpu']:.1f}",
                "mem": lambda: f"{r['mem']:.1f}",
                "mem_res": lambda: fmt_bytes(r["rss"]),
            }.get(field, lambda: "?")()
        if name in DEVICE_VARS:
            s = self.device_sensor(name, a)
            return f"{self.deg(s['value'], DEVICE_VARS[name][0]):.0f}" if s else "N/A"
        if name == "temp_unit":
            return "°F" if self.fahrenheit else "°C"
        if name == "drive":
            return self.drive["root"][:2] if self.drive else ""
        if name == "drive_label":
            return self.drive["label"] if self.drive else ""
        if name == "drive_type":
            return self.drive["kind"] if self.drive else ""
        if name in ("gpu_driver", "gpu_driver_version", "gpu_driver_date", "gpu_driver_name"):
            d = self.gpu_driver()
            if not d:
                return "N/A"
            return {"gpu_driver": driver_release, "gpu_driver_date": driver_date,
                    "gpu_driver_version": lambda x: x["DriverVersion"],
                    "gpu_driver_name": lambda x: x["DriverDesc"]}[name](d)
        if name == "gpu_name":
            g = self.gpu()
            return g[1] if g else "No GPU found"
        if name in ("gpu_vram_used", "gpu_vram_total", "gpu_vram_perc"):
            g = self.gpu()
            used = st.sensors.vram(g[0], "used") if g else None
            total = st.sensors.vram(g[0], "total") if g else None
            if name == "gpu_vram_used":
                return fmt_bytes(used) if used is not None else "N/A"
            if name == "gpu_vram_total":
                return fmt_bytes(total) if total else "N/A"
            return f"{used * 100 / total:.0f}" if used is not None and total else "N/A"
        if name == "gpu_sensor":
            stype, _, q = a.partition(" ")
            g = self.gpu()
            s = st.sensors.find(stype.lower(), q, g[0]) if g else None
            if not s:
                return "N/A"
            v = self.deg(s["value"], stype.lower())
            return f"{v:.0f}" if abs(v) >= 100 or v == int(v) else f"{v:.1f}"
        if name in ("temp", "fan"):
            s = self.sensor("temperature" if name == "temp" else "fan", a)
            return f"{self.deg(s['value'], 'temperature' if name == 'temp' else 'fan'):.0f}" if s else "N/A"
        if name == "sensor":
            stype, _, q = a.partition(" ")
            s = self.sensor(stype.lower(), q)
            if not s:
                return "N/A"
            v = self.deg(s["value"], stype.lower())
            return f"{v:.0f}" if abs(v) >= 100 or v == int(v) else f"{v:.1f}"
        if name == "exec":
            return st.run_exec(args, float(self.cfg["update_interval"]))
        if name == "execi":
            interval, _, cmd = args.partition(" ")
            try:
                return st.run_exec(cmd.strip(), float(interval))
            except ValueError:
                return "[execi needs: interval command]"
        return None

    # ---- whole template -> lines of segments ----------------------------
    def expand_loops(self, tokens: list[tuple]) -> list[tuple]:
        """${for_each_drive [local]} BODY ${endfor} -> BODY once per drive letter, each copy
        preceded by a ("drive", info) marker that build() uses as the fs_* default.
        "local" leaves out network and optical drives."""
        out, i = [], 0
        while i < len(tokens):
            tok = tokens[i]
            if not (tok[0] == "var" and tok[1] == "for_each_drive"):
                out.append(tok)
                i += 1
                continue
            depth, j = 1, i + 1
            while j < len(tokens):
                t = tokens[j]
                if t[0] == "var" and t[1] == "for_each_drive":
                    depth += 1
                elif t[0] == "var" and t[1] == "endfor":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            body = self.expand_loops(tokens[i + 1:j])
            local_only = tok[2].strip().lower() == "local"
            for d in self.st.drives():
                if local_only and d["kind"] not in LOCAL_KINDS:
                    continue
                out.append(("drive", d))
                out.extend(body)
            out.append(("drive", None))
            i = j + 1
        return out

    def build(self, tokens: list[tuple]) -> list[list]:
        tokens = self.expand_loops(tokens)
        cfg = self.cfg
        default_color = norm_color(cfg["default_color"])
        default_font = cfg["font"]
        upper = yes(cfg["uppercase"])
        color, font = default_color, default_font
        lines: list[list] = [[]]
        stack: list[tuple[bool, bool]] = []
        active = True

        def add_text(s: str):
            for i, part in enumerate(s.split("\n")):
                if i:
                    lines.append([])
                if part:
                    lines[-1].append(Text(part.upper() if upper else part, color, font))

        for tok in tokens:
            if tok[0] == "lit":
                if active:
                    add_text(tok[1])
                continue
            if tok[0] == "drive":
                self.drive = tok[1]
                continue
            _, name, args, raw = tok

            if name.startswith("if_"):
                taken = active and self.condition(name, args)
                stack.append((active, taken))
                active = taken
                continue
            if name == "else":
                if stack:
                    parent, taken = stack[-1]
                    active = parent and not taken
                    stack[-1] = (parent, True)
                continue
            if name == "endif":
                if stack:
                    active = stack.pop()[0]
                continue
            if not active:
                continue

            if name == "color":
                color = norm_color(args) if args else default_color
            elif re.fullmatch(r"color\d", name):
                color = norm_color(cfg.get(name, default_color))
            elif name == "font":
                font = args or default_font
            elif name == "alignr":
                lines[-1].append(ALIGNR)
            elif name == "alignc":
                lines[-1].append(ALIGNC)
            elif name == "goto":
                lines[-1].append(Goto(self.px(args or 0)))
            elif name == "offset":
                lines[-1].append(Offset(self.px(args or 0)))
            elif name in ("hr", "stippled_hr"):
                lines[-1].append(HR(max(1, self.px(args or 1)), color, name == "stippled_hr"))
            elif name.endswith("bar") or name.endswith("graph"):
                seg = self.widget(name, args, color)
                if seg is None:
                    add_text(raw)
                else:
                    lines[-1].append(seg)
            else:
                v = self.text_value(name, args)
                add_text(raw if v is None else v)
        return lines


# --------------------------------------------------------------------------
# Win32 helpers
# --------------------------------------------------------------------------
if IS_WINDOWS:
    user32 = ctypes.windll.user32
    user32.GetWindowLongW.restype = ctypes.c_long
    user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
    user32.SetWindowLongW.restype = ctypes.c_long
    user32.SetWindowLongW.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_long]
    user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int, ctypes.c_uint]
    user32.FindWindowW.restype = wt.HWND
    user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
    user32.GetWindow.restype = wt.HWND
    user32.GetWindow.argtypes = [wt.HWND, ctypes.c_uint]
    user32.IsWindowVisible.argtypes = [wt.HWND]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_APPWINDOW = 0x00040000
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000
HWND_TOP, HWND_TOPMOST, HWND_NOTOPMOST = 0, -1, -2
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x1, 0x2, 0x10
GW_HWNDPREV = 3


def set_dpi_awareness():
    if not IS_WINDOWS:
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # per-monitor aware
    except Exception:  # noqa: BLE001
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:  # noqa: BLE001
            pass


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT),
                ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


def monitor_work_areas(root: tk.Tk) -> list[tuple[int, int, int, int]]:
    """Work areas (excluding taskbar) of each monitor, primary first."""
    if not IS_WINDOWS:
        return [(0, 0, root.winfo_screenwidth(), root.winfo_screenheight())]
    found = []
    proc_t = ctypes.WINFUNCTYPE(ctypes.c_int, wt.HMONITOR, wt.HDC, ctypes.POINTER(wt.RECT), wt.LPARAM)

    def cb(hmon, _hdc, _rect, _lp):
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        if user32.GetMonitorInfoW(hmon, ctypes.byref(mi)):
            r = mi.rcWork
            found.append((bool(mi.dwFlags & 1), (r.left, r.top, r.right, r.bottom)))
        return 1

    user32.EnumDisplayMonitors(None, None, proc_t(cb), 0)
    found.sort(key=lambda t: not t[0])
    return [a for _, a in found] or [(0, 0, root.winfo_screenwidth(), root.winfo_screenheight())]


# --------------------------------------------------------------------------
# Systray icon
# --------------------------------------------------------------------------
ICON_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "congee.ico")

if IS_WINDOWS:
    LRESULT = ctypes.c_ssize_t
    WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)

    class WNDCLASSW(ctypes.Structure):
        _fields_ = [("style", wt.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                    ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE), ("hIcon", wt.HICON),
                    ("hCursor", wt.HANDLE), ("hbrBackground", wt.HBRUSH), ("lpszMenuName", wt.LPCWSTR),
                    ("lpszClassName", wt.LPCWSTR)]

    class NOTIFYICONDATAW(ctypes.Structure):
        _fields_ = [("cbSize", wt.DWORD), ("hWnd", wt.HWND), ("uID", wt.UINT), ("uFlags", wt.UINT),
                    ("uCallbackMessage", wt.UINT), ("hIcon", wt.HICON), ("szTip", wt.WCHAR * 128),
                    ("dwState", wt.DWORD), ("dwStateMask", wt.DWORD), ("szInfo", wt.WCHAR * 256),
                    ("uVersion", wt.UINT), ("szInfoTitle", wt.WCHAR * 64), ("dwInfoFlags", wt.DWORD),
                    ("guidItem", ctypes.c_ubyte * 16), ("hBalloonIcon", wt.HICON)]

    user32.DefWindowProcW.restype = LRESULT
    user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
    user32.CreateWindowExW.restype = wt.HWND
    user32.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int,
                                       ctypes.c_int, ctypes.c_int, wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID]
    user32.LoadImageW.restype = wt.HANDLE
    user32.LoadImageW.argtypes = [wt.HINSTANCE, wt.LPCWSTR, wt.UINT, ctypes.c_int, ctypes.c_int, wt.UINT]
    user32.LoadIconW.restype = wt.HICON
    user32.LoadIconW.argtypes = [wt.HINSTANCE, wt.LPVOID]
    user32.CreatePopupMenu.restype = wt.HMENU
    user32.AppendMenuW.argtypes = [wt.HMENU, wt.UINT, ctypes.c_size_t, wt.LPCWSTR]
    user32.TrackPopupMenuEx.argtypes = [wt.HMENU, wt.UINT, ctypes.c_int, ctypes.c_int, wt.HWND, wt.LPVOID]
    user32.DestroyMenu.argtypes = [wt.HMENU]
    user32.SetForegroundWindow.argtypes = [wt.HWND]
    user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
    user32.DestroyWindow.argtypes = [wt.HWND]
    user32.DestroyIcon.argtypes = [wt.HICON]
    user32.GetMessageW.argtypes = [ctypes.POINTER(wt.MSG), wt.HWND, wt.UINT, wt.UINT]
    user32.TranslateMessage.argtypes = [ctypes.POINTER(wt.MSG)]
    user32.DispatchMessageW.restype = LRESULT
    user32.DispatchMessageW.argtypes = [ctypes.POINTER(wt.MSG)]
    shell32 = ctypes.windll.shell32
    shell32.Shell_NotifyIconW.argtypes = [wt.DWORD, ctypes.POINTER(NOTIFYICONDATAW)]


# --------------------------------------------------------------------------
# LibreHardwareMonitor: congee needs it for temperatures, fans and GPU stats.
# It must run as administrator, so starting it shows Windows' admin prompt.
# --------------------------------------------------------------------------
LHM_EXE = "LibreHardwareMonitor.exe"
# CONGEE_TEST_LHM_PROCESS=<name> makes congee treat that process as LHM, to test start/stop prompts
# without touching the real one.
LHM_PROC = (os.environ.get("CONGEE_TEST_LHM_PROCESS") or "LibreHardwareMonitor") + ".exe"


def lhm_running() -> bool:
    for proc in psutil.process_iter(["name"]):
        if (proc.info["name"] or "").lower() == LHM_PROC.lower():
            return True
    return False


def find_lhm_exe() -> str | None:
    """LibreHardwareMonitor.exe from a winget install (per user or machine), Program Files, or
    wherever Windows' installed-apps list says it is; None if it isn't installed."""
    import glob
    local = os.environ.get("LOCALAPPDATA", "")
    pf = os.environ.get("ProgramFiles", r"C:\Program Files")
    for packages in (os.path.join(local, r"Microsoft\WinGet\Packages"), os.path.join(pf, r"WinGet\Packages")):
        for d in glob.glob(os.path.join(packages, "LibreHardwareMonitor.LibreHardwareMonitor_*")):
            if os.path.isfile(os.path.join(d, LHM_EXE)):
                return os.path.join(d, LHM_EXE)
    if os.path.isfile(os.path.join(pf, "LibreHardwareMonitor", LHM_EXE)):
        return os.path.join(pf, "LibreHardwareMonitor", LHM_EXE)
    import winreg
    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        try:
            with winreg.OpenKey(hive, r"Software\Microsoft\Windows\CurrentVersion\Uninstall") as un:
                for i in range(winreg.QueryInfoKey(un)[0]):
                    try:
                        with winreg.OpenKey(un, winreg.EnumKey(un, i)) as k:
                            name = winreg.QueryValueEx(k, "DisplayName")[0]
                            loc = winreg.QueryValueEx(k, "InstallLocation")[0]
                    except OSError:
                        continue
                    if str(name).lower().startswith("librehardwaremonitor") and os.path.isfile(os.path.join(loc, LHM_EXE)):
                        return os.path.join(loc, LHM_EXE)
        except OSError:
            pass
    return None


def start_lhm(exe: str) -> bool:
    """Start it as administrator (Windows asks for permission). False if declined or failed."""
    try:
        os.startfile(exe, "runas", cwd=os.path.dirname(exe))
        Log.write("started LibreHardwareMonitor")
        return True
    except OSError as e:
        if getattr(e, "winerror", None) == 1223:  # ERROR_CANCELLED: "No" on the admin prompt
            Log.write("LibreHardwareMonitor not started: the admin prompt was declined")
        else:
            Log.write(f"couldn't start LibreHardwareMonitor: {e}")
        return False


def lhm_is_local(sensors_url: str) -> bool:
    """True when sensors_url points at this PC (otherwise LHM runs somewhere else)."""
    from urllib.parse import urlparse
    host = (urlparse(sensors_url.strip()).hostname or "").lower()
    return host in ("localhost", "127.0.0.1", "::1") or host == socket.gethostname().lower()


def lhm_dialog(root, heading: str, text: str, yes_label: str, no_label: str = "Not now") -> tuple[bool, bool]:
    """Heading, text, 'Remember my choice', and yes / no buttons. Returns (yes, remember)."""
    result = {"yes": False}
    win = tk.Toplevel(root)
    win.title("congee")
    win.resizable(False, False)
    win.attributes("-topmost", True)
    frame = tk.Frame(win, padx=20, pady=18)
    frame.pack()
    tk.Label(frame, text=heading, font=("Segoe UI", 12, "bold")).pack(anchor="w", pady=(0, 8))
    tk.Label(frame, justify="left", wraplength=420, font=("Segoe UI", 9), text=text).pack(anchor="w")
    remember = tk.BooleanVar(win, False)
    tk.Checkbutton(frame, text="Remember my choice", variable=remember, font=("Segoe UI", 9)).pack(anchor="w", pady=(12, 0))
    buttons = tk.Frame(frame)
    buttons.pack(anchor="e", pady=(14, 0))

    def choose(yes_: bool):
        result["yes"] = yes_
        win.destroy()
    yes_btn = tk.Button(buttons, text=yes_label, width=max(11, len(yes_label) + 2), default="active", command=lambda: choose(True))
    yes_btn.pack(side="left", padx=(0, 8))
    tk.Button(buttons, text=no_label, width=max(11, len(no_label) + 2), command=lambda: choose(False)).pack(side="left")
    win.bind("<Return>", lambda e: choose(True))
    win.bind("<Escape>", lambda e: choose(False))
    win.protocol("WM_DELETE_WINDOW", lambda: choose(False))
    center(win)
    win.focus_force()
    yes_btn.focus_set()
    win.grab_set()
    root.wait_window(win)
    return result["yes"], remember.get()


def center(win):
    win.update_idletasks()
    w, h = win.winfo_reqwidth(), win.winfo_reqheight()
    win.geometry(f"+{(win.winfo_screenwidth() - w) // 2}+{(win.winfo_screenheight() - h) // 2}")
    win.deiconify()


def ask_start_lhm(root) -> tuple[bool, bool]:
    """'Start LibreHardwareMonitor?' dialog. Returns (start, remember)."""
    return lhm_dialog(
        root, "Start LibreHardwareMonitor?",
        "LibreHardwareMonitor isn't running. congee needs it for temperatures, fan speeds, and GPU load, "
        "clocks, power and memory. Everything else works without it.\n\n"
        "It needs administrator rights to read the hardware sensors, so Windows will ask for permission.",
        "Start it")


def ask_stop_lhm(root) -> tuple[bool, bool]:
    """'Stop LibreHardwareMonitor too?' dialog, shown on Exit congee. Returns (stop, remember)."""
    return lhm_dialog(
        root, "Stop LibreHardwareMonitor too?",
        "congee is closing. LibreHardwareMonitor is still running in the background to read temperatures, fans "
        "and GPU stats. Stop it as well?\n\n"
        "Keep it running if other apps use it. If Windows doesn't let congee stop it, Windows asks for "
        "administrator permission.",
        "Stop it", "Keep it running")


def stop_lhm() -> bool:
    """End every LibreHardwareMonitor process. It closes to the tray instead of exiting when asked nicely,
    so it is ended directly; if Windows won't allow that (it runs as administrator), taskkill is run as
    administrator, which shows the admin prompt. False if it's still running."""
    denied = False
    procs = [p for p in psutil.process_iter(["name"]) if (p.info["name"] or "").lower() == LHM_PROC.lower()]
    for p in procs:
        try:
            p.kill()
        except psutil.AccessDenied:
            denied = True
        except psutil.NoSuchProcess:
            pass
    psutil.wait_procs(procs, timeout=3)
    if denied:
        try:
            os.startfile("taskkill.exe", "runas", f"/F /IM {LHM_PROC}", show_cmd=0)  # SW_HIDE
        except OSError as e:
            if getattr(e, "winerror", None) == 1223:  # ERROR_CANCELLED: "No" on the admin prompt
                Log.write("LibreHardwareMonitor not stopped: the admin prompt was declined")
            else:
                Log.write(f"couldn't stop LibreHardwareMonitor: {e}")
            return False
        for _ in range(20):  # taskkill runs on its own; give it up to 5 s
            if not lhm_running():
                break
            time.sleep(0.25)
    stopped = not lhm_running()
    Log.write("stopped LibreHardwareMonitor" if stopped else "couldn't stop LibreHardwareMonitor")
    return stopped


LHM_WINGET_ID = "LibreHardwareMonitor.LibreHardwareMonitor"
LHM_RELEASES_URL = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases"
LHM_WINGET_ARGS = ["install", "--id", LHM_WINGET_ID, "-e", "--source", "winget", "--accept-package-agreements",
                   "--accept-source-agreements", "--disable-interactivity"]
# 0 = installed; these two mean it was there already, which is fine too.
WINGET_OK = {0, 0x8A15002B, 0x8A150061}  # no upgrade available, already installed


def winget_exe() -> str | None:
    """winget.exe (the App Installer alias), or None if it isn't there."""
    p = os.path.join(os.environ.get("LOCALAPPDATA", ""), r"Microsoft\WindowsApps\winget.exe")
    return p if os.path.isfile(p) else None


def ask_install_lhm(root, have_winget: bool) -> tuple[bool, bool]:
    """'Install LibreHardwareMonitor?' dialog. Returns (install, remember). Without winget the
    yes button opens LHM's download page instead."""
    return lhm_dialog(
        root, "Install LibreHardwareMonitor?",
        "LibreHardwareMonitor isn't installed. congee needs it for temperatures, fan speeds, and GPU load, "
        "clocks, power and memory. Everything else works without it. It's free and open source.\n\n" +
        ("congee can install it for you with winget (Windows' package manager) and set it up to share its "
         "sensors with congee. It needs administrator rights to read the hardware sensors, so Windows will "
         "ask for permission when it starts." if have_winget else
         "winget (Windows' package manager) isn't available to install it automatically. You can download it "
         "from its GitHub releases page, unzip it, and run LibreHardwareMonitor.exe as administrator. Turn on "
         "Options > Remote Web Server > Run so congee can read it."),
        "Install" if have_winget else "Open download page")


def ensure_lhm_web_server_config(exe: str):
    """Write LibreHardwareMonitor.config next to the exe, if there isn't one, so it runs its web
    server on port 8085 (what congee reads) and starts/closes to the tray. Same as setup."""
    cfg = os.path.join(os.path.dirname(exe), "LibreHardwareMonitor.config")
    if os.path.exists(cfg):
        return
    try:
        with open(cfg, "w", encoding="utf-8", newline="\r\n") as f:
            f.write('<?xml version="1.0" encoding="utf-8"?>\n'
                    "<configuration>\n"
                    "  <appSettings>\n"
                    '    <add key="runWebServerMenuItem" value="true" />\n'
                    '    <add key="listenerPort" value="8085" />\n'
                    '    <add key="startMinMenuItem" value="true" />\n'
                    '    <add key="minTrayMenuItem" value="true" />\n'
                    '    <add key="minCloseMenuItem" value="true" />\n'
                    "  </appSettings>\n"
                    "</configuration>\n")
        Log.write(f"wrote {cfg} (web server on port 8085)")
    except OSError as e:
        Log.write(f"couldn't write {cfg}: {e}")


def install_lhm(root, winget: str, done):
    """Install LHM with winget, hidden, in a background thread while a small 'Installing...'
    window shows. Calls done(exe) on the Tk thread; exe is None after showing why it failed."""
    from tkinter import messagebox, ttk
    Log.write("installing LibreHardwareMonitor with winget")
    win = tk.Toplevel(root)
    win.title("congee")
    win.resizable(False, False)
    win.attributes("-topmost", True)
    frame = tk.Frame(win, padx=20, pady=18)
    frame.pack()
    tk.Label(frame, text="Installing LibreHardwareMonitor...", font=("Segoe UI", 11, "bold")).pack(anchor="w", pady=(0, 8))
    bar = ttk.Progressbar(frame, mode="indeterminate", length=360)
    bar.pack()
    bar.start(12)
    tk.Label(frame, text="This usually takes under a minute.", fg="gray", font=("Segoe UI", 9)).pack(anchor="w", pady=(8, 0))
    center(win)
    result = {}

    def work():
        try:
            p = subprocess.run([winget] + LHM_WINGET_ARGS, capture_output=True, text=True, errors="replace",
                               creationflags=subprocess.CREATE_NO_WINDOW)
            result["code"], result["out"] = p.returncode & 0xFFFFFFFF, (p.stdout or "") + (p.stderr or "")
        except OSError as e:
            result["code"], result["out"] = -1, str(e)
    t = threading.Thread(target=work, daemon=True)
    t.start()

    def poll():
        if t.is_alive():
            root.after(250, poll)
            return
        win.destroy()
        code, out = result["code"], result["out"]
        last = next((ln.strip() for ln in reversed(out.splitlines()) if any(c.isalpha() for c in ln)), "")
        ok = code in WINGET_OK
        exe = find_lhm_exe()
        if ok and exe:
            Log.write(f"installed LibreHardwareMonitor: {exe}")
            ensure_lhm_web_server_config(exe)
            if code == 0:  # newly installed (not "already there"): congee's uninstaller offers to remove it
                try:
                    import winreg
                    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\congee\InstalledDeps") as k:
                        winreg.SetValueEx(k, LHM_WINGET_ID, 0, winreg.REG_SZ, "LibreHardwareMonitor")
                except OSError as e:
                    Log.write(f"couldn't record the LibreHardwareMonitor install: {e}")
            done(exe)
            return
        why = ("winget finished, but LibreHardwareMonitor.exe couldn't be found afterwards." if ok
               else f"winget failed (exit code 0x{code & 0xFFFFFFFF:08X}).")
        Log.write(f"LibreHardwareMonitor install failed: {why} {last}")
        messagebox.showwarning(
            "congee", f"Couldn't install LibreHardwareMonitor. {why}\n\n{last}\n\n"
                      f"You can install it yourself from a terminal:\n    winget install --id {LHM_WINGET_ID}\n"
                      f"or download it from:\n    {LHM_RELEASES_URL}", parent=root)
        done(None)
    root.after(250, poll)


class Tray:
    """Notification-area icon with a native right-click menu (same as the .NET edition).
    It runs on its own thread with its own Win32 message loop: tkinter must not be called
    from inside a foreign window procedure, so a chosen menu item is only put on a queue,
    and the Tk thread picks it up with poll() and runs it as a normal Tk callback."""
    WM_TRAY = 0x8001
    WM_LBUTTONUP, WM_RBUTTONUP = 0x0202, 0x0205
    WM_CLOSE, WM_DESTROY = 0x0010, 0x0002

    def __init__(self, tip: str, items: list, on_close):
        import queue
        self.tip, self.items = tip, items  # items: (label | callable -> label, action | None)
        self.on_close = on_close  # run if something else closes the tray window (e.g. setup upgrading congee)
        self.closing = False
        self.actions: queue.SimpleQueue = queue.SimpleQueue()
        self.hwnd = None
        self.hicon = None
        self.added = False
        self.taskbar_created = -1
        ready = threading.Event()
        threading.Thread(target=self._run, args=(ready,), name="congee-tray", daemon=True).start()
        ready.wait(5)

    def poll(self):
        """Run any menu choices made since the last call (call this on the Tk thread)."""
        while not self.actions.empty():
            self.actions.get_nowait()()

    # ---- tray thread --------------------------------------------------------
    def _run(self, ready):
        try:
            try:  # dark menu when Windows apps are set to dark (uxtheme ordinals 135/136)
                ctypes.windll.uxtheme[135](1)
                ctypes.windll.uxtheme[136]()
            except Exception:  # noqa: BLE001
                pass
            hinst = ctypes.windll.kernel32.GetModuleHandleW(None)
            self._proc = WNDPROC(self._wndproc)  # keep a reference or ctypes frees the callback
            wc = WNDCLASSW(lpfnWndProc=self._proc, hInstance=hinst, lpszClassName="congee_tray")
            user32.RegisterClassW(ctypes.byref(wc))
            self.taskbar_created = user32.RegisterWindowMessageW("TaskbarCreated")
            self.hwnd = user32.CreateWindowExW(0, "congee_tray", "congee tray", 0, 0, 0, 0, 0,
                                               None, None, hinst, None)
            size = user32.GetSystemMetricsForDpi(49, user32.GetDpiForSystem())  # SM_CXSMICON
            if os.path.exists(ICON_FILE):
                self.hicon = user32.LoadImageW(None, ICON_FILE, 1, size, size, 0x10)  # LR_LOADFROMFILE
            self.hicon = self.hicon or user32.LoadIconW(None, ctypes.c_void_p(32512))  # IDI_APPLICATION
            self._notify(0)  # NIM_ADD
            try:  # WM_WTSSESSION_CHANGE on lock/unlock, for the log
                ctypes.windll.wtsapi32.WTSRegisterSessionNotification(self.hwnd, 0)
            except (OSError, AttributeError):
                pass
        finally:
            ready.set()
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))

    def _data(self):
        d = NOTIFYICONDATAW()
        d.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
        d.hWnd, d.uID = self.hwnd, 1
        d.uFlags = 1 | 2 | 4  # NIF_MESSAGE | NIF_ICON | NIF_TIP
        d.uCallbackMessage, d.hIcon, d.szTip = self.WM_TRAY, self.hicon, self.tip[:127]
        return d

    def _notify(self, msg):
        ok = bool(shell32.Shell_NotifyIconW(msg, ctypes.byref(self._data())))
        if msg == 0:
            self.added = ok

    def _wndproc(self, hwnd, msg, wparam, lparam):
        if msg == self.WM_TRAY and (lparam & 0xFFFF) in (self.WM_LBUTTONUP, self.WM_RBUTTONUP):
            self._show_menu()
            return 0
        if msg == self.taskbar_created:  # Explorer restarted: the old icon is gone
            self._notify(0)
        elif msg == self.WM_CLOSE and not self.closing:  # setup, uninstall, stop-congee.ps1
            Log.stop_reason("asked to close by another program (setup, uninstall or stop-congee.ps1)")
        elif msg == 0x0016 and wparam:  # WM_ENDSESSION: Windows may end the process right after this
            Log.stop_reason("Windows is signing out or shutting down")
            Log.stopped(0)
        elif msg == 0x0218:  # WM_POWERBROADCAST
            if wparam == 4:  # PBT_APMSUSPEND
                Log.write("PC going to sleep")
            elif wparam == 0x12:  # PBT_APMRESUMEAUTOMATIC
                Log.write("PC woke up")
        elif msg == 0x02B1:  # WM_WTSSESSION_CHANGE
            if wparam == 7:
                Log.write("screen locked")
            elif wparam == 8:
                Log.write("screen unlocked")
        elif msg == self.WM_DESTROY:  # Exit, or another program sending WM_CLOSE: don't leave a dead icon
            if self.added:
                self._notify(2)
                self.added = False
            if not self.closing:
                self.actions.put(self.on_close)
            user32.PostQuitMessage(0)
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    def _show_menu(self):
        menu = user32.CreatePopupMenu()
        for i, (label, action, *checked) in enumerate(self.items):
            if action is None:
                user32.AppendMenuW(menu, 0x800, 0, None)  # MF_SEPARATOR
            else:
                flags = 0x8 if checked and checked[0]() else 0  # MF_CHECKED
                user32.AppendMenuW(menu, flags, i + 1, label() if callable(label) else label)
        pt = wt.POINT()
        user32.GetCursorPos(ctypes.byref(pt))
        user32.SetForegroundWindow(self.hwnd)  # so the menu closes when you click elsewhere
        cmd = user32.TrackPopupMenuEx(menu, 0x2 | 0x100 | 0x80, pt.x, pt.y, self.hwnd, None)
        user32.PostMessageW(self.hwnd, 0, 0, 0)
        user32.DestroyMenu(menu)
        if 0 < cmd <= len(self.items) and self.items[cmd - 1][1]:
            self.actions.put(self.items[cmd - 1][1])

    def remove(self):
        """Take the icon out of the tray and end the tray thread (any thread may call this)."""
        self.closing = True
        if self.added:
            self._notify(2)  # NIM_DELETE
            self.added = False
        if self.hwnd:
            user32.PostMessageW(self.hwnd, self.WM_CLOSE, 0, 0)  # DefWindowProc destroys it on its own thread


# --------------------------------------------------------------------------
# The window
# --------------------------------------------------------------------------
class Congee:
    MIN_FLEX = 20

    def __init__(self, config_path: str):
        self.config_path = os.path.abspath(config_path)
        self.root = tk.Tk()
        self.root.withdraw()
        self.root.title("congee")
        self.root.overrideredirect(True)
        self.canvas = tk.Canvas(self.root, highlightthickness=0, bd=0)
        self.canvas.pack(fill="both", expand=True)
        self.fonts: dict[str, tkfont.Font] = {}
        self.font_metrics: dict[str, dict] = {}
        self.rgb_cache: dict[str, tuple] = {}
        self.stats = Stats()
        self.hwnd = None
        self.user_pos = None
        self.geom = None
        self.after_ids: list[str] = []
        self.drag_from = None

        # One menu, shown from the tray icon and (when click_through is off) by right-clicking the widget
        self.hidden = False
        items = [
            (lambda: "Show widget" if self.hidden else "Hide widget", self.toggle_visible),
            ("Edit config", lambda: os.startfile(self.config_path)),
            ("Open config folder", lambda: os.startfile(os.path.dirname(self.config_path))),
            ("Reload config", self.reload),
            ("Reset position", self.reset_position),
            ("Logging", self.toggle_logging, lambda: Log.enabled),
            ("", None),
            ("Exit congee", self.exit_from_menu),
        ]
        self.menu = tk.Menu(self.root, tearoff=0)
        self.menu_refresh = []  # labels and checkmarks that change, updated before the menu opens
        for label, action, *checked in items:
            if action is None:
                self.menu.add_separator()
            elif checked:
                var = tk.BooleanVar(self.root, value=checked[0]())
                self.menu.add_checkbutton(label=label, variable=var, command=action)
                self.menu_refresh.append(lambda var=var, get=checked[0]: var.set(get()))
            else:
                self.menu.add_command(label=label() if callable(label) else label, command=action)
                if callable(label):
                    idx = self.menu.index("end")
                    self.menu_refresh.append(lambda idx=idx, get=label: self.menu.entryconfigure(idx, label=get()))
        self.canvas.bind("<Button-3>", self.show_menu)
        self.root.protocol("WM_DELETE_WINDOW", self.quit)
        # Tk prints callback errors to stderr, which pythonw throws away: log them instead
        self.root.report_callback_exception = lambda kind, exc, tb: Log.once(
            "error: " + "".join(__import__("traceback").format_exception(kind, exc, tb)).rstrip())
        self.root.after(3600_000, self.heartbeat)
        # Offer to start LibreHardwareMonitor if it isn't running. Right after sign-in, wait a
        # minute first: LHM's own "Run On Windows Startup" may still be about to start it.
        if IS_WINDOWS:
            self.root.after(60_000 if time.time() - psutil.boot_time() < 180 else 3_000, self.check_lhm)
        name = os.path.basename(self.config_path)
        self.tray = None
        if IS_WINDOWS:
            try:
                self.tray = Tray("congee" if name == "congee.conf" else f"congee - {name}", items, self.quit)
                self.root.after(200, self.poll_tray)
            except Exception as e:  # noqa: BLE001  (the widget still works without a tray icon)
                print(f"congee: no tray icon: {e}", file=sys.stderr)
        self.canvas.bind("<ButtonPress-1>", self.on_press)
        self.canvas.bind("<B1-Motion>", self.on_drag)

        self.reload()
        self.root.deiconify()
        self.root.update_idletasks()
        self.apply_window_styles()

    # ---- config ---------------------------------------------------------
    def reload(self):
        try:
            self.settings, text = parse_config(self.config_path)
            self.config_error = None
            Log.set_enabled(yes(self.settings["logging"]))
        except Exception as e:  # noqa: BLE001
            self.settings, text = dict(DEFAULTS), ""
            self.config_error = f"Config error: {e}"
            Log.once(self.config_error)
        try:
            self.mtime = os.path.getmtime(self.config_path)
        except OSError:
            self.mtime = 0
        s = self.settings
        a = s["alignment"].strip().lower()
        s["alignment"] = ALIGN_ALIASES.get(a, a)
        if s["dpi_scale"].strip().lower() == "auto":
            self.scale = self.root.winfo_fpixels("1i") / 96.0
        else:
            self.scale = float(s["dpi_scale"])
        self.tokens = tokenize(text)
        self.stats.sensors.configure(s["sensors_url"], float(s["update_interval"]))
        self.fonts.clear()
        self.font_metrics.clear()
        self.evaluator = Evaluator(self.stats, s, self.scale, self.is_color)

        self.transparent = yes(s["own_window_transparent"])
        if self.transparent:
            bg = KEY_COLOR
            self.root.wm_attributes("-alpha", 1.0)
            self.root.wm_attributes("-transparentcolor", KEY_COLOR)
        else:
            bg = norm_color(s["own_window_colour"])
            self.root.wm_attributes("-transparentcolor", "")
            self.root.wm_attributes("-alpha", max(0.05, min(1.0, int(s["own_window_argb_value"]) / 255)))
        self.root.configure(bg=bg)
        self.canvas.configure(bg=bg)
        self.window_type = s["own_window_type"].strip().lower()
        self.root.wm_attributes("-topmost", self.window_type in ("override", "topmost", "panel", "dock"))

        for aid in self.after_ids:
            self.root.after_cancel(aid)
        self.after_ids = []
        self.geom = None
        if self.hwnd:
            self.apply_window_styles()
        self.tick()
        if self.window_type == "desktop":
            self.pin_loop()

    def reset_position(self):
        self.user_pos = None
        self.geom = None
        self.render()

    def poll_tray(self):
        if self.tray:
            self.tray.poll()
            self.root.after(200, self.poll_tray)

    def toggle_visible(self):
        self.hidden = not self.hidden
        if self.hidden:
            self.root.withdraw()
        else:
            self.root.deiconify()
            self.root.update_idletasks()
            self.apply_window_styles()

    def show_menu(self, e):
        for refresh in self.menu_refresh:
            refresh()
        self.menu.tk_popup(e.x_root, e.y_root)

    def toggle_logging(self):
        """Flip the "logging" setting in the config file (so it sticks) and apply it."""
        on = not Log.enabled
        try:
            set_setting(self.config_path, "logging", "yes" if on else "no",
                        r"Write congee.log (starts, stops and crashes) to %APPDATA%\congee: yes or no")
        except OSError:  # config can't be saved: switch for this run only
            Log.set_enabled(on)
            return
        self.reload()  # picks up the new setting

    def check_lhm(self):
        """If LibreHardwareMonitor isn't running, start it (start_lhm yes), offer to (ask), or not (no)."""
        # CONGEE_TEST_LHM_MISSING=1 pretends it isn't running or installed, to try the install prompt.
        test = os.environ.get("CONGEE_TEST_LHM_MISSING") == "1"
        if (lhm_running() and not test) or not lhm_is_local(self.settings["sensors_url"]):
            return
        mode = self.settings["start_lhm"].strip().lower()
        if mode in ("no", "false", "0", "off"):
            Log.write("LibreHardwareMonitor isn't running (start_lhm is no)")
            return
        exe = None if test else find_lhm_exe()
        if not exe:  # not installed: offer to install it (always asks, even with start_lhm yes)
            winget = winget_exe()
            install, remember = ask_install_lhm(self.root, winget is not None)
            if remember:
                self.remember_start_lhm(install)
            if not install:
                Log.write('LibreHardwareMonitor isn\'t installed; install: "Not now"' + (" (remembered: start_lhm no)" if remember else ""))
            elif not winget:
                Log.write("LibreHardwareMonitor isn't installed and winget isn't available; opened the download page")
                try:
                    os.startfile(LHM_RELEASES_URL)
                except OSError:
                    pass
            else:
                install_lhm(self.root, winget, lambda exe_: exe_ and not lhm_running() and start_lhm(exe_))
            return
        start = True
        if not yes(mode):  # ask
            start, remember = ask_start_lhm(self.root)
            if remember:
                self.remember_start_lhm(start)
            if not start:
                Log.write('LibreHardwareMonitor not started: "Not now"' + (" (remembered: start_lhm no)" if remember else ""))
        if start:
            start_lhm(exe)

    def remember_start_lhm(self, on: bool):
        try:
            set_setting(self.config_path, "start_lhm", "yes" if on else "no",
                        "Start LibreHardwareMonitor with congee if it isn't running: ask, yes or no")
        except OSError:
            pass

    def exit_from_menu(self):
        """Exit congee from the tray menu: also stop LibreHardwareMonitor if stop_lhm says so (or the user
        agrees). Only here: Windows shutting down or setup closing congee leave LHM alone."""
        Log.stop_reason("Exit congee chosen from the menu")
        try:
            self.stop_lhm_on_exit()
        except Exception as e:  # noqa: BLE001 - never block exiting
            Log.write(f"LibreHardwareMonitor check on exit failed: {e}")
        self.quit()

    def stop_lhm_on_exit(self):
        if not lhm_running() or not lhm_is_local(self.settings["sensors_url"]):
            return
        mode = self.settings["stop_lhm"].strip().lower()
        if mode in ("no", "false", "0", "off"):
            return
        stop = True
        if not yes(mode):  # ask
            stop, remember = ask_stop_lhm(self.root)
            if remember:
                try:
                    set_setting(self.config_path, "stop_lhm", "yes" if stop else "no",
                                "Stop LibreHardwareMonitor too when you choose Exit congee: ask, yes or no")
                except OSError:
                    pass
            if not stop:
                Log.write('LibreHardwareMonitor left running: "Keep it running"' + (" (remembered: stop_lhm no)" if remember else ""))
        if stop:
            stop_lhm()

    def heartbeat(self):
        Log.write(f"still running (up {Log.uptime()})")
        self.root.after(3600_000, self.heartbeat)

    def quit(self):
        if self.tray:
            self.tray.remove()
            self.tray = None
        self.root.destroy()

    # ---- win32 ------------------------------------------------------------
    def apply_window_styles(self):
        if not IS_WINDOWS:
            return
        self.hwnd = int(self.root.wm_frame(), 16)
        ex = user32.GetWindowLongW(self.hwnd, GWL_EXSTYLE)
        ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW
        if yes(self.settings["click_through"]):
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED
        else:
            ex &= ~WS_EX_TRANSPARENT
        user32.SetWindowLongW(self.hwnd, GWL_EXSTYLE, ex)
        if self.window_type == "desktop":
            self.pin_to_desktop()

    def pin_to_desktop(self):
        """Keep the window directly above the wallpaper (Progman) in z-order:
        under every normal app window, but above the desktop itself."""
        if not (IS_WINDOWS and self.hwnd):
            return
        progman = user32.FindWindowW("Progman", None)
        if not progman:
            return
        # Walk up from the desktop past invisible helper windows (e.g.
        # LibreHardwareMonitor parks one in this exact spot); only visible
        # windows matter. Fighting over the exact slot would just make two
        # apps re-sort the z-order forever.
        above = user32.GetWindow(progman, GW_HWNDPREV)
        while above and above != self.hwnd and not user32.IsWindowVisible(above):
            above = user32.GetWindow(above, GW_HWNDPREV)
        if above == self.hwnd:
            return
        insert_after = above if above else HWND_TOP
        user32.SetWindowPos(self.hwnd, insert_after, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)

    def pin_loop(self):
        self.pin_to_desktop()
        self.after_ids.append(self.root.after(400, self.pin_loop))
        if len(self.after_ids) > 50:
            self.after_ids = self.after_ids[-10:]

    # ---- dragging -------------------------------------------------------------
    def on_press(self, e):
        self.drag_from = (e.x_root - self.root.winfo_x(), e.y_root - self.root.winfo_y())

    def on_drag(self, e):
        if not yes(self.settings["draggable"]) or not self.drag_from:
            return
        self.user_pos = (e.x_root - self.drag_from[0], e.y_root - self.drag_from[1])
        self.root.geometry(f"+{self.user_pos[0]}+{self.user_pos[1]}")
        if self.geom:
            self.geom = (self.geom[0], self.geom[1], *self.user_pos)

    # ---- fonts / colours ---------------------------------------------------
    def font(self, spec: str) -> tkfont.Font:
        f = self.fonts.get(spec)
        if f is None:
            family, size, weight, slant = "Consolas", 10, "normal", "roman"
            parts = [p.strip() for p in spec.split(":")]
            if len(parts) == 1 and re.search(r"\s\d+(\.\d+)?$", spec):
                family, sz = spec.rsplit(None, 1)
                size = round(float(sz))
            else:
                family = parts[0] or family
                for p in parts[1:]:
                    pl = p.lower()
                    if pl.startswith("size="):
                        size = round(float(pl[5:]))
                    elif pl.startswith("pixelsize="):
                        size = -self.evaluator.px(pl[10:])
                    elif pl == "bold":
                        weight = "bold"
                    elif pl in ("italic", "oblique"):
                        slant = "italic"
            f = tkfont.Font(root=self.root, family=family, size=size, weight=weight, slant=slant)
            self.fonts[spec] = f
            self.font_metrics[spec] = f.metrics()
        return f

    def metrics(self, spec: str) -> dict:
        self.font(spec)
        return self.font_metrics[spec]

    def is_color(self, c: str) -> bool:
        try:
            self.root.winfo_rgb(c)
            return True
        except tk.TclError:
            return False

    def rgb(self, c: str) -> tuple[int, int, int]:
        if c not in self.rgb_cache:
            try:
                r, g, b = self.root.winfo_rgb(c)
                self.rgb_cache[c] = (r >> 8, g >> 8, b >> 8)
            except tk.TclError:
                self.rgb_cache[c] = (255, 255, 255)
        return self.rgb_cache[c]

    def safe_color(self, c: str) -> str:
        return "#%02x%02x%02x" % self.rgb(c)

    def mix(self, c1: str, c2: str, t: float) -> str:
        a, b = self.rgb(c1), self.rgb(c2)
        return "#%02x%02x%02x" % tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

    # ---- layout ------------------------------------------------------------------
    def seg_width(self, seg, flex: int) -> int:
        if isinstance(seg, Text):
            return self.font(seg.font).measure(seg.s)
        if isinstance(seg, (Bar, Graph)):
            return seg.w if seg.w > 0 else flex
        if isinstance(seg, HR):
            return flex
        if isinstance(seg, Offset):
            return seg.x
        return 0

    @staticmethod
    def is_flex(seg) -> bool:
        return isinstance(seg, HR) or (isinstance(seg, (Bar, Graph)) and seg.w <= 0)

    def layout_line(self, line: list, flex: int):
        """Split a line on alignr/alignc and place segments inside each group."""
        groups = [["left", []]]
        for seg in line:
            if seg == ALIGNR:
                groups.append(["right", []])
            elif seg == ALIGNC:
                groups.append(["center", []])
            else:
                groups[-1][1].append(seg)
        placed = []
        for kind, segs in groups:
            x, items = 0, []
            for seg in segs:
                if isinstance(seg, Goto):
                    if kind == "left":
                        x = max(x, seg.x)
                    continue
                w = self.seg_width(seg, flex)
                items.append((seg, x, w))
                x += w
            placed.append((kind, items, x))
        return placed

    def line_height(self, line: list) -> tuple[int, int, int]:
        asc = desc = widget_h = 0
        has_text = False
        for seg in line:
            if isinstance(seg, Text):
                m = self.metrics(seg.font)
                asc, desc, has_text = max(asc, m["ascent"]), max(desc, m["descent"]), True
            elif isinstance(seg, (Bar, Graph, HR)):
                widget_h = max(widget_h, seg.h + 2)
        if not has_text:
            m = self.metrics(self.settings["font"])
            asc, desc = m["ascent"], m["descent"]
        return max(asc + desc, widget_h), asc, desc

    # ---- tick / render -------------------------------------------------------------
    def tick(self):
        try:
            if os.path.getmtime(self.config_path) != self.mtime:
                self.reload()  # reload() calls tick() itself
                return
        except OSError:
            pass
        self.stats.sample()
        self.render()
        interval = max(0.1, float(self.settings.get("update_interval", 1)))
        self.after_ids.append(self.root.after(int(interval * 1000), self.tick))
        if len(self.after_ids) > 50:
            self.after_ids = self.after_ids[-10:]

    def render(self):
        try:
            if self.config_error:
                raise RuntimeError(self.config_error)
            lines = self.evaluator.build(self.tokens)
        except Exception as e:  # noqa: BLE001 - show errors on screen rather than vanish
            if not self.config_error:
                import traceback
                Log.once("display error: " + "".join(traceback.format_exception(e)).rstrip())
            lines = [[Text(f"congee: {e}", "#ff6060", self.settings.get("font", DEFAULTS["font"]))]]

        s = self.settings
        px = self.evaluator.px
        pad = px(s["border_inner_margin"])

        # pass 1: natural size
        content_w = px(s["minimum_width"])
        for line in lines:
            natural = sum(g[2] for g in self.layout_line(line, self.MIN_FLEX))
            content_w = max(content_w, natural)
        heights = [self.line_height(line) for line in lines]
        content_h = max(px(s["minimum_height"]), sum(h for h, _, _ in heights))
        W, H = content_w + 2 * pad, content_h + 2 * pad

        # position
        if self.user_pos:
            x, y = self.user_pos
        else:
            areas = monitor_work_areas(self.root)
            idx = int(s.get("monitor", 0) or 0)
            left, top, right, bottom = areas[idx if 0 <= idx < len(areas) else 0]
            gx, gy = px(s["gap_x"]), px(s["gap_y"])
            vert, _, horiz = s["alignment"].partition("_")
            y = top + gy if vert == "top" else bottom - H - gy if vert == "bottom" else top + (bottom - top - H) // 2 + gy
            x = left + gx if horiz == "left" else right - W - gx if horiz == "right" else left + (right - left - W) // 2 + gx
        geom = (W, H, x, y)
        if geom != self.geom:
            self.canvas.configure(width=W, height=H)
            self.root.geometry(f"{W}x{H}+{x}+{y}")
            self.geom = geom

        # pass 2: draw
        c = self.canvas
        c.delete("all")
        shade = self.safe_color(s["default_shade_color"]) if yes(s["draw_shades"]) else None
        outline = self.safe_color(s["default_outline_color"]) if yes(s["draw_outline"]) else None
        borders = yes(s["draw_graph_borders"])
        y = pad
        for line, (lh, asc, desc) in zip(lines, heights):
            nflex = sum(1 for seg in line if self.is_flex(seg))
            fixed_total = sum(g[2] for g in self.layout_line(line, 0))
            flex = max(0, (content_w - fixed_total) // nflex) if nflex else 0
            text_top = y + (lh - (asc + desc)) // 2
            for kind, items, gw in self.layout_line(line, flex):
                if kind == "left":
                    gx = 0
                elif kind == "center":
                    gx = (content_w - gw) // 2
                else:
                    gx = content_w - gw
                for seg, sx, w in items:
                    X = pad + gx + sx
                    if isinstance(seg, Text):
                        f = self.font(seg.font)
                        ty = text_top + asc - self.metrics(seg.font)["ascent"]
                        if outline:
                            for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                                c.create_text(X + dx, ty + dy, text=seg.s, font=f, fill=outline, anchor="nw")
                        if shade:
                            c.create_text(X + 1, ty + 1, text=seg.s, font=f, fill=shade, anchor="nw")
                        c.create_text(X, ty, text=seg.s, font=f, fill=self.safe_color(seg.color), anchor="nw")
                    elif isinstance(seg, HR):
                        wy = y + lh // 2
                        dash = (2, 2) if seg.dash else None
                        if shade:
                            c.create_line(X + 1, wy + 1, X + w + 1, wy + 1, fill=shade, width=seg.h, dash=dash)
                        c.create_line(X, wy, X + w, wy, fill=self.safe_color(seg.color), width=seg.h, dash=dash)
                    elif isinstance(seg, Bar) and w > 1:
                        wy = y + (lh - seg.h) // 2
                        col = self.safe_color(seg.color)
                        if shade:
                            c.create_rectangle(X + 1, wy + 1, X + w, wy + seg.h, outline=shade)
                        c.create_rectangle(X, wy, X + w - 1, wy + seg.h - 1, outline=col)
                        fw = int((w - 1) * seg.frac)
                        if fw > 0:
                            c.create_rectangle(X, wy, X + fw, wy + seg.h - 1, outline=col, fill=col)
                    elif isinstance(seg, Graph) and w > 2:
                        wy = y + (lh - seg.h) // 2
                        bottom = wy + seg.h - 1
                        col = self.safe_color(seg.color)
                        if borders:
                            if shade:
                                c.create_rectangle(X + 1, wy + 1, X + w, wy + seg.h, outline=shade)
                            c.create_rectangle(X, wy, X + w - 1, bottom, outline=col)
                        inner = w - 2
                        vals = seg.vals[-inner:]
                        start = X + 1 + inner - len(vals)
                        for i, v in enumerate(vals):
                            v = max(0.0, min(1.0, v))
                            hpx = int(v * (seg.h - 2))
                            if hpx <= 0:
                                continue
                            fill = self.mix(seg.color, seg.color2, v) if seg.color2 else col
                            c.create_line(start + i, bottom, start + i, bottom - hpx, fill=fill)
            y += lh

    def run(self):
        try:
            self.root.mainloop()
        finally:
            if self.tray:  # e.g. Ctrl+C in a console: don't leave a dead icon behind
                self.tray.remove()


# --------------------------------------------------------------------------
# Text-mode output (--once), handy for testing configs
# --------------------------------------------------------------------------
def render_text(lines: list[list]) -> str:
    out = []
    for line in lines:
        parts = []
        for seg in line:
            if isinstance(seg, Text):
                parts.append(seg.s)
            elif isinstance(seg, Bar):
                n = 20
                k = int(round(seg.frac * n))
                parts.append("[" + "#" * k + "-" * (n - k) + "]")
            elif isinstance(seg, Graph):
                blocks = " ▁▂▃▄▅▆▇█"
                parts.append("".join(blocks[int(max(0, min(1, v)) * 8)] for v in seg.vals[-20:]))
            elif isinstance(seg, HR):
                parts.append("-" * 20)
            elif seg in (ALIGNR, ALIGNC):
                parts.append("  ")
        out.append("".join(parts))
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser(description="conky-style desktop system monitor for Windows")
    ap.add_argument("-c", "--config", default=None,
                    help="config file (default: first found of " + ", ".join(CONFIG_SEARCH) + ")")
    ap.add_argument("--create-config", action="store_true",
                    help=f"copy the bundled default config to {USER_CONFIG} (won't overwrite) and exit")
    ap.add_argument("--which-config", action="store_true", help="print the config file that would be used and exit")
    ap.add_argument("--once", action="store_true", help="print one frame as plain text and exit")
    ap.add_argument("--interfaces", action="store_true", help="list network interface names and exit")
    ap.add_argument("--sensors", action="store_true",
                    help="list hardware sensors from LibreHardwareMonitor and exit")
    args = ap.parse_args()
    args.config = args.config or find_config()

    if args.create_config:
        if os.path.exists(USER_CONFIG):
            print(f"Already exists, not overwriting: {USER_CONFIG}")
        else:
            import shutil
            os.makedirs(os.path.dirname(USER_CONFIG), exist_ok=True)
            shutil.copyfile(DEFAULT_CONFIG, USER_CONFIG)
            print(f"Created {USER_CONFIG}")
        return

    if args.which_config:
        print(os.path.abspath(args.config))
        return

    if args.sensors:
        try:
            settings, _ = parse_config(args.config)
        except OSError:
            settings = dict(DEFAULTS)
        sens = Sensors()
        sens.configure(settings["sensors_url"], 1.0)
        sens.refresh_now()
        if not sens.ok:
            print(f"Can't reach LibreHardwareMonitor at {sens.url}: {sens.error}")
            print("Start LibreHardwareMonitor and enable Options > Remote Web Server > Run.")
            return
        for s in sens.items:
            print(f"{s['type']:12} {s['display']:>14}   {s['path']}")
        gpus = sens.devices("/gpu")
        sel = sens.select_gpu(settings.get("gpu", "auto"))
        print()
        for i, (hw_id, name) in enumerate(gpus):
            print(f"GPU {i}: {name}  ({hw_id}){'  <- used by gpu_* variables' if sel and sel[0] == hw_id else ''}")
        if not gpus:
            print("No GPU found in LibreHardwareMonitor's data.")
        cpus = sens.devices("/amdcpu") + sens.devices("/intelcpu") + sens.devices("/cpu")
        print(f"CPU: {cpus[0][1]}  ({cpus[0][0]})" if cpus else "No CPU sensors found.")
        return

    if args.interfaces:
        stats = psutil.net_if_stats()
        for name, addrs in psutil.net_if_addrs().items():
            ipv4 = [a.address for a in addrs if a.family == socket.AF_INET]
            up = "up" if stats.get(name) and stats[name].isup else "down"
            print(f"{name!r:40} {up:5} {', '.join(ipv4)}")
        return

    if args.once:
        settings, text = parse_config(args.config)
        stats = Stats()
        ev = Evaluator(stats, settings, 1.0, lambda c: bool(HEX6_RE.match(c)))
        tokens = tokenize(text)
        stats.sensors.configure(settings["sensors_url"], 1.0)
        stats.sensors.refresh_now()
        stats.drive_scanner.wait(15)  # include network/DVD drives, which are scanned in the background
        stats.sample()
        ev.build(tokens)  # prime process CPU counters and exec commands
        time.sleep(1.0)
        stats.sample()
        print(render_text(ev.build(tokens)))
        return

    set_dpi_awareness()
    try:
        logging_on = yes(parse_config(args.config)[0]["logging"])
    except Exception:  # noqa: BLE001 - a broken config is reported in the widget itself
        logging_on = True
    Log.started(os.path.abspath(args.config), logging_on)
    Congee(args.config).run()
    Log.stopped(0)


if __name__ == "__main__":
    main()
