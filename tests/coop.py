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
import os
import re
import time

from check import Results, Slot, first

TRAVEL = (1.0, 4.0)
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
                      r"meshesVisible=(\w+) ragdolled=(\w+) downed=(\w+) eliminated=(\w+) pos=\(([-\d.]+),([-\d.]+),([-\d.]+)\)", line)
        if not m:
            continue
        if (name_part is None and m.group(3) == "True") or (name_part is not None and m.group(3) == "False"):
            return m
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", required=True)
    ap.add_argument("--client", required=True)
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
    roles = {}
    for g, m in ((host, hm), (client, cm)):
        line = g.wait_line(m, r"phase -> Hiding .*you=(\w+)", 30)
        r = re.search(r"you=(\w+)", line) if line else None
        roles[g.label] = r.group(1) if r else None
        res.check("%s enters Hiding" % g.label, line is not None, line or g.lines(m)[-3:])
    res.check("one hunter and one hider", sorted(roles.values()) == ["Hider", "Hunter"], roles)
    if sorted(roles.values()) != ["Hider", "Hunter"]:
        return res.summary()
    hunter = host if roles["host"] == "Hunter" else client
    hider = client if hunter is host else host
    res.note("hunter=%s hider=%s" % (hunter.label, hider.label))

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

    res.begin("concussion knockdown")
    hx, hy, hz = hunter.position()
    hider.tp(hx + 2.5, hy + 0.2, hz)
    time.sleep(2)
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
        hunter.cmd("phfire", wait=0.6)
        caught = host.wait_line(marks["host"], r"CAUGHT", 1.0)
        if caught:
            break
    res.check("the hunter's shot catches the hider", caught is not None,
              caught or host.lines(marks["host"], r"hit |CAUGHT|phfire")[-4:])
    for g in both:
        end = g.wait_line(marks[g.label], r"phase -> (RoundEnd|MatchEnd)", 20)
        res.check("%s sees the round end" % g.label, end is not None, end or g.lines(marks[g.label])[-3:])
    hunter.shot("coop-round-end-hunter")

    for g in both:
        g.cmd("phstop", wait=1)
    return res.summary()


if __name__ == "__main__":
    raise SystemExit(main())
