// The stand-in data and handlers for PropHunt's browser preview. Authoring only - never shipped (see the Exclude
// in PropHunt.csproj).
//
// Everything around it - the stage, the fenced DOM, the storage, the back and orientation events - is shared and
// lives in sideload-preview.js. Only the parts that are PropHunt are here: a table of sessions, and the switch that
// answers ph.snapshot with whichever one the strip above the phone has selected. That strip is the only way to look
// at RoundEnd or a twenty-player roster without four people and a lobby.

const NAMES = [
  'DooDesch', 'fadestyle', 'DonyThePony', 'xAkitoh', 'godofn00bs', 'Marcy', 'Ming',
  'a_very_long_steam_name_indeed', 'Pauli', 'Kev', 'Sam', 'Toni', 'Rue', 'Nix', 'Bo', 'Jules',
  'Ash', 'Wren', 'Kit', 'Ozzy',
];

const PROPS = ['Wheeled Bin', 'Traffic Cone', 'Wooden Pallet', 'Cardboard Box', 'Fire Hydrant', 'Trash Bag'];

function player(i, role, opts = {}) {
  return {
    id: String(76561190000000000 + i),
    name: NAMES[i % NAMES.length],
    role,
    eliminated: !!opts.eliminated,
    self: !!opts.self,
    // Every third mocked player, so the roster preview shows the badge next to names that also have roles,
    // thumbnails and kick buttons - which is where it has to hold up.
    friend: !opts.self && i % 3 === 1,
    catches: opts.catches ?? 0,
    survived: opts.survived ?? 0,
    score: opts.score ?? 0,
    hits: opts.hits ?? 0,
    baits: opts.baits ?? 0,
    stuns: opts.stuns ?? 0,
    smashed: 0,
    taunts: opts.taunts ?? 0,
    ...(opts.prop !== undefined ? { prop: opts.prop, propName: PROPS[opts.prop % PROPS.length] } : {}),
    ...(opts.self ? { hp: opts.hp ?? 1, maxHp: opts.maxHp ?? 3 } : {}),
  };
}

const SETTINGS = [
  ['Round', 'round', 'Round mode', 'segmented', 'Continuous', ['Continuous', 'Single']],
  ['Round', 'swap', 'Rounds before swap', 'number', '1', null, 1, 10, 1, ''],
  ['Round', 'hide', 'Hiding time', 'number', '30', null, 5, 120, 5, 's'],
  ['Round', 'hunt', 'Hunting time', 'number', '300', null, 30, 900, 15, 's'],
  ['Round', 'end', 'Results screen', 'number', '15', null, 5, 60, 1, 's'],
  ['Round', 'taunt', 'Taunt interval', 'number', '30', null, 0, 120, 5, 's'],
  ['Round', 'caught', 'On catch', 'segmented', 'Spectator', ['Spectator', 'Infection']],
  ['Roles & Combat', 'pph', 'Hunter ratio', 'number', '5', null, 2, 10, 1, ''],
  ['Roles & Combat', 'hiderspeed', 'Hider speed', 'number', '90', null, 70, 100, 5, '%'],
  ['Roles & Combat', 'weapon', 'Hunter weapon', 'choice', 'pumpshotgun',
    ['None', 'Pump Shotgun', 'M1911', 'Revolver'], 0, 0, 1, '', ['', 'pumpshotgun', 'm1911', 'revolver']],
  ['Roles & Combat', 'ff', 'Friendly fire', 'toggle', '1'],
  ['Roles & Combat', 'hhp', 'Friendly hits to down', 'number', '3', null, 1, 10, 1, ''],
  ['Props', 'hits', 'Prop toughness', 'number', '2', null, 1, 10, 1, ''],
  ['Props', 'chg', 'Max prop changes', 'number', '5', null, 0, 20, 1, ''],
  ['Props', 'decoy', 'Decoys per prop', 'number', '4', null, 0, 10, 1, ''],
  ['Props', 'conc', 'Concussions per prop', 'number', '1', null, 0, 10, 1, ''],
  ['Props', 'rnd', 'Random prop [2]', 'toggle', '1'],
  ['World', 'area', 'Play-area radius', 'number', '75', null, 50, 200, 5, 'm'],
  ['World', 'time', 'Time of day', 'number', '1200', null, 0, 2300, 100, ''],
  ['World', 'autostart', 'Auto-start next round', 'toggle', '1'],
  ['World', 'goblin', 'Allow the sewer goblin', 'toggle', '1'],
];

