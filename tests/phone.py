"""
The phone app, screen by screen, in a running test game.

Every pane of every phase is opened the way a player opens it - a click on its tab through the real uGUI raycast -
then measured inside the page and photographed. The measurement is what makes this a check rather than a photo
album: every box is read with getBoundingClientRect, and the script fails on

  - a box that runs out of its parent sideways (text or a control pushed past its column),
  - a box that runs out of its parent downwards where nothing scrolls (clipped at the bottom),
  - two siblings that overlap,
  - anything outside the 733 x 400 panel.

The pane and the rail's vitals are the two scroll containers; content below their fold is expected and reported as
"scrolls", and the end of a long pane is photographed a second time after scrolling it there.

Usage (the slot needs a Debug build of PropHunt with the app open, as session.ps1 -App prophunt leaves it):

    python tests/phone.py --slot mptest                 # walk a solo match: lobby, rules categories, every phase
    python tests/phone.py --slot mptest --only          # just the screen that is up now, all four panes
    python tests/phone.py --slot mptest2 --label client --only   # the same from a client in a duo run
    python tests/phone.py --slot mptest3 --client mptest4       # a joined pair (coop_up.ps1): both phones, one match

Shots land in tests/out/phone-<label>-<phase>-<pane>.png.
"""
import argparse
import json
import re
import sys
import time

from PIL import Image

from check import Results, Slot

# Points inside the phone screen of a 1920x1080 shot: rail top and bottom, tab bar, pane corners and middle.
PHONE_SAMPLES = [(545, 300), (545, 720), (700, 735), (1000, 290), (1380, 300), (1380, 720), (1000, 735), (800, 300)]
# The app's flat surfaces: rail, pane, tab bar, and the same surfaces under a dialog's scrim. A dark world (grass
# at night) comes close but never hits these.
PHONE_COLOURS = [(13, 13, 13), (13, 13, 22), (22, 28, 28), (2, 13, 13), (3, 15, 15)]


def on_phone(px):
    return any(all(abs(a - b) <= 3 for a, b in zip(px, c)) for c in PHONE_COLOURS)

APP = "prophunt"
PANES = ["board", "roster", "rules", "scores"]

