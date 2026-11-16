"""
Solo knockdown measurement (card PropHunt#21): knocks the local player down with `phknock` and reads how far
the body centre travels, for the pre-fix recipe and for the fix at a few speeds.

    python tests/knock.py --slot mptest3 [--at x,y,z] [--dir dx,dz]

Needs a Debug build of PropHunt in the slot and an open, flat spot in the knock direction (`--at` teleports there
first). The pre-fix recipe is one 30 m/s VelocityChange on the ragdoll root body in the ragdoll's first frame;
the fix gives every ragdoll body the shove velocity once the Animator has handed the bones to physics, with the
player's own capsule collider ignored by the ragdoll. The knock runs with the character controller off, as in a round.
"""
import argparse
import glob
import os
import re
import time

from check import Results, Slot, first

RESULT = r"phknock: RESULT legacy=(\w+) speed=(\S+) horizontal=([\d.]+)m peak=([\d.]+)m"
SHOVE = r"knockdown: shove v=([\d.]+) m/s on (\d+) bodies after ([\d.]+)s"
HOLD = r"knockdown: root speed ([\d.]+) m/s 4 steps after the shove"
CAPSULE = r"knockdown: capsule of \S+ ignores (\d+) ragdoll colliders"
TARGET = (1.0, 4.0)   # metres of horizontal travel a knockdown should give: a visible shove, not a launch
REPEAT = 2


def knock(game, res, mode, speed, d, seconds=3.0):
    """One phknock run. Returns (horizontal, peak, shove-line-match) or None."""
    m = game.mark()
    game.cmd("phknock %s %s %s %s %s" % (mode, speed, d[0], d[1], seconds), wait=0.3)
    line = game.wait_line(m, RESULT, timeout=seconds + 12)
    lines = game.lines(m)
    time.sleep(2.5)   # stand-up settles before the next run
    if not line:
        res.note("no RESULT line for %s %s; last lines: %s" % (mode, speed, lines[-4:]))
        return None
    r = re.search(RESULT, line)
    return (float(r.group(3)), float(r.group(4)), first(lines, SHOVE),
            first(lines, r"legacy impulse on the root body, kinematic=(\w+)"), first(lines, HOLD), first(lines, CAPSULE))


def series(game, res, mode, speed, d):
    runs = [knock(game, res, mode, speed, d) for _ in range(REPEAT)]
    runs = [r for r in runs if r]
    if not runs:
        return None
    mean = sum(r[0] for r in runs) / len(runs)
    peak = max(r[1] for r in runs)
    res.note("%s speed=%s: travel %s m (mean %.2f, peak %.2f)" % (mode, speed, ", ".join("%.2f" % r[0] for r in runs), mean, peak))
    return mean, peak, runs


def source_targets():
    """Every Harmony target class/method the source declares, as 'Type.Method' (Debug build: all of them)."""
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    rx = re.compile(r'\[HarmonyPatch\(typeof\(([\w.]+)\),\s*(?:nameof\(([\w.]+)\)|"(\w+)")')
    found = set()
    for path in glob.glob(os.path.join(root, "**", "*.cs"), recursive=True):
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path:
            continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for m in rx.finditer(f.read()):
                found.add(m.group(1).split(".")[-1] + "." + (m.group(2) or m.group(3)).split(".")[-1])
    return found


def check_patches(game, res):
    """Every Harmony target of the source is patched in the running game, and no patch threw."""
    log = game.lines()
    patched = set(m.group(1) for m in (re.search(r"\[PropHunt\] patched: ([\w.]+)$", l) for l in log) if m)
    want = source_targets()
    missing = sorted(want - patched)
    res.check("every Harmony target of the source is patched (%d)" % len(want), want and not missing,
              "missing: %s" % missing if missing else "")
    bad = [l for l in log if "PatchAll FAILED" in l or ("PropHunt" in l and re.search(r"HarmonyException|Patching exception|Failed to patch", l))]
    res.check("no Harmony patch of PropHunt failed", not bad, bad[:3])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", required=True)
    ap.add_argument("--at", default="124,4.3,-98", help="x,y,z to teleport to first (default: flat open ground in Hyland Point)")
    ap.add_argument("--dir", default="1,0", help="knock direction dx,dz")
    a = ap.parse_args()
    d = [float(v) for v in a.dir.split(",")]
    res = Results()
    game = Slot(a.slot)

    res.begin("boot")
    check_patches(game, res)

    res.begin("console")
    out = game.cmd("phprobe")
    res.check("dev console commands reach PropHunt", any("phprobe:" in l for l in out), out[-3:])

    if a.at:
        x, y, z = [float(v) for v in a.at.split(",")]
        game.tp(x, y, z)
    res.note("position %s" % (game.position(),))

    res.begin("pre-fix recipe (reproduction of #21)")
    base = series(game, res, "legacy", 0, d)
    legacy = series(game, res, "legacy", 30, d)
    res.check("pre-fix recipe measured", base is not None and legacy is not None)
    if legacy:
        k = legacy[2][0][3]
        res.note("root body kinematic in the ragdoll's first frame: %s" % (k.group(1) if k else "no line"))

    res.begin("fix")
    fix = series(game, res, "fix", -1, d)
    if fix:
        run = fix[2][0]
        shove, hold, capsule = run[2], run[4], run[5]
        res.check("the player's own capsule lets the ragdoll through", capsule is not None and int(capsule.group(1)) > 1,
                  capsule.group(0) if capsule else "no capsule line")
        res.check("the shove lands on every ragdoll body once the Animator is off",
                  shove is not None and int(shove.group(2)) > 1 and float(shove.group(3)) < 0.2,
                  shove.group(0) if shove else "no shove line")
        v = float(shove.group(1)) if shove else 0
        res.check("the body keeps most of the shove speed (nothing blocks it)",
                  hold is not None and float(hold.group(1)) >= 0.7 * v, hold.group(0) if hold else "no root speed line")
        res.check("knockdown travel within %.1f-%.1f m" % TARGET, TARGET[0] <= fix[0] <= TARGET[1], "%.2f m" % fix[0])
        if base:
            res.check("the shove moves the body beyond a plain fall", fix[0] > base[0] + 0.5,
                      "fix %.2f m vs fall %.2f m" % (fix[0], base[0]))
    else:
        res.check("fix measured", False, "no RESULT line")
    weak, strong = series(game, res, "fix", 4, d), series(game, res, "fix", 8, d)
    if weak and strong:
        res.check("a stronger shove moves the body further", strong[0] > weak[0] + 0.3,
                  "4 m/s %.2f m, 8 m/s %.2f m" % (weak[0], strong[0]))

    return res.summary()


if __name__ == "__main__":
    raise SystemExit(main())