const HINTS = {
  hide: 'Seconds hiders get before hunters are released.',
  hunt: 'Seconds hunters have to find every hider.',
  taunt: 'How often hiders are forced to make a sound (0 = off).',
  chg: 'Re-picks per round; each resets HP (0 = unlimited).',
  area: 'Radius of the play area around the safehouse.',
};

function settings(overrides = {}) {
  return SETTINGS.map(([cat, key, label, type, def, options, min, max, step, unit, values]) => ({
    key, label, cat, type,
    hint: HINTS[key] || '',
    unit: unit || '',
    value: overrides[key] ?? def,
    def,
    min: String(min ?? 0),
    max: String(max ?? 0),
    step: String(step ?? 1),
    whole: true,
    ...(options ? { options, values: values || options } : {}),
  }));
}

const AWARDS = [
  { label: 'Top Hunter', name: 'fadestyle', value: '4 catches' },
  { label: 'Survivor', name: 'DooDesch', value: '271 s alive' },
  { label: 'Trickster', name: 'Ming', value: '3 decoy baits' },
];

function base(over) {
  const now = Math.floor(Date.now() / 1000);
  return {
    ok: true, host: true, phase: 'Lobby', round: 0, winner: -1,
    now, ends: 0, phaseLen: 0, nextRound: -1, whistle: -1, rotation: -1,
    hidersAlive: 0, lobby: 6, becomable: 214,
    me: {
      id: '76561190000000000', role: 'Unassigned', eliminated: false, spectating: false,
      hp: 0, maxHp: 3, hunterHp: 0, hunterMaxHp: 3,
      prop: -1, propName: '', locked: false,
      changes: 0, maxChanges: 5, freeChanges: false,
      decoys: 0, maxDecoys: 4, conc: 0, maxConc: 1,
      downed: false, downedLeft: 0, outside: false, water: false, grace: 0,
    },
    players: [], settings: settings(), presets: ['Classic Hunt', 'Infection', 'Panic Room', 'Deep Cover'],
    activePreset: 'Classic Hunt',
    baselinePreset: 'Classic Hunt',
    safehouse: { name: 'Motel Room 2', code: 'motel2', options: 3, ready: false },
    awards: [],
    ...over,
  };
}

