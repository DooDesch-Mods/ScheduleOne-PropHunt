"""
Card PropHunt#35: a joiner must not sit in the vanilla character creator when a round starts.

Runs the player's own path, not the debug one (phhost/phjoin in a vanilla lobby): the host presses "Host lobby" for
PropHunt on Side Hustle's hub page (a public lobby named LOBBY), the client presses Join on that lobby in the hub's
lobby list, and the host starts a round. Both presses go through the page's own bridge calls (hub.host, hub.join).
Side Hustle turns the intro off for a PropHunt session (GamemodeDescriptor.SkipIntro), so the joiner, whose test
account has no player data in the host's fresh world, must arrive in the world without the intro and without the
creator, and its camera must stay with its body through the round start.

    pwsh -File Workspace/tools/session/session.ps1 -Coop -Build _wt-PropHunt-35 -Save 0 -NoApp
    python tests/joiner_creator.py --host mptest3 --client mptest5

Both slots start in the main menu with a Debug build of PropHunt and Side Hustle.
"""
import argparse
import json
import math
import time

from check import Results, Slot

GAMEMODE = "doodesch.prophunt"
HUB = "sidehustle-hub"
LOBBY = "ph35"
SETTINGS = [("hide", 25), ("hunt", 60), ("end", 8), ("round", "Single"), ("area", 120)]
INTRO = r"Player has not completed intro; playing intro"
HYGIENE = r"\[hygiene\] applied for 'PropHunt' \(skipIntro=(\w+)"
CREATOR_UI = "Character Creator Interface"


def camera_gap(game):
    """Metres between the player and the camera, or None when the bridge reports neither."""
    where = (game.g.world("player_position", {}, timeout_ms=5000) or {}).get("result") or {}
    pos, cam = where.get("position"), (where.get("camera") or {}).get("position")
    return math.dist(pos, cam) if pos and cam else None


def creator_open(game):
    res = game.g.call("ui_find", {"query": CREATOR_UI})
    return ((res or {}).get("result") or res or {}).get("count", 0) > 0


def hub_eval(game, code):
    """Runs code in the hub page; None while the page is not mounted."""
    try:
        return game.g.eval(HUB, code)
    except Exception as e:
        print("      [%s] hub eval failed: %s" % (game.label, e), flush=True)
        return None


def hub_call(game, name, arg):
    """One of the page's bridge calls, the way its buttons make it. Returns the refusal text ('' = accepted)."""
    return hub_eval(game, "call(%s, %s)" % (json.dumps(name), json.dumps(arg)))


def wait_hub(res, game, timeout=180):
    end = time.time() + timeout
    while time.time() < end:
        if hub_eval(game, "typeof call") == "function":
            return True
        time.sleep(3)
    return res.check("%s: Side Hustle's hub page is up" % game.label, False, "not mounted after %d s" % timeout)


def wait_world(res, game, timeout):
    ready, last = game.g.wait_ready(timeout=timeout, say=lambda s: None)
    return res.check("%s reaches the loaded world" % game.label, ready, last)


def check_no_creator(res, game, mark, when):
    intro = game.lines(mark, INTRO)
    res.check("%s: no intro cutscene %s" % (game.label, when), not intro, intro[-1] if intro else "")
    res.check("%s: the character creator is closed %s" % (game.label, when), not creator_open(game))
    d = camera_gap(game)
    res.check("%s: the camera is with the player %s" % (game.label, when), d is not None and d <= 5,
              "%.1f m" % d if d is not None else "no position")


def check_hygiene(res, game, mark):
    line = game.wait_line(mark, HYGIENE, 30)
    res.check("%s: Side Hustle applies the PropHunt hygiene with skipIntro" % game.label,
              line is not None and "skipIntro=True" in line,
              line or game.lines(mark, r"\[mp\]|\[hub3?\]|hygiene")[-4:])


def host_session(res, host):
    res.begin("host hosts PropHunt from the hub")
    if not wait_hub(res, host):
        return False
    m = host.mark()
    why = hub_call(host, "hub.host", {"id": GAMEMODE, "players": 4, "visibility": 0, "lobbyName": LOBBY,
                                      "password": "", "policyMode": 0})
    res.check("host: the hub accepts Host lobby", why == "", why)
    check_hygiene(res, host, m)
    if not wait_world(res, host, 240):
        return False
    time.sleep(5)
    check_no_creator(res, host, m, "after the host's world loads")
    return True