# Runs inside the page. ES5 on purpose: it is pasted into whatever the page's engine accepts.
AUDIT_JS = r"""
(function () {
  var W = 733, H = 400, out = [], scrolls = [];
  function name(e) {
    var s = (e.tagName || '').toLowerCase();
    if (e.id) s += '#' + e.id;
    var c = String(e.className || '').trim();
    if (c) s += '.' + c.split(/\s+/).join('.');
    return s;
  }
  function path(e) {
    var p = [], n = e;
    for (var i = 0; i < 3 && n; i++) { p.unshift(name(n)); n = n.parentElement; }
    return p.join(' > ');
  }
  function text(e) {
    if (e.children.length !== 0) return '';
    var t = String(e.textContent || '');
    return t ? ' "' + t.slice(0, 32) + '"' : '';
  }
  function cls(e) { return ' ' + String(e.className || '') + ' '; }
  function isScroller(e) { return e.id === 'pane' || cls(e).indexOf(' vitals ') >= 0; }
  function inScroller(e) {
    for (var n = e.parentElement; n; n = n.parentElement) if (isScroller(n)) return true;
    return false;
  }
  function f(v) { return Math.round(v); }
  // The clock is a stack of glyph reels clipped to one cell by design, and a digit's ghost sits under its reel.
  var skip = /\b(reel|cell|glyph|digit-ghost|digit)\b/;

  var nodes = document.querySelectorAll('#app *');
  for (var i = 0; i < nodes.length; i++) {
    var e = nodes[i];
    if (skip.test(String(e.className || ''))) continue;
    var r = e.getBoundingClientRect();
    if (r.width < 0.5 || r.height < 0.5) continue;
    var p = e.parentElement;
    if (!p) continue;
    var pr = p.getBoundingClientRect();
    var where = path(e) + text(e);

    // The overlay is position: fixed, measured against the panel; its holder #layer has no box of its own.
    var overlay = p.id === 'layer';
    if (!overlay && (r.left < pr.left - 1 || r.right > pr.right + 1))
      out.push('runs out sideways: ' + where + ' [' + f(r.left) + '..' + f(r.right) + ' in ' + f(pr.left) + '..' + f(pr.right) + ']');

    if (!isScroller(p) && !overlay && (r.top < pr.top - 1 || r.bottom > pr.bottom + 1))
      out.push('runs out downwards: ' + where + ' [' + f(r.top) + '..' + f(r.bottom) + ' in ' + f(pr.top) + '..' + f(pr.bottom) + ']');

    if (r.left < -1 || r.right > W + 1)
      out.push('off the panel sideways: ' + where + ' [' + f(r.left) + '..' + f(r.right) + ']');
    if (!inScroller(e) && (r.top < -1 || r.bottom > H + 1))
      out.push('off the panel downwards: ' + where + ' [' + f(r.top) + '..' + f(r.bottom) + ']');
  }

  // Siblings overlapping each other. Absolute and fixed boxes are layered on purpose and stay out of it.
  var parents = document.querySelectorAll('#app, #app *');
  for (var k = 0; k < parents.length; k++) {
    var par = parents[k];
    if (skip.test(String(par.className || '')) || cls(par).indexOf(' clock ') >= 0) continue;
    var kids = par.children;
    if (!kids || kids.length < 2) continue;
    var rects = [];
    for (var a = 0; a < kids.length; a++) {
      var kr = kids[a].getBoundingClientRect();
      if (kids[a].id === 'layer' || skip.test(String(kids[a].className || '')) || kr.width < 0.5 || kr.height < 0.5) { rects.push(null); continue; }
      rects.push(kr);
    }
    for (var x = 0; x < rects.length; x++) {
      for (var y = x + 1; y < rects.length; y++) {
        var A = rects[x], B = rects[y];
        if (!A || !B) continue;
        var ox = Math.min(A.right, B.right) - Math.max(A.left, B.left);
        var oy = Math.min(A.bottom, B.bottom) - Math.max(A.top, B.top);
        if (ox > 1 && oy > 1)
          out.push('overlap: ' + path(kids[x]) + text(kids[x]) + ' with ' + name(kids[y]) + text(kids[y]) + ' [' + f(ox) + 'x' + f(oy) + ']');
      }
    }
  }

  var scrollers = [document.getElementById('pane'), document.getElementById('vitals')];
  for (var s = 0; s < scrollers.length; s++) {
    var box = scrollers[s];
    if (!box) continue;
    var br = box.getBoundingClientRect(), bottom = br.top;
    for (var c = 0; c < box.children.length; c++) bottom = Math.max(bottom, box.children[c].getBoundingClientRect().bottom);
    scrolls.push({ id: box.id, view: f(br.bottom - br.top), content: f(bottom - br.top) });
  }

  return JSON.stringify({ issues: out, scrolls: scrolls });
})()
"""

STATE_JS = r"""
(function () {
  function t(id) { var e = document.getElementById(id); return e ? String(e.textContent || '') : ''; }
  var pane = document.getElementById('pane');
  return JSON.stringify({
    phase: t('phase'), seat: t('seat'), role: t('role'), tally: t('tallycount'), note: t('clocknote'),
    vitals: t('vitals'),
    clockbox: String(document.getElementById('clockbox').className || ''),
    tallybox: String(document.getElementById('tallybox').className || ''),
    pane: pane ? String(pane.textContent || '').slice(0, 400) : '',
    layer: document.getElementById('layer').children.length
  });
})()
"""


ROSTER_JS = r"""
(function () {
  var rows = document.querySelectorAll('#pane .row'), out = [];
  for (var i = 0; i < rows.length; i++) {
    var note = rows[i].querySelector('.row-note'), stamps = rows[i].querySelectorAll('.stamp'), st = [];
    for (var k = 0; k < stamps.length; k++) st.push(String(stamps[k].textContent || ''));
    out.push({ note: note ? String(note.textContent || '') : '', stamps: st });
  }
  return JSON.stringify(out);
})()
"""