const SCENARIOS = {
  'lobby (host)': () => base({ lobby: 6 }),

  'lobby (client, 1 player)': () => base({ host: false, lobby: 1, presets: [] }),

  'lobby wearing a prop': () => {
    const s = base({ lobby: 6 });
    s.me.prop = 0;
    s.me.propName = 'Wheeled Bin';
    return s;
  },

  'hiding (you are a bin)': () => {
    const s = base({
      phase: 'Hiding', round: 1, ends: Math.floor(Date.now() / 1000) + 22, phaseLen: 30,
      hidersAlive: 5, lobby: 6,
    });
    s.me = { ...s.me, role: 'Hider', prop: 0, propName: 'Wheeled Bin', maxHp: 3, hp: 0, changes: 1 };
    s.players = [
      player(1, 'Hunter'),
      player(0, 'Hider', { self: true, prop: 0, hp: 0, maxHp: 3 }),
      player(2, 'Hider'), player(3, 'Hider'), player(4, 'Hider'), player(5, 'Hider'),
    ];
    return s;
  },

  'hunting (hider, hurt)': () => {
    const s = base({
      phase: 'Hunting', round: 1, ends: Math.floor(Date.now() / 1000) + 143, phaseLen: 300,
      hidersAlive: 3, lobby: 6, whistle: 12,
    });
    s.me = { ...s.me, role: 'Hider', prop: 0, propName: 'Wheeled Bin', maxHp: 3, hp: 2, changes: 3, decoys: 3, conc: 1 };
    s.players = [
      player(1, 'Hunter', { catches: 2, hits: 9 }),
      player(0, 'Hider', { self: true, prop: 0, hp: 2, maxHp: 3, survived: 143 }),
      player(2, 'Hider', { survived: 143 }), player(3, 'Hider', { survived: 143 }),
      player(4, 'Hider', { eliminated: true, prop: 2, survived: 61 }),
      player(5, 'Hider', { eliminated: true, prop: 4, survived: 88 }),
    ];
    return s;
  },

  'hunting (hunter, 8s left)': () => {
    const s = base({
      phase: 'Hunting', round: 2, ends: Math.floor(Date.now() / 1000) + 8, phaseLen: 300,
      hidersAlive: 1, lobby: 6, whistle: 3,
    });
    s.me = { ...s.me, role: 'Hunter', hunterHp: 1, hunterMaxHp: 3 };
    s.players = [
      player(0, 'Hunter', { self: true, catches: 3, hits: 14 }),
      player(1, 'Hider', { survived: 292 }),
      player(2, 'Hider', { eliminated: true, prop: 1, survived: 40 }),
      player(3, 'Hider', { eliminated: true, prop: 3, survived: 120 }),
      player(4, 'Hider', { eliminated: true, prop: 5, survived: 201 }),
    ];
    return s;
  },

  'caught + out of bounds': () => {
    const s = base({
      phase: 'Hunting', round: 2, ends: Math.floor(Date.now() / 1000) + 95, phaseLen: 300,
      hidersAlive: 2, lobby: 6, host: false, presets: [],
    });
    s.me = {
      ...s.me, role: 'Hider', eliminated: true, prop: 3, propName: 'Cardboard Box',
      downed: true, downedLeft: 4, outside: true, water: true, grace: 6,
    };
    s.players = [player(1, 'Hunter', { catches: 2 }), player(0, 'Hider', { self: true, eliminated: true, prop: 3 })];
    return s;
  },

  'round over': () => {
    const s = base({
      phase: 'RoundEnd', round: 2, winner: 0, ends: Math.floor(Date.now() / 1000) + 11, phaseLen: 15,
      nextRound: 24, hidersAlive: 0, lobby: 6, awards: AWARDS,
    });
    s.players = [
      player(1, 'Hunter', { catches: 4, hits: 17, score: 96 }),
      player(0, 'Hider', { self: true, eliminated: true, prop: 0, survived: 271, score: 71, baits: 1 }),
      player(2, 'Hider', { eliminated: true, prop: 2, survived: 210, score: 54 }),
      player(3, 'Hider', { eliminated: true, prop: 4, survived: 120, score: 33, stuns: 2 }),
    ];
    return s;
  },

  'between rounds': () => base({
    phase: 'Safehouse', round: 2, ends: 0, nextRound: 9, lobby: 6,
    safehouse: { name: 'Bungalow', code: 'bungalow', options: 4, ready: false },
  }),

  // Eleven players leave exactly one place big enough for them, so the map buttons cannot go anywhere. The host
  // needs to be told that, or pressing them and seeing nothing change reads as a broken control.
  'between rounds (one place fits)': () => base({
    phase: 'Safehouse', round: 3, ends: 0, nextRound: 12, lobby: 11,
    safehouse: { name: 'Barn', code: 'barn', options: 1, ready: false },
  }),

  'match over': () => {
    const s = base({ phase: 'MatchEnd', round: 5, winner: 1, ends: 0, lobby: 6, awards: AWARDS });
    s.players = [
      player(1, 'Hunter', { catches: 9, hits: 41, score: 210 }),
      player(0, 'Hider', { self: true, survived: 900, score: 188 }),
      player(2, 'Hider', { survived: 640, score: 140 }),
    ];
    return s;
  },

  // The same ending seen by someone who cannot act on it. The host has a button here; this player has to be told
  // what is about to happen to them instead.
  'match over (client)': () => {
    const s = base({ phase: 'MatchEnd', round: 5, winner: 1, ends: 0, lobby: 6, awards: AWARDS, host: false, presets: [] });
    s.players = [
      player(1, 'Hunter', { catches: 9, hits: 41, score: 210 }),
      player(0, 'Hider', { self: true, survived: 900, score: 188 }),
      player(2, 'Hider', { survived: 640, score: 140 }),
    ];
    return s;
  },

  // Someone who joined mid-round: no role yet, and not eliminated either. The roster has to say they are waiting
  // for the next round rather than lumping them in with the players who were caught.
  'a late joiner is waiting': () => {
    const s = base({
      phase: 'Hunting', round: 2, ends: Math.floor(Date.now() / 1000) + 168, phaseLen: 300,
      hidersAlive: 2, lobby: 7, whistle: 19,
    });
    s.me = { ...s.me, role: 'Hunter', hunterHp: 0, hunterMaxHp: 3 };
    s.players = [
      player(0, 'Hunter', { self: true, catches: 1, hits: 6 }),
      player(1, 'Hider', { survived: 168 }), player(2, 'Hider', { survived: 168 }),
      player(3, 'Hider', { eliminated: true, prop: 2, survived: 74 }),
      player(6, 'Unassigned'),
      player(7, 'Spectator'),
    ];
    return s;
  },

  'twenty players': () => {
    const s = base({
      phase: 'Hunting', round: 3, ends: Math.floor(Date.now() / 1000) + 402, phaseLen: 600,
      hidersAlive: 11, lobby: 20, whistle: 25,
    });
    s.me = { ...s.me, role: 'Hider', prop: 1, propName: 'Traffic Cone', maxHp: 2, hp: 0 };
    s.players = [];
    for (let i = 0; i < 4; i++) s.players.push(player(i + 1, 'Hunter', { catches: i, hits: i * 3 }));
    s.players.push(player(0, 'Hider', { self: true, prop: 1, survived: 402, score: 80 }));
    for (let i = 5; i < 15; i++) s.players.push(player(i, 'Hider', { survived: 402, score: 60 - i }));
    for (let i = 15; i < 20; i++) s.players.push(player(i, 'Hider', { eliminated: true, prop: i % 6, survived: i * 9 }));
    return s;
  },

  'no session': () => ({ ok: false }),
};

