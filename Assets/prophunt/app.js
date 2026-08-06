/* PropHunt - the depot board.
 *
 * Two rules shape the whole file.
 *
 * 1. COUNTDOWNS ARE DERIVED, NEVER SYNCED. The snapshot carries absolute host-time deadlines plus the host's own
 *    clock; the page notes how far that is from its own clock once and subtracts from then on. A counted-down
 *    number sent over the wire would be wrong by the transport delay and by however much two Windows machines
 *    disagree, which is routinely several seconds.
 *
 * 2. THE ONE-SECOND TICK MUST NOT REBUILD THE PAGE. Writing textContent marks the document dirty and the host
 *    rebuilds the whole thing at roughly half a millisecond per box - a hitch every second, worst exactly when a
 *    twenty-player roster is on screen and the player most needs a smooth frame. Only `transform`, `background*`,
 *    `border-color`, `border-radius` and `box-shadow` repaint without a rebuild. So nothing that moves once a
 *    second writes text: the clock slides reels of pre-rendered glyphs with `transform`, and every continuous
 *    countdown is a gauge of fixed cells recoloured by `background`. Numbers that change on an EVENT stay text,
 *    because an event rebuilds the page anyway.
 */

const $ = (id) => document.getElementById(id);

/** querySelectorAll hands back a host collection; copy it into a real array before iterating or spreading it. */
function all(selector) {
  const found = document.querySelectorAll(selector);
  const list = [];
  for (let i = 0; i < found.length; i++) list.push(found[i]);
  return list;
}

/* ------------------------------------------------------------------------------------ seven segment ---- */

/* The game ships a real seven-segment typeface - Sideload exposes it as `font-family: game-segment`
 * (Sideload/Paint/TextSupport.cs) - so the clock is set in it rather than drawn out of rectangles.
 *
 * The catch is that changing a digit means changing text, and any text write rebuilds every box on the page.
 * So nothing here writes text after construction: each digit is a reel of the eleven glyphs it can show, clipped
 * to one cell, and the digit changes by sliding the reel with `transform` - the one property that both repaints
 * without a rebuild AND carries its children with it. A tenth of a second of travel turns that into the roll a
 * split-flap or an odometer has, which is the motion this instrument has in life anyway.
 *
 * Behind each reel sits a static "8" in near-black: the segments a real display leaves faintly visible. It never
 * changes, so it costs nothing. */

