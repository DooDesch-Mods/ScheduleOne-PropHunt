"""
One PropHunt round in a co-op pair: lobby, roles, a prop disguise, a concussion knockdown with the shove
(card PropHunt#21) seen on both clients, the catch, and the end of the round.

    pwsh -File tests/coop_up.ps1
    python tests/coop.py --host mptest3 --client mptest4

Needs a Debug build of PropHunt on both slots, joined into one Steam lobby (coop_up.ps1 does that).
With two players a round has one hunter and one hider, so friendly fire cannot happen; the knockdown comes from
the hider's concussion.
"""
import argparse
import hashlib
import math
import os
import re
import time

from check import Results, Slot, first

# The slide depends on the ground: about 2 m on grass, 4.6 to 5.2 m on the asphalt of KNOCK_SPOT.
TRAVEL = (1.0, 6.0)
KNOCK_SPOT = (-41.0, 0.2, 91.0)
SETTINGS = [("hide", 25), ("hunt", 240), ("end", 8), ("stun", 4), ("conc", 3), ("concr", 6), ("round", "Single"),
            ("area", 120)]


def packs(game):
    """Name -> content hash of every Personnel NPC pack folder of a slot."""
    root = os.path.join(game.g.root, "UserData", "Personnel", "Packs")
    found = {}
    if not os.path.isdir(root):
        return found
    for name in sorted(os.listdir(root)):
        folder = os.path.join(root, name)
        if not os.path.isdir(folder):
            continue
        digest = hashlib.md5()
        for base, _, files in sorted(os.walk(folder)):
            for file in sorted(files):
                path = os.path.join(base, file)
                digest.update(os.path.relpath(path, folder).encode("utf-8"))
                with open(path, "rb") as handle:
                    digest.update(handle.read())
        found[name] = digest.hexdigest()
    return found


def camera_gap(game):
    """Metres between the player and the camera, or None when the bridge reports neither."""
    where = (game.g.world("player_position", {}, timeout_ms=5000) or {}).get("result") or {}
    pos, cam = where.get("position"), (where.get("camera") or {}).get("position")
    return math.dist(pos, cam) if pos and cam else None


def free_camera(res, game, quiet=False):
    """The template save holds no player data for the test accounts, so a joining client runs the vanilla intro and
    sits in the character creator, which holds the camera in its room far off the map (IntroManager parents the
    camera container there, CharacterCreator.Open overrides the transform). It takes the camera back now and then
    until the creator is finished, and every shot would start in that room. Hand the camera back to the player when
    it is far from them. quiet=True only notes a release and records no check."""
    d = camera_gap(game)
    if d is None:
        res.check("%s reports player and camera position" % game.label, False)
        return
    if d <= 50:
        if not quiet:
            res.note("%s camera is with the player (%.1f m)" % (game.label, d))
        return
    res.note("%s camera is %.0f m from the player (character creator) - releasing it" % (game.label, d))
    game.g.world("camera_release", {}, timeout_ms=5000)
    time.sleep(1)
    d2 = camera_gap(game)
    if not quiet:
        res.check("%s camera is back with the player" % game.label, d2 is not None and d2 <= 50,
                  "%.1f m" % d2 if d2 is not None else "no position")


def state(game):
    """(phase, role) from phstate, or (None, None)."""
    m = first(game.cmd("phstate"), r"state: phase=(\w+) role=(\w+)")
    return (m.group(1), m.group(2)) if m else (None, None)


def wait_phase(game, phase, timeout):
    end = time.time() + timeout
    while time.time() < end:
        p, r = state(game)
        if p == phase:
            return r
        time.sleep(2)
    return None


