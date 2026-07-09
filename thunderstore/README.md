# PropHunt - Hide as a Prop, or Hunt Them Down

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

A multiplayer prop-hunt gamemode for Schedule I - as far as I know the first prop hunt for
this game. Hiders look at any world prop and become an exact copy of it (a crate, a bin, a
traffic cone) and blend into the map; hunters get a weapon and track them down before the
timer runs out. Hosted and launched from the [Side Hustle](https://thunderstore.io/c/schedule-i/p/DooDesch/SideHustle/)
menu and run from your in-game phone. Built on [S1API](https://github.com/ifBars/S1API).

![Version](https://img.shields.io/badge/version-1.0.1-blue)
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
- **Made for a full lobby.** Public lobbies let non-friends join straight from the lobby
  browser - as far as I can tell nobody else has gotten public lobbies working for Schedule I
  yet. With BiggerLobbies you can fill up to 20 players, and hiders play in third person with
  a spectator cam once caught.

## Presets

Pick one on the host form and start - no config files, and you can still tweak any setting after.

- **Classic Hunt** - the standard round; caught hiders are out and spectate. Best for 4-10.
- **Infection** - caught hiders turn into hunters, so it snowballs the longer it runs. Great for 6-16.
- **Panic Room** - tiny zone, short rounds, "just one more". Best for 2-5.
- **Ranked Rules** - competitive, low randomness, no friendly fire. Best for 4-8.
- Plus **Side Hustle Party**, **Deep Cover** and **Last Prop Standing** for bigger or slower lobbies.

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

It's built for a group, so grab a few friends or open a public lobby. Everyone in the lobby
needs the same mods and the same PropHunt build - easiest if you all install from the same
mod-manager profile. Looking for people to play with? Hop in the [Discord](https://discord.gg/aN3u7BTa3h).

If you have fun with it, leaving a rating (or an endorsement over on Nexus) really helps
other people find it - ty either way, and lemme know if anything breaks or you've got ideas 🙂

Thanks to the beta testers: DonyThePony, fadestyle, xAkitoh, godofn00bs.

Provided as-is under the MIT License. Built on [S1API](https://github.com/ifBars/S1API) by ifBars.