const REEL = ['-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

/* The reel travels exactly one cell per glyph, so this MUST equal .cell's height in app.css - the digit box is
 * taller by its two 1px borders, and stepping by the box instead leaves a sliver of the next glyph showing.
 * The cell also has to clear the glyph's whole line box, or the digit is clipped top and bottom. */
const CELL_H = 60;

class SevenSegment {
  #reels = [];

  /** Builds `count` digit reels with a colon before the last two, once. Nothing here runs again. */
  constructor(host, count) {
    host.replaceChildren();

    for (let i = 0; i < count; i++) {
      if (i === count - 2) host.appendChild(this.#colon());
      host.appendChild(this.#digit());
    }
  }

  #digit() {
    const box = document.createElement('div');
    box.className = 'digit';

    // The unlit segments, as a real display shows them. Static, so it is drawn once and never touched again.
    const ghost = document.createElement('div');
    ghost.className = 'digit-ghost';
    const ghostFace = document.createElement('div');
    ghostFace.className = 'glyph';
    ghostFace.textContent = '8';
    ghost.appendChild(ghostFace);
    box.appendChild(ghost);

    const reel = document.createElement('div');
    reel.className = 'reel';
    for (const glyph of REEL) {
      // The glyph goes in a CHILD, never in the cell itself. A box carrying direct text IS the text leaf and
      // takes its height from what TMP measures for that character - so a reel of cells-with-text ends up with
      // eleven slightly different heights, and stepping by a fixed amount drifts a little further with every
      // digit. Only "0" looked right, because it is the first stop and had not accumulated any error yet.
      const cell = document.createElement('div');
      cell.className = 'cell';

      const face = document.createElement('div');
      face.className = 'glyph';
      face.textContent = glyph;

      cell.appendChild(face);
      reel.appendChild(cell);
    }

    box.appendChild(reel);
    this.#reels.push(reel);
    return box;
  }

  #colon() {
    const box = document.createElement('div');
    box.className = 'colon';
    const face = document.createElement('div');
    face.className = 'glyph';
    face.textContent = ':';
    box.appendChild(face);
    return box;
  }

  /** `text` is one character per digit. Only transforms move, so this never rebuilds the page. */
  show(text) {
    for (let i = 0; i < this.#reels.length; i++) {
      let at = REEL.indexOf(text[i]);
      if (at < 0) at = 0;                                  // anything unexpected shows a dash
      this.#reels[i].style.transform = 'translateY(-' + (at * CELL_H) + 'px)';
    }
  }
}

/* ------------------------------------------------------------------------------------------- gauge ---- */

/** A depleting bar drawn as fixed segments, so filling it is a repaint rather than a relayout. */
class Gauge {
  #cells = [];

  constructor(host, count) {
    host.replaceChildren();
    for (let i = 0; i < count; i++) {
      const cell = document.createElement('div');
      cell.className = 'gcell';
      host.appendChild(cell);
      this.#cells.push(cell);
    }
  }

  set(fraction, colour) {
    const lit = Math.round(Math.max(0, Math.min(1, fraction)) * this.#cells.length);
    for (let i = 0; i < this.#cells.length; i++) this.#cells[i].style.background = i < lit ? colour : '#20272A';
  }
}

/* -------------------------------------------------------------------------------------------- text ---- */

/* One word per concept, and the game's own word wherever it has one.
 *
 * A MATCH is made of ROUNDS; a round runs through PHASES. Players are HUNTERS and HIDERS; a hider who is found is
 * CAUGHT, never "eliminated". What a hider wears is a PROP, never a "disguise". Those are the words the mod's
 * README, its console and its keybind overlay already use, so the app does not teach a second vocabulary.
 *
 * An earlier pass had these read CREW / UNACCOUNTED / RECOVERED, which is how a depot would file them and is the
 * fiction this app is dressed in. It was wrong on the label that carries the most: mid-round, "am I hunting or am
 * I hiding" has to answer itself in a glance, and it may not be answered in a costume. The world lives in the
 * palette, the stamps and the gauges - never in a word the player has to translate. */

const PHASE_LABEL = {
  Lobby: 'LOBBY', Hiding: 'HIDING', Hunting: 'HUNTING',
  RoundEnd: 'ROUND OVER', Safehouse: 'BETWEEN ROUNDS', MatchEnd: 'MATCH OVER',
};

const ROLE_STAMP = {
  Hunter: 'HUNTER', Hider: 'HIDER', Caught: 'CAUGHT', Spectator: 'SPECTATING', Unassigned: 'NO ROLE YET',
};

const CATEGORY_SHORT = { 'Round': 'Round', 'Roles & Combat': 'Roles', 'Props': 'Props', 'World': 'World' };

function mmss(total) {
  const s = Math.max(0, Math.floor(total));
  const m = Math.floor(s / 60);
  if (m > 99) return '99:59';
  return String(m).padStart(2, '0') + String(s % 60).padStart(2, '0');
}

function plural(n, one, many) { return n + ' ' + (n === 1 ? one : many); }

/* An <img> is sized by CSS alone - the layout runs without Unity and cannot read a PNG's intrinsic size, and
 * the HTML width/height ATTRIBUTES are not CSS. Set them as style or the box is nothing and nothing paints. */
function icon(name, size) {
  const img = document.createElement('img');
  img.setAttribute('src', 'icons/' + name + '.png');
  img.style.width = size + 'px';
  img.style.height = size + 'px';
  return img;
}

function picture(key, size, className) {
  const img = document.createElement('img');
  img.setAttribute('src', 's1://' + key);
  img.style.width = size + 'px';
  img.style.height = size + 'px';
  if (className) img.className = className;
  return img;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
}

/* An icon needs the label in a sibling; without one the text goes straight on the button and the whole control
 * is a single box. A render rebuilds every box at well over a millisecond each, so on a pane full of chips and
 * rows that second box per button is real time - and it buys nothing. */
function button(className, label, iconName, onClick) {
  const b = el('div', className);

  if (iconName) {
    b.appendChild(icon(iconName, 15));
    b.appendChild(el('div', null, label));
  } else {
    b.textContent = label;
  }

  if (onClick) b.addEventListener('click', onClick);
  return b;
}

/* --------------------------------------------------------------------------------------------- app ---- */

class App {
  #snap = null;
  #takenAt = 0;        // local ms when #snap was read, so the clock can run on between snapshots
  #pane = s1.storage.get('pane', 'board');
  /* Opens on one category, not on all of them. Every row is about a dozen boxes and a render rebuilds each one,
   * so "All" was 378 boxes and 554ms of frozen frame every time the pane opened - measured, not guessed. The
   * category chips exist to make thirty-one rules navigable; making them the default is what they were for. */
  #category = s1.storage.get('rules.category', 'Round');
  #editing = null;     // key of the rule whose value is being typed
  #clock = null;
  #gauge = null;
  #whistle = null;
  #renderQueued = false;

  start() {
    this.#clock = new SevenSegment($('clock'), 4);
    this.#gauge = new Gauge($('gauge'), 28);

    for (const tab of all('.tab')) {
      tab.addEventListener('click', () => {
        this.#pane = tab.getAttribute('data-pane');
        this.#editing = null;
        s1.storage.set('pane', this.#pane);
        this.queueRender();
      });
    }

    document.addEventListener('back', (e) => {
      // The rail and the tab bar are always on screen, so there is nothing to step back FROM unless a value is
      // being typed. Taking the press otherwise would leave the player unable to close the app at all.
      if (!this.#editing) return;
      e.preventDefault();
      this.#editing = null;
      this.queueRender();
    });

    s1.on('ph.changed', () => this.pull());

    this.pull();

    // One second is all a seconds countdown needs, and this tick only ever writes background colours.
    setInterval(() => this.tick(), 1000);

    // The roster and the scoreboard carry values the host recomputes every second (how long someone has been
    // alive). Those two panes ask for a fresh snapshot on the same beat; the other two do not, because nothing
    // on them moves without an event.
    setInterval(() => {
      if (this.#pane === 'roster' || this.#pane === 'scores') this.pull();
    }, 1000);
  }

  pull() {
    const raw = s1.call('ph.snapshot');

    if (!raw) this.#snap = null;
    else {
      try { this.#snap = JSON.parse(raw); }
      catch (err) { console.error('bad snapshot: ' + err); this.#snap = null; }
    }

    this.#takenAt = Date.now();
    this.queueRender();
  }

  /**
   * Render at most once per burst.
   *
   * Anything the host does arrives twice: the command's own answer, and the state change the mod pushes a frame
   * or two later because that same command moved the state. Each render rebuilds every box on the page, so
   * applying a preset cost two full rebuilds back to back - which is exactly the stutter you feel when clicking
   * from one ruleset to the next. The snapshot itself is fetched every time and is cheap; only the drawing waits.
   *
   * The window is short enough not to read as lag on a tab switch and long enough to swallow the echo.
   */
  queueRender() {
    if (this.#renderQueued) return;

    this.#renderQueued = true;
    setTimeout(() => { this.#renderQueued = false; this.render(); }, 30);
  }

  /** Host time right now, from the snapshot's own stamp plus how long ago we read it. */
  now() {
    if (!this.#snap) return 0;
    return this.#snap.now + Math.floor((Date.now() - this.#takenAt) / 1000);
  }

  secondsLeft() {
    const s = this.#snap;
    if (!s || !s.ends) return -1;
    return Math.max(0, s.ends - this.now());
  }

  /* ---- the per-second beat: colours only ---- */

  tick() {
    const s = this.#snap;
    if (!s || !s.ok) { this.#clock.show('----'); return; }

    const left = this.secondsLeft();

    if (left < 0) {
      this.#clock.show('----');
      this.#gauge.set(0, '#20272A');
      this.#paintWhistle();
      return;
    }

    this.#clock.show(mmss(left));

    // The colour walk to amber and then to orange is a `color` write, which is inherited and so costs a rebuild -
    // but it crosses a threshold twice in a phase rather than once a second, so it is paid where it is cheap.
    const urgency = left <= 15 ? 'clock urgent' : left <= 45 ? 'clock warn' : 'clock';
    const clock = $('clock');
    if (clock.className !== urgency) clock.className = urgency;

    const span = s.phaseLen > 0 ? s.phaseLen : Math.max(1, left);
    this.#gauge.set(left / span, left <= 15 ? '#E4572E' : left <= 45 ? '#E8B22A' : '#08A6A6');

    this.#paintWhistle();
  }

  #paintWhistle() {
    if (!this.#whistle) return;

    const s = this.#snap;
    const interval = Number(this.#settingValue('taunt') || 0);
    const due = s && s.whistle >= 0 ? s.whistle : -1;

    if (due < 0 || interval <= 0) { this.#whistle.set(0, '#20272A'); return; }

    // Fills up TOWARDS the whistle rather than draining away from it: the useful question is how close the
    // next forced reveal is, not how much quiet is left.
    this.#whistle.set(1 - due / interval, due <= 5 ? '#E4572E' : '#E8B22A');
  }

  #settingValue(key) {
    const s = this.#snap;
    if (!s || !s.settings) return null;
    for (const row of s.settings) if (row.key === key) return row.value;
    return null;
  }

  /* ---- render ---- */

  render() {
    const s = this.#snap;

    this.#renderRail(s);

    const pane = $('pane');
    pane.replaceChildren();

    if (!s || !s.ok) { this.#renderNoSession(pane); this.tick(); return; }

    if (this.#pane === 'board') this.#renderBoard(pane, s);
    else if (this.#pane === 'roster') this.#renderRoster(pane, s);
    else if (this.#pane === 'rules') this.#renderRules(pane, s);
    else this.#renderScores(pane, s);

    for (const tab of all('.tab'))
      tab.className = tab.getAttribute('data-pane') === this.#pane ? 'tab on' : 'tab';

    this.tick();
  }

  #renderNoSession(pane) {
    $('phase').textContent = 'NO MATCH';
    $('role').textContent = 'NO ROLE YET';
    $('role').className = 'stamp role';
    $('tallycount').textContent = '0 / 0';
    $('tally').replaceChildren();
    $('vitals').replaceChildren();
    $('clocknote').textContent = '';

    const box = el('div', 'empty');
    box.appendChild(el('div', 'empty-title', 'No match running.'));
    box.appendChild(el('div', 'empty-note', 'Start one from the main menu: Side Hustle, then PropHunt.'));
    pane.appendChild(box);
  }

  #renderRail(s) {
    if (!s || !s.ok) return;

    $('phase').textContent = PHASE_LABEL[s.phase] || s.phase.toUpperCase();

    const me = s.me;
    const stamp = $('role');
    const caught = me.eliminated && me.role === 'Hider';
    stamp.textContent = ROLE_STAMP[caught ? 'Caught' : me.role] || 'WAITING';
    stamp.className = 'stamp role ' + (caught ? 'caught' : me.role.toLowerCase());

    // The tally is the headcount: one block per hider who started, filled while they are still out there.
    const total = s.players.filter((p) => p.role === 'Hider').length;
    $('tallycount').textContent = s.hidersAlive + ' / ' + total;

    const tally = $('tally');
    tally.replaceChildren();
    for (let i = 0; i < total; i++) tally.appendChild(el('div', i < s.hidersAlive ? 'mark' : 'mark out'));

    // A phase without a deadline has no clock, and a seven-segment frame showing two dashes reads as a broken
    // instrument rather than as "no timer". Hide the slab and let the line under it carry the phase instead.
    const running = s.ends > 0;
    $('clockbox').className = running ? 'clockbox' : 'clockbox blank';
    $('clocknote').textContent = this.#clockNote(s);

    this.#renderVitals(s);
  }

  /** The line under the clock. In a phase with no deadline it stands in for the clock entirely, so it has to
   *  say something on its own rather than annotate a number that is not there. */
  #clockNote(s) {
    if (s.phase === 'Lobby') return plural(s.lobby, 'player in the lobby', 'players in the lobby');
    if (s.phase === 'Hiding') return 'Hunters are released when this runs out.';
    if (s.phase === 'Hunting') return 'Round ' + s.round;
    if (s.phase === 'RoundEnd' || s.phase === 'Safehouse')
      return s.nextRound >= 0 ? 'Next round in ' + s.nextRound + 's' : 'Waiting for the host.';
    if (s.phase === 'MatchEnd') return 'Match over.';
    return '';
  }

  #renderVitals(s) {
    const box = $('vitals');
    box.replaceChildren();

    const me = s.me;
    const running = s.phase === 'Hiding' || s.phase === 'Hunting';

    if (me.downed) box.appendChild(this.#vital('downed', 'Knocked down', 'up in ' + me.downedLeft + 's', true));
    if (me.outside) box.appendChild(this.#vital('oob', me.water ? 'In deep water' : 'Outside the area', 'back in ' + me.grace + 's', true));

    if (!running) {
      // The whistle gauge belongs to the hunt. Build it anyway so the tick never has to test for it.
      this.#whistle = null;
      if (box.children.length === 0)
        box.appendChild(el('div', 'note', 'Your prop, your hits and your gear show up here once the round starts.'));
      return;
    }

    if (me.role === 'Hider' && !me.eliminated) {
      // "Hits left" for both roles: for a hider it is the prop taking damage, for a hunter it is friendly fire
      // before they go down. Same question from the player's side, so the same words.
      box.appendChild(this.#vital('hp', 'Hits left', Math.max(0, me.maxHp - me.hp) + ' / ' + me.maxHp));
      box.appendChild(this.#vital('change', 'Prop changes left',
        me.freeChanges ? 'free now' : me.maxChanges > 0 ? String(Math.max(0, me.maxChanges - me.changes)) : 'unlimited'));
      if (me.maxDecoys > 0) box.appendChild(this.#vital('decoy', 'Decoys left', String(Math.max(0, me.maxDecoys - me.decoys))));
      if (me.maxConc > 0) box.appendChild(this.#vital('concussion', 'Concussions left', String(Math.max(0, me.maxConc - me.conc))));
    } else if (me.role === 'Hunter') {
      box.appendChild(this.#vital('hp', 'Hits left', Math.max(0, me.hunterMaxHp - me.hunterHp) + ' / ' + me.hunterMaxHp));
    }

    if (s.phase === 'Hunting' && s.whistle >= 0) {
      const row = el('div', 'vital');
      row.appendChild(icon('whistle', 14));
      row.appendChild(el('div', 'vital-label', 'Next whistle'));
      box.appendChild(row);

      const bar = el('div', 'gauge');
      box.appendChild(bar);
      this.#whistle = new Gauge(bar, 28);
    } else {
      this.#whistle = null;
    }
  }

  #vital(iconName, label, value, alert) {
    const row = el('div', alert ? 'vital alert' : 'vital');
    row.appendChild(icon(iconName, 14));
    row.appendChild(el('div', 'vital-label', label));
    row.appendChild(el('div', 'vital-value', value));
    return row;
  }

  /* ---- board ---- */

  #renderBoard(pane, s) {
    if (s.phase === 'Lobby') return this.#boardLobby(pane, s);
    if (s.phase === 'Hiding') return this.#boardHiding(pane, s);
    if (s.phase === 'Hunting') return this.#boardHunting(pane, s);
    if (s.phase === 'RoundEnd' || s.phase === 'MatchEnd') return this.#boardResult(pane, s);
    if (s.phase === 'Safehouse') return this.#boardSafehouse(pane, s);
  }

  #boardLobby(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', 'Waiting to start'));
    head.appendChild(el('div', 'sub', plural(s.lobby, 'player here', 'players here')));
    pane.appendChild(head);

    if (s.host) {
      // The button keeps its name whether or not it can be pressed, and the reason it cannot sits under it. A
      // button relabelled with its own blocker stops saying what it does and reads as a different control.
      const ready = s.lobby >= 2;
      pane.appendChild(button(ready ? 'act' : 'act off', 'START MATCH', 'start',
        ready ? () => this.#send('ph.begin') : null));
      pane.appendChild(el('div', 'note', ready
        ? 'Set the rules first if you want to - they apply from the first round.'
        : 'PropHunt needs two players. Invite someone through the Steam overlay.'));
    } else {
      pane.appendChild(el('div', 'note', 'Waiting for the host to start.'));
    }

    pane.appendChild(el('div', 'rule'));
    this.#dressingRoom(pane, s);
  }

  /** The lobby dressing room. This is what used to be a whole tab that showed one sentence outside the lobby. */
  #dressingRoom(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', 'Try a prop'));
    pane.appendChild(head);

    if (s.becomable <= 0) {
      pane.appendChild(el('div', 'note', 'No props found here yet. Walk around a little and come back.'));
      return;
    }

    const wearing = s.me.prop >= 0;

    // The name and the buttons sit CENTRED against the picture rather than stacked under a paragraph. With the
    // hint still in this column the block was taller than the 96px tile and the button hung below it, which is
    // the kind of near-miss alignment that is more annoying than a wrong layout. The hint moved out to full
    // width underneath, where a line of help belongs anyway.
    const dress = el('div', 'dress');
    const shot = el('div', 'dress-shot');

    if (wearing && s.me.propImage) {
      shot.appendChild(picture(s.me.propImage, 88));
    } else {
      shot.appendChild(icon('prop', 34));
      shot.appendChild(el('div', 'dress-empty', wearing ? 'LOADING' : 'NO PROP'));
    }

    dress.appendChild(shot);

    const main = el('div', 'dress-main inline');
    main.appendChild(el('div', 'dress-name', wearing ? s.me.propName : plural(s.becomable, 'prop nearby', 'props nearby')));

    const pair = el('div', 'pair');
    pair.appendChild(button('btn', wearing ? 'Try another' : 'Try a random prop', 'random', () => this.#send('ph.prop.roll')));
    if (wearing) pair.appendChild(button('btn', 'Take it off', 'takeoff', () => this.#send('ph.prop.clear')));
    main.appendChild(pair);

    dress.appendChild(main);
    pane.appendChild(dress);
    pane.appendChild(el('div', 'note', 'Or press [2] out in the world.'));
  }

  #boardHiding(pane, s) {
    const hunter = s.me.role === 'Hunter';

    const head = el('div', 'head');
    head.appendChild(el('div', 'title', hunter ? 'Hunters are blind' : 'Find a hiding spot'));
    head.appendChild(el('div', 'sub', 'Round ' + s.round));
    pane.appendChild(head);

    if (hunter) {
      pane.appendChild(el('div', 'note', 'You get your weapon and your sight back when the clock runs out.'));
    } else if (s.me.prop >= 0) {
      this.#wearing(pane, s);
      pane.appendChild(el('div', 'note', 'Hold [F] and move the mouse to sit the way a real one would.'));
    } else {
      pane.appendChild(el('div', 'note', 'Look at any prop and press [E] to become it. [2] picks one at random.'));
    }

    this.#hostRoundControls(pane, s);
  }

  #boardHunting(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', s.hidersAlive === 1 ? 'One hider left' : s.hidersAlive + ' hiders left'));
    head.appendChild(el('div', 'sub', 'Round ' + s.round));
    pane.appendChild(head);

    if (s.me.role === 'Hider' && !s.me.eliminated && s.me.prop >= 0) this.#wearing(pane, s);
    else if (s.me.role === 'Hunter') pane.appendChild(el('div', 'note', 'Shoot a prop to catch it. Bigger props take more hits.'));
    else pane.appendChild(el('div', 'note', 'You are out for this round. Press [4] to switch between follow-cam and freecam.'));

    this.#hostRoundControls(pane, s);
  }

  /** The prop you are, as a picture. Falls back to its name alone, which is what the old app only ever had. */
  #wearing(pane, s) {
    const dress = el('div', 'dress');
    const shot = el('div', 'dress-shot');

    if (s.me.propImage) shot.appendChild(picture(s.me.propImage, 88));
    else shot.appendChild(icon('prop', 34));

    dress.appendChild(shot);

    const main = el('div', 'dress-main');
    main.appendChild(el('div', 'dress-name', s.me.propName || 'A prop'));
    main.appendChild(el('div', 'note', plural(s.me.maxHp, 'hit', 'hits') + ' before you are caught'));
    if (s.me.locked) main.appendChild(el('div', 'note', 'Facing locked - [F] to turn'));
    dress.appendChild(main);

    pane.appendChild(dress);
  }

  #boardResult(pane, s) {
    const result = el('div', 'result');
    const word = s.winner === 0 ? 'HUNTERS WIN' : s.winner === 1 ? 'HIDERS WIN' : 'ROUND OVER';
    const cls = s.winner === 0 ? 'verdict hunters' : s.winner === 1 ? 'verdict hiders' : 'verdict';
    result.appendChild(el('div', cls, word));
    if (s.phase === 'RoundEnd' && s.nextRound >= 0) result.appendChild(el('div', 'sub', 'Next round in ' + s.nextRound + 's'));
    pane.appendChild(result);

    if (s.awards.length > 0) {
      pane.appendChild(el('div', 'rule'));
      for (const a of s.awards) {
        const row = el('div', 'award');
        row.appendChild(el('div', 'award-label', a.label.toUpperCase()));
        row.appendChild(el('div', 'award-name', a.name));
        row.appendChild(el('div', 'award-value', a.value));
        pane.appendChild(row);
      }
    }

    const top = [...s.players].sort((a, b) => b.score - a.score).slice(0, 3);
    if (top.length > 0) {
      pane.appendChild(el('div', 'rule'));
      for (let i = 0; i < top.length; i++) {
        const row = el('div', top[i].self ? 'row me' : 'row');
        row.appendChild(el('div', 'row-num', String(i + 1)));
        row.appendChild(this.#face(top[i]));

        const main = el('div', 'row-main');
        main.appendChild(el('div', 'row-name', top[i].name));
        row.appendChild(main);

        row.appendChild(el('div', 'row-num', String(top[i].score)));
        pane.appendChild(row);
      }
    }

    if (s.phase === 'MatchEnd' && s.host) {
      pane.appendChild(this.#tape());
      pane.appendChild(button('danger', 'RETURN TO HUB', 'hub', () => this.#send('ph.hub')));
      pane.appendChild(el('div', 'note', 'Closes the match and sends everyone back to the Side Hustle menu.'));
    }
  }

  #boardSafehouse(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', 'Round ' + (s.round + 1) + ' next'));
    head.appendChild(el('div', 'sub', 'Starting from ' + s.safehouse.name));
    pane.appendChild(head);

    if (s.safehouse.ready) pane.appendChild(el('div', 'note', 'Doors are opening - get ready.'));

    if (!s.host) {
      pane.appendChild(el('div', 'note', 'The host is picking where everyone starts.'));
      return;
    }

    const pair = el('div', 'pair');
    pair.appendChild(button('btn', 'Previous start', 'prev', () => this.#send('ph.map', '-1')));
    pair.appendChild(button('btn', 'Next start', 'forward', () => this.#send('ph.map', '1')));
    pane.appendChild(pair);

    if (s.safehouse.options > 1)
      pane.appendChild(el('div', 'note', s.safehouse.options + ' places are big enough for this many players.'));

    pane.appendChild(button('act', 'START NEXT ROUND', 'next-round', () => this.#send('ph.next')));
    this.#autoStart(pane, s);
  }

  #hostRoundControls(pane, s) {
    if (!s.host) return;

    pane.appendChild(el('div', 'rule'));
    this.#autoStart(pane, s);
    pane.appendChild(this.#tape());
    pane.appendChild(button('danger', 'END ROUND NOW', 'end-round', () => this.#send('ph.endround')));
    pane.appendChild(el('div', 'note', 'Cuts the round short for everyone and goes straight to the scoreboard.'));
  }

  #autoStart(pane, s) {
    const on = this.#settingValue('autostart') === '1';
    pane.appendChild(button(on ? 'btn wide on' : 'btn wide',
      on ? 'Auto-start next round: ON' : 'Auto-start next round: OFF', 'autostart',
      () => this.#send('ph.set', 'autostart\n' + (on ? '0' : '1'))));
  }

  /** Hazard band. Only ever directly above something the host cannot take back. */
  #tape() {
    const band = el('div', 'tape');
    for (let i = 0; i < 26; i++) band.appendChild(el('div', i % 2 ? 'tape-seg gap' : 'tape-seg'));
    return band;
  }

  /* ---- roster ---- */

  #renderRoster(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', 'Players'));
    head.appendChild(el('div', 'sub', plural(s.players.length, 'in the match', 'in the match')));
    pane.appendChild(head);

    if (s.players.length === 0) {
      pane.appendChild(el('div', 'note', 'Players show up here as they join.'));
      return;
    }

    for (const p of s.players) {
      const caught = p.eliminated && p.role === 'Hider';
      let cls = 'row';
      if (p.self) cls = 'row me';
      else if (p.role === 'Hunter') cls = 'row hunter';
      else if (p.role === 'Hider' && !p.eliminated) cls = 'row hider';

      const row = el('div', cls);
      row.appendChild(this.#face(p));

      const main = el('div', 'row-main');
      main.appendChild(el('div', 'row-name', p.name + (p.self ? '  (you)' : '')));
      main.appendChild(el('div', 'row-note', this.#rosterNote(p, s, caught)));
      row.appendChild(main);

      // A caught hider's prop is no longer a secret worth keeping, and seeing what fooled you is the point.
      if (p.propImage) row.appendChild(picture(p.propImage, 34, 'row-thumb'));

      row.appendChild(el('div', 'stamp', ROLE_STAMP[caught ? 'Caught' : p.role] || '?'));

      if (s.host && !p.self && p.id !== '0')
        row.appendChild(button('btn', 'Kick', 'kick', () => this.#send('ph.kick', p.id)));

      pane.appendChild(row);
    }
  }

  /** Never says where a living hider is or what they are wearing - that is not hidden by the page, it is absent
   *  from the snapshot. What this line can say is only ever how far along they are. */
  #rosterNote(p, s, caught) {
    if (p.role === 'Hunter') return plural(p.catches, 'catch', 'catches') + ' this session';
    if (caught) return p.propName ? 'Caught as a ' + p.propName.toLowerCase() : 'Caught';
    if (p.self && p.prop >= 0) return p.propName + '  -  ' + Math.max(0, p.maxHp - p.hp) + ' of ' + p.maxHp + ' hits left';
    if (p.role === 'Hider') return s.phase === 'Hunting' || s.phase === 'Hiding' ? 'Still hiding' : 'Hiding next round';
    return 'Spectating';
  }

  #face(p) {
    if (p.face) return picture(p.face, 30, 'row-face');

    const box = el('div', 'row-face');
    box.appendChild(el('div', 'row-initial', (p.name || '?').trim().charAt(0).toUpperCase() || '?'));
    return box;
  }

  /* ---- rules ---- */

  #renderRules(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', 'Rules'));
    head.appendChild(el('div', 'sub', s.host ? 'Changes apply from the next round' : 'Only the host can change these'));
    pane.appendChild(head);

    if (s.host && s.presets.length > 0) {
      // The mod does not record which preset was applied - it only stores values - so "active" means every value
      // this preset names still holds. Tweak one and the highlight goes out, which is the truth: the rules are no
      // longer that preset.
      const chips = el('div', 'chips');
      for (const name of s.presets) {
        const on = name === s.activePreset;
        chips.appendChild(button(on ? 'chip on' : 'chip', name, null, () => this.#send('ph.preset', name)));
      }
      pane.appendChild(chips);
      // Say what the dots measure against, because after one tweak nothing is highlighted any more and a mark
      // with an unnamed baseline is just a dot.
      pane.appendChild(el('div', 'note',
        s.baselinePreset
          ? (s.activePreset
              ? 'Rules match ' + s.activePreset + '. A dot marks anything changed away from it.'
              : 'Changed away from ' + s.baselinePreset + '. A dot marks each one.')
          : 'Pick a preset to start from, or tune the rules one by one.'));
      pane.appendChild(el('div', 'rule'));
    }

    const cats = [];
    for (const row of s.settings) if (!cats.includes(row.cat)) cats.push(row.cat);

    // A remembered category that no longer exists would show an empty pane with no way to tell why.
    if (this.#category !== 'all' && !cats.includes(this.#category)) this.#category = cats[0] || 'all';

    const filter = el('div', 'chips');
    filter.appendChild(button(this.#category === 'all' ? 'chip on' : 'chip', 'All', null,
      () => this.#pickCategory('all')));
    for (const c of cats)
      filter.appendChild(button(this.#category === c ? 'chip on' : 'chip', CATEGORY_SHORT[c] || c, null,
        () => this.#pickCategory(c)));
    pane.appendChild(filter);

    for (const row of s.settings) {
      if (this.#category !== 'all' && row.cat !== this.#category) continue;
      pane.appendChild(this.#settingRow(row, s.host));
    }
  }

  #pickCategory(name) {
    this.#category = name;
    s1.storage.set('rules.category', name);
    this.queueRender();
  }

  #settingRow(row, host) {
    const line = el('div', 'setting');

    const main = el('div', 'setting-main');
    const label = el('div', 'setting-label');
    label.appendChild(el('div', 'setting-name', row.label));
    if (row.value !== row.def) label.appendChild(el('div', 'moved'));
    main.appendChild(label);
    if (row.hint) main.appendChild(el('div', 'setting-hint', row.hint));
    line.appendChild(main);

    line.appendChild(host ? this.#control(row) : el('div', 'readonly', this.#display(row)));
    return line;
  }

  #display(row) {
    if (row.type === 'toggle') return row.value === '1' ? 'ON' : 'OFF';
    if (row.options) {
      const at = row.values.indexOf(row.value);
      if (at >= 0) return row.options[at];
    }
    return row.unit ? row.value + ' ' + row.unit : row.value;
  }

  #control(row) {
    if (row.type === 'toggle') {
      const on = row.value === '1';
      return button(on ? 'btn on' : 'btn', on ? 'ON' : 'OFF', on ? 'check' : 'close',
        () => this.#send('ph.set', row.key + '\n' + (on ? '0' : '1')));
    }

    if (row.type === 'segmented') {
      const seg = el('div', 'seg');
      for (let i = 0; i < row.options.length; i++) {
        const value = row.values[i];
        const opt = el('div', value === row.value ? 'seg-opt on' : 'seg-opt', row.options[i]);
        opt.addEventListener('click', () => this.#send('ph.set', row.key + '\n' + value));
        seg.appendChild(opt);
      }
      return seg;
    }

    if (row.type === 'choice') {
      const at = Math.max(0, row.values.indexOf(row.value));
      const box = el('div', 'stepper');
      box.appendChild(this.#stepButton('prev', () =>
        this.#send('ph.set', row.key + '\n' + row.values[(at - 1 + row.values.length) % row.values.length])));
      box.appendChild(el('div', 'step-value', row.options[at] || row.value));
      box.appendChild(this.#stepButton('forward', () =>
        this.#send('ph.set', row.key + '\n' + row.values[(at + 1) % row.values.length])));
      return box;
    }

    // A number. There is no slider in this renderer and a drag would be the wrong control on a phone anyway;
    // the descriptors already carry sensible coarse steps, and tapping the value types an exact one.
    const box = el('div', 'stepper');
    const value = Number(row.value) || 0;
    const step = Number(row.step) || 1;

    box.appendChild(this.#stepButton('minus', () => this.#setNumber(row, value - step)));

    if (this.#editing === row.key) {
      const field = document.createElement('input');
      field.className = 'step-input';
      field.setAttribute('value', row.value);
      field.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        this.#editing = null;
        this.#setNumber(row, Number(e.value));
      });
      box.appendChild(field);
    } else {
      const shown = el('div', 'step-value', row.unit ? row.value + ' ' + row.unit : row.value);
      shown.addEventListener('click', () => { this.#editing = row.key; this.queueRender(); });
      box.appendChild(shown);
    }

    box.appendChild(this.#stepButton('plus', () => this.#setNumber(row, value + step)));
    return box;
  }

  #stepButton(iconName, onClick) {
    const b = el('div', 'step');
    b.appendChild(icon(iconName, 14));
    b.addEventListener('click', onClick);
    return b;
  }

  #setNumber(row, raw) {
    let v = Number(raw);
    if (!isFinite(v)) return;

    v = Math.max(Number(row.min), Math.min(Number(row.max), v));
    const text = row.whole ? String(Math.round(v)) : String(Math.round(v * 100) / 100);
    this.#send('ph.set', row.key + '\n' + text);
  }

  /* ---- scores ---- */

  #renderScores(pane, s) {
    const head = el('div', 'head');
    head.appendChild(el('div', 'title', 'Scores'));
    head.appendChild(el('div', 'sub', 'Whole session, all rounds'));
    pane.appendChild(head);

    if (s.players.length === 0) {
      pane.appendChild(el('div', 'note', 'Scores appear after the first round.'));
      return;
    }

    if (s.awards.length > 0) {
      for (const a of s.awards) {
        const row = el('div', 'award');
        row.appendChild(el('div', 'award-label', a.label.toUpperCase()));
        row.appendChild(el('div', 'award-name', a.name));
        row.appendChild(el('div', 'award-value', a.value));
        pane.appendChild(row);
      }
      pane.appendChild(el('div', 'rule'));
    }

    const header = el('div', 'thead');
    header.appendChild(el('div', 'th left', 'PLAYER'));
    for (const [label, width] of [['CAUGHT', 54], ['HITS', 42], ['BAITS', 48], ['STUNS', 48], ['ALIVE', 50], ['SCORE', 50]]) {
      const th = el('div', 'th', label);
      th.style.width = width + 'px';
      header.appendChild(th);
    }
    pane.appendChild(header);

    // Two of these columns cannot be guessed from a five-letter heading, so they are spelled out once.
    pane.appendChild(el('div', 'legend',
      'Baits: hunters who shot one of your decoys.   Stuns: hunters your concussion knocked down.'));

    const ranked = [...s.players].sort((a, b) => b.score - a.score);

    for (const p of ranked) {
      const row = el('div', p.self ? 'row me' : 'row');

      const main = el('div', 'row-main');
      main.appendChild(el('div', 'row-name', p.name));
      row.appendChild(main);

      for (const [value, width] of [
        [p.catches, 54], [p.hits, 42], [p.baits, 48], [p.stuns, 48],
        [p.survived + 's', 50], [p.score, 50],
      ]) {
        const cell = el('div', 'row-num', String(value));
        cell.style.width = width + 'px';
        row.appendChild(cell);
      }

      pane.appendChild(row);
    }
  }

  /* ---- commands ---- */

  #send(name, arg) {
    const answer = s1.call(name, arg === undefined ? '' : arg);
    if (answer !== 'ok') console.error(name + ' was refused by the mod (answered "' + answer + '")');
    this.pull();
  }
}

new App().start();
