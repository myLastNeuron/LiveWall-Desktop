"""Regression check: is the LiveWall window below the desktop icon view (icons visible)?

usage: python tools/verify-zorder.py [--break]
  --break  first raises the wallpaper window to the top of Progman's children, which is the
           state the app used to get stuck in (wallpaper visible, icons gone).
"""
import ctypes
import subprocess
import sys
from ctypes import wintypes as w

u = ctypes.WinDLL("user32", use_last_error=True)
for fn, res, args in [
    ("GetParent", w.HWND, [w.HWND]),
    ("FindWindowW", w.HWND, [w.LPCWSTR, w.LPCWSTR]),
    ("FindWindowExW", w.HWND, [w.HWND, w.HWND, w.LPCWSTR, w.LPCWSTR]),
    ("GetWindow", w.HWND, [w.HWND, ctypes.c_uint]),
    ("GetDesktopWindow", w.HWND, []),
    ("SetWindowPos", w.BOOL, [w.HWND, w.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint]),
]:
    f = getattr(u, fn)
    f.restype = res
    f.argtypes = args

HWND_TOP = 0
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def kids(p):
    out, c = [], u.FindWindowExW(p, None, None, None)
    while c:
        out.append(c)
        c = u.FindWindowExW(p, c, None, None)
    return out


def pid_of(h):
    p = w.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def livewall_windows():
    pids = set()
    for line in subprocess.run(["tasklist", "/FI", "IMAGENAME eq LiveWall.exe", "/FO", "CSV", "/NH"],
                               capture_output=True, text=True).stdout.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1:
            pids.add(int(parts[1]))

    found = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if pid_of(h) in pids and u.IsWindowVisible(h):
            found.append(h)
        return True

    u.EnumChildWindows(u.GetDesktopWindow(), cb, 0)
    return found


def icon_view(progman):
    for parent in (progman, u.FindWindowExW(progman, None, "WorkerW", None)):
        if parent:
            d = u.FindWindowExW(parent, None, "SHELLDLL_DefView", None)
            if d:
                return d
    return None


def state():
    progman = u.FindWindowW("Progman", None)
    icons = icon_view(progman)
    order = []
    for h in kids(progman):
        tag = cls(h)
        if h == icons:
            tag += "  <- ICONS"
        for win in livewall_windows():
            if win == h:
                tag += "  <- LIVE WALL WINDOW"
            elif u.GetParent(win) == h:
                tag += f"  <- contains LIVE WALL WINDOW 0x{win:X}"
        order.append(tag)
    return progman, icons, order


def main():
    if not livewall_windows():
        raise SystemExit("LiveWall is not running — start it first, then rerun this check.")

    if "--break" in sys.argv:
        wins = livewall_windows()
        if not wins:
            raise SystemExit("no visible LiveWall window to break")
        for win in wins:
            u.SetWindowPos(win, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE)
        print(f"raised 0x{wins[0]:X} to the top of its parent (simulated bug)\n")

    progman, icons, order = state()
    print(f"Progman children, top of z-order first (icons at 0x{icons:X}):")
    for i, tag in enumerate(order):
        print(f"  {i}: {tag}")
    if icons is None:
        raise SystemExit("\nno SHELLDLL_DefView found")
    above = order.index(next(t for t in order if "ICONS" in t))
    ours = [i for i, t in enumerate(order) if "LIVE WALL WINDOW" in t]
    if not ours:
        raise SystemExit("LiveWall window is not a child of Progman (another desktop layout)")
    print(f"\nicons rank {above}, wallpaper rank {ours[0]}")
    hidden = ours[0] < above
    print("RESULT: icons are " + ("COVERED by the wallpaper (bug)" if hidden else "on top (ok)"))
    raise SystemExit(1 if hidden else 0)


main()
