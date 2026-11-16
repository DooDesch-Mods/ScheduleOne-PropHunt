"""
The helper behind PropHunt's in-game checks: drives a running test game over the ScheduleMCP bridge
(Workspace/tools/bridge/s1.py), reads the game's log, and prints PASS or FAIL per check.

The checks read the lines of PropHunt's dev console commands (ph*), so the slot needs a Debug build.
"""
import glob
import os
import re
import shutil
import sys
import time


def _workspace_bridge():
    here = os.path.dirname(os.path.abspath(__file__))
    folder = here
    for _ in range(6):
        candidate = os.path.join(folder, "Workspace", "tools", "bridge")
        if os.path.isfile(os.path.join(candidate, "s1.py")):
            return candidate
        folder = os.path.dirname(folder)
    raise SystemExit("[check] Workspace/tools/bridge/s1.py not found above " + here)


sys.path.insert(0, _workspace_bridge())
from s1 import Game, BridgeDown  # noqa: E402

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")

for _stream in (sys.stdout, sys.stderr):
    _stream.reconfigure(encoding="utf-8", errors="replace")


class Results:
    """PASS/FAIL lines of one script, across one or more games."""

    def __init__(self):
        self.rows = []
        self.stage = ""

    def begin(self, stage):
        self.stage = stage
        print("\n##### " + stage, flush=True)

    def check(self, name, ok, detail=""):
        verdict = "PASS" if ok else "FAIL"
        self.rows.append((self.stage, name, verdict, detail))
        print("%s  %s%s" % (verdict, name, ("  - " + str(detail)) if detail else ""), flush=True)
        return ok

    def note(self, text):
        print("      " + text, flush=True)

    def summary(self):
        failed = [r for r in self.rows if r[2] != "PASS"]
        print("\n===== %d PASS, %d FAIL" % (len(self.rows) - len(failed), len(failed)))
        for stage, name, verdict, detail in failed:
            print("%s  [%s] %s  - %s" % (verdict, stage, name, detail))
        return 1 if failed else 0


class Slot:
    """One running game: console commands, its log, teleport, position, screenshot."""

    def __init__(self, slot, label=None):
        self.g = Game(slot)
        self.slot = slot
        self.label = label or slot

    def log_path(self):
        found = glob.glob(os.path.join(self.g.root, "MelonLoader", "Logs", "*.log"))
        found += glob.glob(os.path.join(self.g.root, "SideHustle_Profiles", "session", "*", "MelonLoader", "Logs", "*.log"))
        return max(found, key=os.path.getmtime) if found else None

    def mark(self):
        path = self.log_path()
        return (path, os.path.getsize(path) if path else 0)

    def lines(self, mark=None, grep=None):
        path, offset = mark if mark else (None, 0)
        now = self.log_path()
        if not now:
            return []
        if now != path:
            offset = 0
        with open(now, "r", encoding="utf-8", errors="replace") as handle:
            handle.seek(offset)
            found = handle.read().splitlines()
        if grep:
            rx = re.compile(grep)
            found = [l for l in found if rx.search(l)]
        return found

    def wait_line(self, mark, pattern, timeout=15.0):
        """The first log line after `mark` that matches, or None after `timeout` seconds."""
        rx = re.compile(pattern)
        end = time.time() + timeout
        while True:
            for line in self.lines(mark):
                if rx.search(line):
                    return line
            if time.time() >= end:
                return None
            time.sleep(0.4)

    def cmd(self, command, wait=0.8):
        """Run a console command; returns the log lines written while it ran."""
        m = self.mark()
        try:
            self.g.run(command, settle_ms=int(wait * 1000), max_wait_ms=int(wait * 1000) + 8000)
        except TimeoutError as e:
            print("      [%s] console '%s': %s" % (self.label, command, e), flush=True)
        return self.lines(m)

    def position(self):
        r = self.g.world("player_position", {}, timeout_ms=8000).get("result") or {}
        p = r.get("position") or [0, 0, 0]
        return (float(p[0]), float(p[1]), float(p[2]))

    def tp(self, x, y, z, yaw=None):
        args = {"x": x, "y": y, "z": z}
        if yaw is not None:
            args["yaw"] = yaw
        self.g.world("teleport", args, timeout_ms=8000)
        time.sleep(1.2)

    def shot(self, name):
        """A screenshot copied to tests/out/<name>.png for a person to look at; no check reads pixels."""
        path = self.g.shot()
        if not path or not os.path.isfile(path):
            print("      [%s] screenshot failed: %r" % (self.label, path), flush=True)
            return None
        os.makedirs(OUT, exist_ok=True)
        target = os.path.join(OUT, name + ".png")
        shutil.copyfile(path, target)
        print("      screenshot: " + target, flush=True)
        return target


def first(lines, pattern):
    rx = re.compile(pattern)
    for line in lines:
        m = rx.search(line)
        if m:
            return m
    return None
