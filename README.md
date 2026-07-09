# PropHunt - Hide as a Prop, or Hunt Them Down

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> A multiplayer prop-hunt gamemode for Schedule I - as far as I know the first prop hunt for
> this game. Hiders look at any world prop and become an exact copy of it (a crate, a bin, a
> traffic cone) and blend into the map; hunters get a weapon and track them down before the
> timer runs out. Hosted and launched from the [Side Hustle](https://github.com/DooDesch-Mods/ScheduleOne-SideHustle)
> menu and run from your in-game phone. Built on [S1API](https://github.com/ifBars/S1API).

![Version](https://img.shields.io/badge/version-1.1.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)

## Features

- **Become almost anything.** Look at a world prop and press **`E`** to turn into it, an
  exact copy of its real shape and detail, or press **`2`** for a random one. Rotate and
  lock your facing so you sit exactly like the real thing. The becomable props are curated,
  so you never end up as an un-hideable mess.
- **A real hider toolkit.** Slow-walk to creep past a hunter, drop **decoys** that look
  just like your prop, throw a **concussion** to stun hunters who get close, and **taunt**
  to give yourself away on purpose (or on a timer the host sets).
- **Hunters shoot to catch.** Each hunter gets a weapon at the start of the hunt and
  catches a hider by hitting their prop; bigger props take more hits. Friendly fire is on,
  so a wild hunter can knock a teammate out for a few seconds.
- **Full round flow.** A hiding phase (hunters are blinded), then the hunt, then a
  scoreboard - either continuous rounds with role swaps, or a single round back to the hub.
  A visible play-area border keeps everyone in bounds.
- **The host runs it from their phone.** Round length, roles, prop HP, abilities, the
  hunter's weapon, play-area size, taunts, time of day and more - all set in-game from the
  PropHunt phone app, with presets. Everyone else sees the live state (players, timer,
  scoreboard) but only the host changes the rules or kicks a player.
- **Made for a full lobby.** Public lobbies let non-friends join straight from the lobby
  browser - as far as I can tell nobody else has gotten public lobbies working for Schedule I
  yet. Side Hustle raises the lobby cap for you, so you can fill a big lobby. Hiders play in
  third person; you get a spectator cam once you are caught.

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | `0.4.5f2` (IL2CPP, current Steam public build) |
| MelonLoader | `0.7.3+` |
| S1API | [ifBars/S1API_Forked](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) - the Schedule I modding API |
| Side Hustle | [DooDesch/SideHustle](https://thunderstore.io/c/schedule-i/p/DooDesch/SideHustle/) - the gamemode hub PropHunt launches from; also raises the lobby cap for you |
| SteamNetworkLib | [ifBars/SteamNetworkLib_Il2Cpp](https://thunderstore.io/c/schedule-i/p/ifBars/SteamNetworkLib_Il2Cpp/) - multiplayer state sync |

## Installation

### Recommended: a Thunderstore mod manager

Install with a mod manager (r2modman / Gale) from the Schedule I community; the
dependencies (MelonLoader, S1API, Side Hustle, SteamNetworkLib) are pulled
in automatically.

### Manual

1. Install **MelonLoader 0.7.3** for Schedule I and launch the game once.
2. Install **S1API**, **Side Hustle** and **SteamNetworkLib** per their own instructions.
3. Drop **`PropHunt.dll`** into your Schedule I `Mods/` folder.

Everyone who plays together needs the **same mods and the same build** - the game warns
about a version mismatch on join.

## Configuration

There is nothing to edit in a config file. The host picks everything **in-game** from the
PropHunt app on their phone (or the Side Hustle host form) before a round: round length,
players per hunter, prop HP and max changes, the hider toolkit (decoys, concussions,
taunts), the hunter's weapon and friendly fire, the play-area size, the time of day, and
more. Pick a preset or tune individual values; your choices are saved as the defaults for
next time. Clients can open the same app to follow the state live, but only the host
changes anything.

Developer tools (the `F3` overlay and the `ph*` console commands) exist only in
development builds and are not shipped in the release.

## How to play

1. On the **main menu**, open **Side Hustle** and pick **PropHunt**.
2. **Host:** choose *Host*, set the player count and round settings (or a preset), then
   start hosting. Invite friends via Steam, or they use *Join*.
3. **Join:** choose *Join* and pick the host's lobby.
4. The host starts the match and each round from the in-game **PropHunt phone app**.

It's built for a group, so grab a few friends or open a public lobby. Looking for people to
play with (or need a hand setting it up)? Hop in the [Discord](https://discord.gg/aN3u7BTa3h).

### Controls

**Hider**

| Key | Action |
|---|---|
| `E` | Become the prop you are looking at |
| `2` | Become a random prop |
| `F` + mouse | Rotate your prop |
| `Q` | Drop a decoy |
| `G` | Concussion (stun nearby hunters) |
| `1` | Taunt |
| `B` | Toggle the becomable-prop markers |
| `V` | Third-person view (on at round start) |
| `Ctrl` | Slow-walk |

**Hunter**

| Key | Action |
|---|---|
| Left click | Shoot / catch props |

**Spectator (after you are caught)**

| Key | Action |
|---|---|
| `4` | Follow-cam / freecam |
| Left click | Next player (follow-cam) |

Press `H` any time for the in-game controls overlay.

## Compatibility

- IL2CPP build only (current Steam public branch).
- Everyone in the lobby needs the same mods and the same PropHunt build.
- Side Hustle can auto-disable incompatible mods for the session (PropHunt asks for a
  required-mods-only lobby by default).

## Credits

- **DooDesch** - mod author.
- **[ifBars/S1API](https://github.com/ifBars/S1API)** - the modding API this is built on,
  plus SteamNetworkLib.
- **[Side Hustle](https://github.com/DooDesch-Mods/ScheduleOne-SideHustle)** - the gamemode
  hub PropHunt runs inside.
- **Testers** - DonyThePony, fadestyle, xAkitoh, godofn00bs. Thanks for the playtesting.

## License

Provided as-is under the [MIT License](LICENSE.md).
