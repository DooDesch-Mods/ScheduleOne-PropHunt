# PropHunt - Hide as a Prop, or Hunt Them Down

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

A multiplayer prop-hunt gamemode for Schedule I. Hiders disguise as everyday props and
blend into the world; hunters get a weapon and track them down before the timer runs out.
Hosted and launched from the [Side Hustle](https://thunderstore.io/c/schedule-i/p/DooDesch/SideHustle/)
menu and run from your in-game phone. Built on [S1API](https://github.com/ifBars/S1API).

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)

## Features

- **Become almost anything.** Look at a prop and press `E` to turn into it (an exact copy),
  or `2` for a random one. Rotate and lock your facing. The becomable props are curated.
- **A real hider toolkit.** Slow-walk, drop decoys that look like your prop, throw a
  concussion to stun nearby hunters, and taunt to give yourself away on purpose or on a timer.
- **Hunters shoot to catch.** Each hunter gets a weapon; bigger props take more hits.
  Friendly fire knocks a wild hunter's teammate out for a few seconds.
- **Full round flow.** A hiding phase (hunters blinded), the hunt, then a scoreboard -
  continuous rounds with role swaps or a single round, with a visible play-area border.
- **The host runs it from their phone.** Round length, roles, prop HP, abilities, the
  hunter's weapon, play area, taunts and more, with presets. Clients follow the live state;
  only the host changes the rules or kicks players.
- **Made for a full lobby.** Public lobbies let non-friends join; with BiggerLobbies you can
  fill up to 20 players. Hiders play in third person and get a spectator cam once caught.

## Requirements

- [MelonLoader](https://github.com/LavaGang/MelonLoader) `0.7.3+` - the mod loader.
- [S1API](https://github.com/ifBars/S1API) (ifBars-S1API_Forked) - the Schedule I modding API.
- [Side Hustle](https://thunderstore.io/c/schedule-i/p/DooDesch/SideHustle/) - the gamemode hub PropHunt launches from.
- [SteamNetworkLib](https://thunderstore.io/c/schedule-i/p/ifBars/SteamNetworkLib_Il2Cpp/) - multiplayer state sync.
- [BiggerLobbies](https://thunderstore.io/c/schedule-i/p/ifBars/BiggerLobbies/) - raises the lobby cap to 20.

A mod manager installs all of these automatically.

## How to play

1. On the main menu, open **Side Hustle** and pick **PropHunt**.
2. **Host:** set the player count and settings (or a preset) and start hosting; friends
   join via Steam or the lobby browser.
3. Start the match and each round from the in-game **PropHunt phone app**.

Everyone in the lobby needs the same mods and the same PropHunt build.

Provided as-is under the MIT License. Built on [S1API](https://github.com/ifBars/S1API) by ifBars.