def probe(game, name_part):
    """The phprobe line of the player whose name contains name_part (or the local one for None)."""
    for line in game.cmd("phprobe"):
        m = re.search(r"phprobe: (\S+) id=(\d+) local=(\w+) role=(\w+) prop=(-?\d+) clone=(\w+) layer=(\w+) "
                      r"meshesVisible=(\w+) ragdolled=(\w+) downed=(\w+) eliminated=(\w+) pos=\(([-\d.]+),([-\d.]+),([-\d.]+)\)"
                      r"(?: worn=(-?\d+))?", line)
        if not m:
            continue
        if (name_part is None and m.group(3) == "True") or (name_part is not None and m.group(3) == "False"):
            return m
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", required=True)
    ap.add_argument("--client", required=True)
    ap.add_argument("--hunter", choices=("any", "host", "client"), default="any",
                    help="restart the match (up to 8 times) until this side hunts")
    a = ap.parse_args()
    res = Results()
    host, client = Slot(a.host, "host"), Slot(a.client, "client")
    both = (host, client)

    res.begin("setup")
    hp, cp = packs(host), packs(client)
    differ = sorted(n for n in set(hp) | set(cp) if hp.get(n) != cp.get(n))
    res.check("host and client load the same NPC packs", not differ,
              "differ: " + ", ".join(differ) if differ else "%d packs on both" % len(hp))
    for g in both:
        out = g.cmd("phstatus")
        m = first(out, r"status: Ready=(\w+) InLobby=(\w+) IsHost=(\w+) members=(\d+)")
        res.check("%s is in the Steam lobby with 2 members" % g.label, m is not None and m.group(2) == "True" and m.group(4) == "2",
                  m.group(0) if m else out[-2:])
    for g in both:
        free_camera(res, g)

    roles = None
    for attempt in range(1, 9):
        roles = start_match(res, host, client)
        if roles is None:
            return res.summary()
        if a.hunter == "any" or roles[a.hunter] == "Hunter":
            break
        res.note("attempt %d: %s hunts, want %s - restarting the match" % (attempt, "host" if roles["host"] == "Hunter" else "client", a.hunter))
        for g in both:
            g.cmd("phstop", wait=2)
    else:
        res.check("%s hunts within 8 matches" % a.hunter, False, roles)
        return res.summary()
    hunter = host if roles["host"] == "Hunter" else client
    hider = client if hunter is host else host
    res.note("hunter=%s hider=%s" % (hunter.label, hider.label))
    run_round(res, host, client, hunter, hider)
    for g in both:
        g.cmd("phstop", wait=1)
    return res.summary()


def start_match(res, host, client):
    """Host a session, join it, start the match and the first round. Returns {label: role}, or None on failure."""
    res.begin("lobby")
    hm, cm = host.mark(), client.mark()
    host.cmd("phhost", wait=2)
    res.check("host session starts", host.wait_line(hm, r"host session started \(Lobby\)", 15) is not None)
    client.cmd("phjoin", wait=2)
    res.check("client session starts", client.wait_line(cm, r"client session started", 15) is not None)
    for k, v in SETTINGS:
        host.cmd("phset %s %s" % (k, v), wait=0.3)
    hm, cm = host.mark(), client.mark()
    host.cmd("phstart", wait=2)
    res.check("host begins the match", host.wait_line(hm, r"match begun", 15) is not None, host.lines(hm)[-3:])
    # A match opens in the Safehouse lobby (round 0), which waits for the host's "start next round".
    for g, m in ((host, hm), (client, cm)):
        line = g.wait_line(m, r"phase -> Safehouse", 15)
        res.check("%s enters the Safehouse" % g.label, line is not None, line or g.lines(m)[-3:])
    time.sleep(2)
    hm, cm = host.mark(), client.mark()
    host.cmd("phnextround", wait=1)
    res.check("host starts the first round", host.wait_line(hm, r"host starting next round", 10) is not None,
              host.lines(hm)[-3:])
    roles = {}
    for g, m in ((host, hm), (client, cm)):
        line = g.wait_line(m, r"phase -> Hiding .*you=(\w+)", 30)
        r = re.search(r"you=(\w+)", line) if line else None
        roles[g.label] = r.group(1) if r else None
        res.check("%s enters Hiding" % g.label, line is not None, line or g.lines(m)[-3:])
    paired = sorted(str(r) for r in roles.values()) == ["Hider", "Hunter"]
    res.check("one hunter and one hider", paired, roles)
    return roles if paired else None


def worn(m):
    """Worn outfit objects from a probe match, or None."""
    return int(m.group(15)) if m is not None and m.group(15) is not None else None