let scenario = 'hunting (hider, hurt)';

// The bridge, handed over by the shell before the page runs. It is how this file pushes at the page instead of only
// answering it - which is what the mod does when the session moves on.
let host = null;
export const ready = (s1) => { host = s1; };

export function call(name, argument = '') {
  switch (name) {
    case 'ph.snapshot':
      return JSON.stringify(SCENARIOS[scenario]());

    // The rest of PropHunt/Phone/PhoneBackend.cs Handlers. Every one of them changes a live session and there is
    // no session here, so they are accepted and not applied - the strip above the phone moves the state instead.
    // Answering anything other than "ok" is what the mod does when it REFUSES a command, and app.js says so in the
    // console, so a silent stand-in would read as the mod turning every button down.
    case 'ph.begin':
    case 'ph.next':
    case 'ph.endround':
    case 'ph.hub':
    case 'ph.map':
    case 'ph.set':
    case 'ph.preset':
    case 'ph.kick':
    case 'ph.prop.roll':
    case 'ph.prop.clear':
      console.log('call ' + name + (argument ? ' <- ' + JSON.stringify(argument) : ''));
      return 'ok';

    default:
      console.warn(`[preview] no stand-in for s1.call("${name}")`);
      return '';
  }
}

// One chip per session above. Built from the table rather than written out again, so a session added there cannot
// go missing from the strip. Picking one only swaps which snapshot the next call answers with - the page is told
// to ask again by the same event the mod raises when the round moves.
export const scenarios = Object.fromEntries(
  Object.keys(SCENARIOS).map((name) => [name, () => { scenario = name; host?.emit('ph.changed'); }]),
);