PRESS = """(function(){var v=(read()||{}).view;if(!v)return'';var hit=null;
(function f(o){if(!o||hit)return;if(Array.isArray(o)){o.forEach(f);return}
if(typeof o==='object'){if(o.label===%s&&o.act){hit=o.act;return}for(var k in o)f(o[k])}})(v);
if(!hit)return v.key+':no button';viewAct(hit,'');return v.key+':'+hit})()"""


def pass_consent(res, client, timeout=20):
    """The client carries test mods the host lacks (the bridge among them), so Side Hustle shows its sync screen.
    The player's own choice there is "Join without syncing"; syncing would restart the game and pause the bridge."""
    end = time.time() + timeout
    while time.time() < end:
        key = hub_eval(client, "((read()||{}).view||{}).key||''") or ""
        if key == "join.consent":
            got = hub_eval(client, PRESS % json.dumps("Join without syncing")) or ""
            res.check("client: presses Join without syncing on the sync screen", got.endswith(":no button") is False and
                      got.startswith("join.consent:"), got)
            return
        time.sleep(2)
    res.note("client: no sync screen within %d s (mod sets match)" % timeout)


def join_session(res, client):
    res.begin("client joins from the hub's lobby list")
    if not wait_hub(res, client):
        return None
    lobby = None
    for _ in range(20):
        hub_call(client, "hub.refresh", "")
        time.sleep(4)
        raw = hub_eval(client, "JSON.stringify((read()||{}).lobbies||[])")
        rows = json.loads(raw) if raw else []
        mine = [r for r in rows if r.get("mode") == GAMEMODE and r.get("name") == LOBBY]
        if mine:
            lobby = mine[0]
            break
    if not res.check("client: the host's PropHunt lobby is in the list", lobby is not None,
                     lobby and "%s %s/%s" % (lobby.get("id"), lobby.get("members"), lobby.get("max"))):
        return None
    m = client.mark()
    why = hub_call(client, "hub.join", {"id": lobby["id"], "password": "", "anyway": True})
    res.check("client: the hub accepts Join", why == "", why)
    pass_consent(res, client)
    check_hygiene(res, client, m)
    if not wait_world(res, client, 420):
        return None
    time.sleep(8)
    check_no_creator(res, client, m, "after joining")
    return m


def round_start(res, host, client, client_mark):
    res.begin("round start")
    for k, v in SETTINGS:
        host.cmd("phset %s %s" % (k, v), wait=0.3)
    hm, cm = host.mark(), client.mark()
    host.cmd("phstart", wait=2)
    res.check("host begins the match", host.wait_line(hm, r"match begun", 20) is not None, host.lines(hm)[-3:])
    for g, mk in ((host, hm), (client, cm)):
        line = g.wait_line(mk, r"phase -> Safehouse", 20)
        res.check("%s enters the Safehouse" % g.label, line is not None, line or g.lines(mk)[-3:])
    time.sleep(3)
    check_no_creator(res, client, client_mark, "in the Safehouse")
    hm, cm = host.mark(), client.mark()
    host.cmd("phnextround", wait=1)
    for g, mk in ((host, hm), (client, cm)):
        line = g.wait_line(mk, r"phase -> Hiding .*you=(\w+)", 30)
        res.check("%s enters Hiding" % g.label, line is not None, line or g.lines(mk)[-3:])
    time.sleep(4)
    check_no_creator(res, client, client_mark, "after the round starts")
    client.shot("joiner-round-start-client")
    host.shot("joiner-round-start-host")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", required=True)
    ap.add_argument("--client", required=True)
    a = ap.parse_args()
    res = Results()
    host, client = Slot(a.host, "host"), Slot(a.client, "client")
    if not host_session(res, host):
        return res.summary()
    cm = join_session(res, client)
    if cm is None:
        return res.summary()
    round_start(res, host, client, cm)
    return res.summary()


if __name__ == "__main__":
    raise SystemExit(main())