def check_outfit(res, hunter, hider):
    """A stripped hider stays stripped when vanilla re-applies the outfit (it does on every clothing-slot change):
    phredress re-applies it, the next disguise tick must strip it again. Checked on the hider's own view and on the
    hunter's copy of the hider."""
    res.begin("hider outfit")
    time.sleep(2)
    own, seen = probe(hider, None), probe(hunter, "other")
    res.check("hider is stripped (own view)", worn(own) == 0, own.group(0) if own else "no probe line")
    res.check("hider is stripped (hunter's view)", worn(seen) == 0, seen.group(0) if seen else "no probe line")
    for g, arg, who in ((hider, "", None), (hunter, " remote", "other")):
        m = g.mark()
        g.cmd("phredress" + arg, wait=0.5)
        line = g.wait_line(m, r"phredress: .* worn before=(-?\d+) after=(-?\d+)", 5)
        after = int(re.search(r"after=(-?\d+)", line).group(1)) if line else -1
        if after == 0:
            # A test account that never finished the character creator has empty clothing slots, so there is no
            # outfit to re-apply (the client in the rig, see free_camera).
            res.note("%s: the hider's clothing slots are empty (character creator not finished) - redress check "
                     "skipped" % g.label)
            continue
        res.check("%s: phredress puts the outfit back on" % g.label, after > 0, line or g.lines(m)[-3:])
        again = g.wait_line(m, r"appearance: hider \d+ got \d+ worn objects back - stripped again", 5)
        res.check("%s: the disguise tick strips the hider again" % g.label, again is not None, again or g.lines(m)[-3:])
        time.sleep(1)
        p = probe(g, who)
        res.check("%s: hider stays stripped" % g.label, worn(p) == 0, p.group(0) if p else "no probe line")


def check_trash(res, hunter, hider):
    """Trash spawned during a round falls and lands before the round freeze pins it (TrashItem.Initialize gives it its
    spawn velocity). phtrash throws one through the networked TrashManager path, so both clients spawn it."""
    res.begin("trash spawned mid-round")
    hunter.tp(*KNOCK_SPOT)
    free_camera(res, hunter, quiet=True)
    time.sleep(2)
    marks = {g.label: g.mark() for g in (hunter, hider)}
    hunter.cmd("phtrash", wait=0.5)
    thrown = hunter.wait_line(marks[hunter.label], r"phtrash: threw .* round freeze=(\w+)", 5)
    res.check("hunter throws trash during the round freeze",
              thrown is not None and re.search(r"freeze=(\w+)", thrown).group(1) == "True",
              thrown or hunter.lines(marks[hunter.label])[-3:])
    result = hunter.wait_line(marks[hunter.label], r"phtrash: RESULT fell ([-\d.]+) m, ([-\d.]+) m above ground, kinematic=(\w+)", 10)
    m = re.search(r"fell ([-\d.]+) m, ([-\d.]+) m above ground, kinematic=(\w+)", result) if result else None
    res.check("the trash fell to the ground before it froze", m is not None and float(m.group(1)) >= 0.5
              and -0.15 <= float(m.group(2)) <= 0.3 and m.group(3) == "True", result or "no phtrash RESULT line")
    for g in (hunter, hider):
        line = g.wait_line(marks[g.label], r"trash: spawned .+ settled after .* fell ([-\d.]+) m, ([-\d.]+) m above ground - frozen", 10)
        m = re.search(r"fell ([-\d.]+) m, ([-\d.]+) m above ground", line) if line else None
        res.check("%s freezes the spawned trash on the ground" % g.label, m is not None and float(m.group(1)) >= 0.5
                  and -0.15 <= float(m.group(2)) <= 0.3, line or g.lines(marks[g.label], r"trash")[-3:])