# A prompt name that still carries mesh noise: a LOD tag, a ".003" suffix, an underscore or a bare modelling word.
RAW_NAME = re.compile(r"LOD\d|\.\d{3}|_|^(body|cube|mesh|object|default|model)$", re.I)


def ranks_ok(rows):
    """Standard competition ranking over (place, score) rows sorted by score: equal scores share a place, the next
    score takes the place after everyone above it."""
    for i, (rank, score) in enumerate(rows):
        want = 1 if i == 0 else (rows[i - 1][0] if score == rows[i - 1][1] else i + 1)
        if rank != want:
            return False
    return True


class Phone:
    def __init__(self, slot, label, results):
        self.s = slot
        self.label = label
        self.r = results

    def shot(self, name, blinded=False):
        """A screenshot that shows the phone. A shot can land in the frames the phone is not drawn (it opening, or
        a session start reopening it); those show the world instead. A shot whose sample points are mostly not the
        phone's own flat colours is taken again, and a phone that never shows is a FAIL. A hunter while the hiders
        hide is blinded: the black blind covers the whole screen, phone included, and that is checked instead."""
        hits = dark = 0
        for attempt in range(4):
            path = self.s.shot(name)
            if not path:
                return None
            img = Image.open(path).convert("RGB").resize((1920, 1080))
            px = [img.getpixel(p) for p in PHONE_SAMPLES]
            hits = sum(1 for c in px if on_phone(c))
            dark = sum(1 for c in px if max(c) <= 4)
            if hits >= len(PHONE_SAMPLES) - 3:
                if attempt:
                    self.r.note("%s: phone showed on attempt %d" % (name, attempt + 1))
                return path
            if blinded and dark >= len(PHONE_SAMPLES) - 3:
                self.r.check("%s: the hunter's blind covers the screen" % name, True)
                return path
            time.sleep(1.0)
        if blinded:
            self.r.check("%s: the hunter's blind covers the screen" % name, False,
                         "%d of %d sample points black, %d on the phone" % (dark, len(PHONE_SAMPLES), hits))
        else:
            self.r.check("%s: the phone is on screen" % name, False, "%d of %d sample points on the phone" % (hits, len(PHONE_SAMPLES)))
        return path

    def js(self, code):
        try:
            return self.s.g.eval(APP, code)
        except Exception as e:  # the bridge answers with an exception object when the page is not mounted
            print("      [%s] eval failed: %s" % (self.label, e), flush=True)
            return None

    def state(self):
        raw = self.js(STATE_JS)
        try:
            return json.loads(raw) if raw else {}
        except ValueError:
            print("      [%s] state was not JSON: %r" % (self.label, raw), flush=True)
            return {}

    def open_pane(self, pane):
        """A real click on the tab. Falls back to a script click only to keep the walk going, and says so."""
        selector = '.tab[data-pane="%s"]' % pane
        try:
            res = self.s.g.click(APP, selector)
            ok = bool(res) and not (isinstance(res, dict) and res.get("error"))
        except Exception as e:
            res, ok = str(e), False
        if not ok:
            self.r.check("tab %s answers a real click" % pane, False, res)
            self.js("document.querySelector('%s').click(); 'clicked'" % selector)
        time.sleep(0.6)   # the page renders on a 30 ms debounce after its pull; a shot right away shows the old pane

    def press(self, selector, what):
        """A real click on a control of the open pane, the way the host presses it."""
        try:
            res = self.s.g.click(APP, selector)
            ok = bool(res) and not (isinstance(res, dict) and res.get("error"))
        except Exception as e:
            res, ok = str(e), False
        self.r.check("%s answers a real click" % what, ok, res)
        time.sleep(0.6)
        return ok

    def audit(self, where):
        raw = self.js(AUDIT_JS)
        if isinstance(raw, str) and "TimeoutException" in raw:
            # The audit walks every box; on a loaded machine it can run past Sideload's 250 ms script budget once.
            print("      [%s] layout audit of %s ran past the script budget; measuring again" % (self.label, where),
                  flush=True)
            time.sleep(0.5)
            raw = self.js(AUDIT_JS)
        try:
            data = json.loads(raw) if raw else None
        except ValueError:
            data = None
        if data is None:
            return self.r.check("layout %s" % where, False, "audit returned %r" % (raw,)), None
        issues = data.get("issues") or []
        for sc in data.get("scrolls") or []:
            if sc["content"] > sc["view"] + 1:
                self.r.note("%s scrolls: %d px of content in %d px" % (sc["id"], sc["content"], sc["view"]))
        detail = "; ".join(issues[:6]) + (" (+%d more)" % (len(issues) - 6) if len(issues) > 6 else "")
        return self.r.check("layout %s" % where, not issues, detail), data

    def capture(self, phase_tag, panes=PANES):
        """Every pane of the screen that is up now: click, measure, photograph, and the end of a long pane."""
        blinded = phase_tag.startswith("hiding") and self.state().get("role", "").strip().upper() == "HUNTER"
        # First, while the phase is fresh: the round-over scoreboard is up for only a few seconds.
        self.hud_checks(phase_tag)
        for pane in panes:
            self.open_pane(pane)
            where = "%s/%s" % (phase_tag, pane)
            _, data = self.audit(where)
            self.shot("phone-%s-%s-%s" % (self.label, phase_tag, pane), blinded)
            if pane == "board":
                self.board_checks(phase_tag)
            if pane == "roster" and phase_tag.split("-")[0] in ("roundover", "matchover"):
                text = self.state().get("pane", "")
                self.r.check("%s: a hider who made it is not told they hide next round" % phase_tag,
                             "Hiding next round" not in text, text[:160])
            if pane == "roster":
                self.roster_checks(phase_tag)
            for sc in (data or {}).get("scrolls") or []:
                if sc["id"] == "pane" and sc["content"] > sc["view"] + 1:
                    # scrollToEnd only pins the box; the pin lands on the next rebuild, so one is forced with a
                    # text write that ends where it started.
                    self.js("(function(){document.getElementById('pane').scrollToEnd();"
                            "var n=document.getElementById('clocknote'),t=n.textContent;n.textContent=t+'.';n.textContent=t;"
                            "return 'ok';})()")
                    time.sleep(0.4)
                    self.shot("phone-%s-%s-%s-end" % (self.label, phase_tag, pane))
        # hud_checks points the crosshair at a prop in a round; it stays on through the shots, so they show the open
        # phone with a prompt the HUD holds back.
        if phase_tag.split("-")[0] in ("hiding", "hunting"):
            self.s.cmd("phhud look off", wait=0.2)
        self.state_checks(phase_tag)

    def roster_checks(self, phase_tag):
        """A row's stamp has to agree with its note: no role at all in the lobby, no SPECTATING next to a player who
        is ready or waiting for a round."""
        raw = self.js(ROSTER_JS)
        try:
            rows = json.loads(raw) if raw else None
        except ValueError:
            rows = None
        if rows is None:
            return self.r.check("%s: roster rows readable" % phase_tag, False, raw)
        roles = [st for row in rows for st in row["stamps"] if st != "FRIEND"]
        if phase_tag.split("-")[0] == "lobby":
            self.r.check("%s: the lobby roster shows no role stamp" % phase_tag, not roles, roles)
        clash = [row for row in rows
                 if row["note"] in ("Ready to play", "Waiting for the next round") and "SPECTATING" in row["stamps"]]
        self.r.check("%s: no SPECTATING stamp next to a waiting player" % phase_tag, not clash, clash)

    def hud(self):
        """The phhud report as a dict: phone, becomePill, hint, card, help, becomeWanted, headline, rows."""
        lines = self.s.cmd("phhud", wait=0.6)
        line = next((l for l in reversed(lines) if "hud: phone=" in l), "")
        out = dict(re.findall(r"(\w+)=(open|closed|on|off)\b", line))
        for key in ("becomeWanted", "hintWanted", "headline"):
            m = re.search(r"%s='([^']*)'" % key, line)
            out[key] = m.group(1) if m else None
        m = re.search(r"rows=([\d:,\-]*)", line)
        out["rows"] = [tuple(int(v) for v in pair.split(":")) for pair in m.group(1).split(",") if pair] if m else []
        out["scoreboardHidden"] = "(hidden) scoreboard" in line
        out["neverBuilt"] = any("the HUD was never built" in l for l in lines)
        out["line"] = line
        return out

    def hud_checks(self, phase_tag):
        """While the phone is up, nothing of the HUD sits on top of it: no become prompt, no hint line, no role card,
        no controls overlay. In a round the crosshair is pointed at a catalog prop first, so the prompt has a name
        it would show, and that name has to read as a name."""
        phase = phase_tag.split("-")[0]
        in_round = phase in ("hiding", "hunting")
        if in_round:
            self.s.cmd("phhud look on", wait=0.4)
        h = self.hud()
        if not h.get("phone") and h["neverBuilt"] and phase == "nomatch":
            # Before the first session there is no HUD at all, so nothing of it can sit on the phone.
            return self.r.check("%s: no HUD exists before a session" % phase_tag, True)
        if not h.get("phone"):
            return self.r.check("%s: phhud answers" % phase_tag, False, h["line"] or "no hud line")
        self.r.check("%s: the phone is open for the HUD check" % phase_tag, h["phone"] == "open", h["line"])
        hidden = all(h.get(k) == "off" for k in ("becomePill", "hint", "card", "help", "scoreboardCard"))
        self.r.check("%s: no prompt, hint, role card, help or scoreboard over the open phone" % phase_tag, hidden,
                     h["line"])
        if in_round and self.state().get("role", "").strip().upper() == "HIDER":
            wanted = h.get("becomeWanted") or ""
            self.r.check("%s: the hider's prompt has a target to name" % phase_tag, bool(wanted), h["line"])
            self.r.check("%s: the prompt names the prop, not its mesh ('%s')" % (phase_tag, wanted),
                         bool(wanted) and not RAW_NAME.search(wanted), wanted)
        if phase == "roundover":
            self.r.check("%s: the scoreboard has a headline" % phase_tag,
                         not h["scoreboardHidden"] and bool(h.get("headline")), h["line"])
            self.r.check("%s: tied scores share a place on the scoreboard" % phase_tag,
                         bool(h["rows"]) and ranks_ok(h["rows"]), h["rows"])

    def names_check(self):
        """Every catalog prop's shown name, from the phnames report: none may still read as mesh noise."""
        lines = self.s.cmd("phnames", wait=1.5)
        line = next((l for l in reversed(lines) if "names: total=" in l), "")
        m = re.search(r"total=(\d+) readable=(\d+) fallback=(\d+) raw=(\d+)", line)
        if not m:
            return self.r.check("%s: phnames answers" % self.label, False, line or "no names line")
        total, readable, fallback, raw = (int(v) for v in m.groups())
        self.r.note("%s: %d props, %d readable, %d shown as 'this prop'" % (self.label, total, readable, fallback))
        for l in lines:
            if "names: RAW" in l or "names: fallback" in l:
                print("      " + l.strip(), flush=True)
        self.r.check("%s: no catalog prop shows a mesh name" % self.label, total > 0 and raw == 0, line)

    def board_checks(self, phase_tag):
        """What the board says has to fit who is reading it."""
        st = self.state()
        role, pane = st.get("role", "").strip().upper(), st.get("pane", "")
        if role == "HIDER":
            self.r.check("%s: a hider in the round is not told they are out" % phase_tag,
                         "out for this round" not in pane, pane[:120])
        if "HOST" in st.get("seat", "").upper():
            self.r.check("%s: the host is not told to wait for the host" % phase_tag,
                         "Waiting for the host" not in st.get("note", ""), st.get("note", ""))

    def state_checks(self, phase_tag):
        """What the rail says has to agree with itself."""
        st = self.state()
        if phase_tag.split("-")[0] in ("roundover", "matchover"):
            self.r.check("%s: the rail promises no round that is not coming" % phase_tag,
                         "once the round starts" not in st.get("vitals", ""), st.get("vitals", ""))
        if phase_tag.split("-")[0] == "lobby":
            self.r.check("%s: the lobby counts the reader" % phase_tag,
                         not st.get("note", "").strip().startswith("0 "), st.get("note", ""))
        m = re.match(r"\s*(\d+)\s*/\s*(\d+)", st.get("tally", ""))
        if m and "hidden" not in st.get("tallybox", ""):
            alive, total = int(m.group(1)), int(m.group(2))
            self.r.check("%s: hiders left never exceeds the hiders who started" % phase_tag, alive <= total, st["tally"])
        if phase_tag.split("-")[0] in ("lobby", "betweenrounds", "matchover", "nomatch"):
            self.r.check("%s: no hider tally outside a round" % phase_tag, "hidden" in st.get("tallybox", ""),
                         st.get("tallybox", ""))
        board_numbers = re.findall(r"Next round in \d+s", st.get("pane", ""))
        self.r.check("%s: the countdown is said once, by the clock" % phase_tag, not board_numbers, board_numbers)

    def rules_categories(self, phase_tag):
        """The rules pane opens on one category; every other one is a chip away and gets the same audit."""
        self.open_pane("rules")
        count = self.js("String((function(){var a=document.querySelectorAll('.chips');return a.length?a[a.length-1].children.length:0;})())")
        try:
            n = int(count or 0)
        except ValueError:
            n = 0
        self.r.note("rules: %d category chips" % n)
        for i in range(n):
            label = self.js("(function(){var a=document.querySelectorAll('.chips'),c=a[a.length-1].children[%d]; c.click(); return String(c.textContent);})()" % i)
            time.sleep(0.6)
            tag = re.sub(r"[^a-z0-9]+", "", str(label or i).lower()) or str(i)
            self.audit("%s/rules-%s" % (phase_tag, tag))
            self.shot("phone-%s-%s-rules-%s" % (self.label, phase_tag, tag))

    def confirm_dialog(self, phase_tag, button_text):
        """Open a host confirm through a real click on its button, photograph it, then back out."""
        self.open_pane("board")
        found = self.js("(function(){var b=document.querySelectorAll('.danger');for(var i=0;i<b.length;i++)"
                        "if(String(b[i].textContent).indexOf('%s')>=0){b[i].click();return 'yes';}return 'no';})()" % button_text)
        if found != "yes":
            self.r.check("%s: '%s' button is on the board" % (phase_tag, button_text), False, found)
            return
        time.sleep(0.6)
        st = self.state()
        self.r.check("%s: '%s' asks first" % (phase_tag, button_text), st.get("layer", 0) > 0, st.get("layer"))
        self.audit("%s/confirm" % phase_tag)
        self.shot("phone-%s-%s-confirm" % (self.label, phase_tag))
        self.js("(function(){var b=document.querySelectorAll('.dialog .btn');if(b.length)b[0].click();return 'ok';})()")
        time.sleep(0.6)
        self.r.check("%s: 'Keep playing' closes the question" % phase_tag, self.state().get("layer", 1) == 0)


def tag_of(phase_text):
    return re.sub(r"[^a-z0-9]+", "", phase_text.lower()) or "none"


def walk_solo(ph, timeout):
    s, r = ph.s, ph.r

    st = ph.state()
    if st.get("phase", "") in ("", "NO MATCH"):
        ph.capture("nomatch", panes=["board"])
        s.cmd("phhost", wait=3.0)

    r.begin("%s lobby" % ph.label)
    ph.capture("lobby")
    ph.rules_categories("lobby")

    # Short phases so the whole round fits in a minute; the auto start carries it into the next one. Set after
    # the rules walk, and as the last word before the match, so nothing on the phone can override them.
    for key, value in (("round", "Continuous"), ("hide", "20"), ("hunt", "25"), ("end", "12"), ("autostart", "1")):
        s.cmd("phset %s %s" % (key, value), wait=0.3)

    s.cmd("phsolo", wait=2.0)

    # The safehouse comes twice: once at the start of the match, waiting for the host, and once after a round, where
    # auto start counts down to the next one. Those are different screens, so each is its own capture.
    wanted = {"betweenrounds", "hiding", "hunting", "roundover", "betweenrounds-auto"}
    seen = {"lobby"}
    end = time.time() + timeout
    confirmed = False
    while time.time() < end:
        tag = tag_of(ph.state().get("phase", ""))
        if tag == "betweenrounds" and "roundover" in seen:
            tag = "betweenrounds-auto"
        if tag not in seen:
            seen.add(tag)
            r.begin("%s %s" % (ph.label, tag))
            ph.capture(tag)
            if tag == "hiding":
                ph.names_check()
            if tag == "hunting" and not confirmed:
                ph.confirm_dialog(tag, "END ROUND")
                confirmed = True
            if tag == "betweenrounds":
                # A match opens in the safehouse and waits for the host; auto start only carries later rounds.
                ph.open_pane("board")
                ph.press("#pane .act", "START NEXT ROUND")
        if wanted <= seen:
            break
        time.sleep(1.0)
    missing = wanted - seen
    r.check("%s: walked every round phase" % ph.label, not missing, sorted(missing))
    if missing:
        return

    # The last round of a match: a single-round match ends after the scoreboard, which is the only way to reach the
    # match-over screen without waiting out a whole rotation. The round-over screen of this round says so too.
    s.cmd("phset round Single", wait=0.3)
    last = set()
    end = time.time() + 150
    while time.time() < end:
        tag = tag_of(ph.state().get("phase", ""))
        if tag in ("roundover", "matchover") and tag not in last:
            last.add(tag)
            r.begin("%s %s last" % (ph.label, tag))
            ph.capture(tag + "-last")
        if "matchover" in last:
            break
        time.sleep(1.0)
    r.check("%s: reached match over" % ph.label, "matchover" in last, sorted(last))