def run_round(res, host, client, hunter, hider):
    """Outfit, disguise, trash, concussion knockdown, catch and round end of one round already in Hiding."""
    both = (host, client)
    check_outfit(res, hunter, hider)
    res.begin("disguise")
    hm = hider.mark()
    hider.cmd("phbecome", wait=3)
    req = hider.wait_line(hm, r"phbecome: requesting prop (\d+)", 10)
    res.check("hider requests a prop", req is not None, hider.lines(hm)[-3:])
    time.sleep(3)
    own = probe(hider, None)
    res.check("hider wears the prop (own view)", own is not None and int(own.group(5)) >= 0 and own.group(6) == "True",
              own.group(0) if own else "no probe line")
    seen = probe(hunter, "other")
    res.check("hunter sees the prop clone and not the body", seen is not None and int(seen.group(5)) >= 0
              and seen.group(6) == "True" and seen.group(8) == "False", seen.group(0) if seen else "no probe line")
    hider.shot("coop-disguise-hider")
    hunter.shot("coop-disguise-hunter")

    res.begin("hunting")
    for g in both:
        res.check("%s enters Hunting" % g.label, wait_phase(g, "Hunting", 60) is not None)
    check_trash(res, hunter, hider)

    res.begin("concussion knockdown")
    # Open flat road inside the play area (centre -69,83, r 120): the blast pushes the hunter -x along the road.
    hunter.tp(*KNOCK_SPOT)
    hider.tp(KNOCK_SPOT[0] + 2.5, KNOCK_SPOT[1] + 0.2, KNOCK_SPOT[2], yaw=270)   # facing the hunter (-x)
    time.sleep(3)
    marks = {g.label: g.mark() for g in both}
    hider.cmd("phconcuss", wait=1)
    hostm = marks["host"]
    line = host.wait_line(hostm, r"concussion by \d+ - knocked down (\d+) hunter", 10)
    res.check("host knocks the hunter down", line is not None and re.search(r"knocked down (\d+)", line).group(1) == "1",
              line or host.lines(hostm)[-3:])
    cap = hunter.wait_line(marks[hunter.label], r"knockdown: capsule of \S+ ignores (\d+) ragdoll colliders", 8)
    res.check("the hunter's own capsule lets the ragdoll through", cap is not None, cap or "no capsule line")
    for g in both:
        shove = g.wait_line(marks[g.label], r"knockdown: shove v=([\d.]+) m/s on (\d+) bodies after ([\d.]+)s", 8)
        res.check("%s shoves the downed hunter's body" % g.label, shove is not None, shove or g.lines(marks[g.label])[-3:])
        hold = g.wait_line(marks[g.label], r"knockdown: root speed ([\d.]+) m/s 4 steps after the shove", 8)
        v = float(re.search(r"v=([\d.]+)", shove).group(1)) if shove else 0
        s = float(re.search(r"speed ([\d.]+)", hold).group(1)) if hold else -1
        res.check("%s: the body keeps most of the shove speed" % g.label, s >= 0.7 * v > 0, hold or "no root speed line")
    time.sleep(1.0)
    hunter.shot("coop-knockdown-hunter")
    hider.shot("coop-knockdown-hider")
    for g in both:
        t = g.wait_line(marks[g.label], r"knockdown: travel ([\d.]+) m for", 20)
        d = float(re.search(r"travel ([\d.]+) m", t).group(1)) if t else -1
        res.check("%s: knockdown travel within %.1f-%.1f m" % ((g.label,) + TRAVEL), TRAVEL[0] <= d <= TRAVEL[1], t or "no travel line")
    time.sleep(2)

    res.begin("catch and round end")
    marks = {g.label: g.mark() for g in both}
    caught = None
    for _ in range(12):
        free_camera(res, hunter, quiet=True)
        hunter.cmd("phfire", wait=0.6)
        caught = host.wait_line(marks["host"], r"CAUGHT", 1.0)
        if caught:
            break
    res.check("the hunter's shot catches the hider", caught is not None,
              caught or (host.lines(marks["host"], r"hit |CAUGHT|catch claim")[-3:]
                         + hunter.lines(marks[hunter.label], r"phfire|shot ")[-3:]))
    for g in both:
        end = g.wait_line(marks[g.label], r"phase -> (RoundEnd|MatchEnd)", 20)
        res.check("%s sees the round end" % g.label, end is not None, end or g.lines(marks[g.label])[-3:])
    hunter.shot("coop-round-end-hunter")


if __name__ == "__main__":
    raise SystemExit(main())