def open_app(ph):
    """Raise the phone on the PropHunt app. The phone refuses for a while after a load, so this asks for ten seconds."""
    end = time.time() + 10
    res = None
    while time.time() < end:
        try:
            res = ph.s.g.call("sideload_open_app", {"appId": APP, "open": True})
        except Exception as e:
            res = {"error": str(e)}
        if isinstance(res, dict) and (res.get("onScreen") or (res.get("result") or {}).get("onScreen")):
            return True
        time.sleep(1.0)
    ph.r.check("%s: the PropHunt app comes up" % ph.label, False, res)
    return False


def creator_open(ph):
    try:
        res = ph.s.g.call("ui_find", {"query": "Character Creator Interface"})
    except Exception as e:
        print("      [%s] ui_find failed: %s" % (ph.label, e), flush=True)
        return False
    return ((res or {}).get("result") or res or {}).get("count", 0) > 0


def phone_up(ph):
    try:
        res = ph.s.g.call("sideload_open_app", {"appId": APP, "open": True})
    except Exception as e:
        print("      [%s] sideload_open_app failed: %s" % (ph.label, e), flush=True)
        return False
    return isinstance(res, dict) and bool(res.get("onScreen") or (res.get("result") or {}).get("onScreen"))


def finish_creator(ph, within=300):
    """A player new to the host's save first sits through the intro, then lands in the character creator, and the
    game will not raise the phone during either. Waits for the creator or a phone that comes up, whichever is first,
    and presses the creator through with Next and Done like a player."""
    start = time.time()
    while not creator_open(ph):
        if phone_up(ph):
            print("      [%s] no character creator; the phone comes up after %d s" % (ph.label, time.time() - start), flush=True)
            return True
        if time.time() - start > within:
            return ph.r.check("%s: reaches the world (creator or phone)" % ph.label, False,
                              "neither the creator nor the phone after %d s" % within)
        time.sleep(3.0)
    for _ in range(12):
        res = ph.s.g.call("ui_find", {"query": "Done"})
        done = ((res or {}).get("result") or res or {}).get("count", 0)
        ph.s.g.call("ui_click", {"query": "Done" if done else "Next"})
        time.sleep(1.5)
        if not creator_open(ph):
            time.sleep(3.0)
            return ph.r.check("%s: finishes the character creator" % ph.label, True)
    return ph.r.check("%s: finishes the character creator" % ph.label, False, "creator still open after 12 clicks")


def walk_coop(host, client, timeout):
    """One single-round match in a joined co-op pair, both phones captured at every phase. The client is the half
    the solo walk cannot show: a lobby it does not own, a board without host controls, the roster from the outside."""
    hs = host.s
    finish_creator(client)
    hs.cmd("phstop", wait=1.0)
    client.s.cmd("phstop", wait=1.0)
    hs.cmd("phhost", wait=3.0)
    client.s.cmd("phjoin", wait=3.0)
    for ph in (host, client):
        open_app(ph)

    # The match needs the client in the host's Steam lobby. A client can reach the host's world and still be missing
    # from the lobby member list; the walk would then wait out its timeout on "need at least 2 players".
    players = 0
    end = time.time() + 30
    while time.time() < end:
        found = [int(m) for line in hs.cmd("phstate", wait=0.5) for m in re.findall(r"players=(\d+)", line)]
        players = found[-1] if found else 0
        if players >= 2:
            break
        time.sleep(2.0)
    if not host.r.check("coop: the host counts the client as a player", players >= 2, "players=%d" % players):
        return

    for ph in (host, client):
        ph.r.begin("%s lobby" % ph.label)
        ph.capture("lobby")

    for key, value in (("round", "Single"), ("hide", "20"), ("hunt", "25"), ("end", "12"), ("autostart", "1")):
        hs.cmd("phset %s %s" % (key, value), wait=0.3)
    hs.cmd("phstart", wait=2.0)

    wanted = {"betweenrounds", "hiding", "hunting", "roundover", "matchover"}
    seen = {"lobby"}
    end = time.time() + timeout
    while time.time() < end:
        tag = tag_of(host.state().get("phase", ""))
        if tag not in seen:
            seen.add(tag)
            for ph in (host, client):
                ph.r.begin("%s %s" % (ph.label, tag))
                ph.capture(tag, panes=["board", "roster"])
                if tag == "hiding":
                    ph.names_check()
            if tag == "betweenrounds":
                host.open_pane("board")
                host.press("#pane .act", "START NEXT ROUND")
        if wanted <= seen:
            break
        time.sleep(1.0)
    missing = wanted - seen
    host.r.check("coop: walked every phase to match over", not missing, sorted(missing))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", required=True)
    ap.add_argument("--label", default="solo")
    ap.add_argument("--only", action="store_true", help="capture the screen that is up now and stop")
    ap.add_argument("--timeout", type=int, default=150, help="seconds to wait for the round phases")
    ap.add_argument("--client", help="client slot of a joined co-op pair; --slot is then the host")
    args = ap.parse_args()

    r = Results()
    if args.client:
        walk_coop(Phone(Slot(args.slot, "host"), "host", r), Phone(Slot(args.client, "client"), "client", r),
                  args.timeout)
        sys.exit(r.summary())

    ph = Phone(Slot(args.slot, args.label), args.label, r)

    if args.only:
        r.begin("%s now" % args.label)
        ph.capture(tag_of(ph.state().get("phase", "")))
    else:
        walk_solo(ph, args.timeout)

    sys.exit(r.summary())


if __name__ == "__main__":
    main()
