# Changelog

All notable changes to PropHunt are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-08-01

### Fixed
- Friendly fire and the concussion grenade knock people down again. Schedule I 0.4.6f11 deleted the
  system both were built on, so neither did anything at all. A knocked-down player still drops away
  from whoever hit them.
- Your disguise no longer turns into nothing on someone else's screen. Each machine built its own prop
  list from the part of the map it had loaded, and the game streams building interiors - so a player
  standing inside one could become a prop nobody outside had. Everyone uses the host's list now, and it
  grows as soon as anyone walks into a new building.
- Props outside that list are no longer highlighted or offered, so every prop you can pick shows up for
  everyone.
- Pressing `F` to rotate your prop no longer flicks the flashlight on as well. It moved onto the phone
  in 0.4.6f11.

### Changed
- WhatsDab is required now. Side Hustle switches it on for you, or tells you to install it, instead of
  starting a round without chat.
- Needs S1API 3.1.1 (was 3.0.5) and Side Hustle 2.2.1.

## [1.1.1] - 2026-07-26

### Fixed
- Typing a message in a phone app no longer triggers PropHunt's controls. Writing "hey" opened the controls
  overlay, dropped a decoy and swapped your prop, because every hotkey listened while you were typing.

## [1.1.0] - 2026-07-09

### Changed
- Bigger lobbies now come straight from Side Hustle, so PropHunt no longer needs the separate BiggerLobbies
  mod - one less thing to install. Side Hustle raises the lobby cap for you.

## [1.0.1] - 2026-07-09

### Changed
- Hid the two unfinished experimental presets ("Blend In", "Closing Time") from the host
  form so every preset you can pick is fully playable. They return once their headline
  mechanic (NPC disguise / shrinking play area) is ready.

## [1.0.0] - 2026-07-08

First public release.

### Added
- Multiplayer prop-hunt gamemode, hosted and launched from the Side Hustle menu and run
  from an in-game PropHunt phone app.
- Disguise as any curated world prop (`E` to become the one you look at, `2` for a random
  one), an exact copy of its shape and detail, with rotate and lock.
- Hider toolkit: slow-walk, decoys, concussion grenades, and taunts.
- Hunters are given a weapon at the start of the hunt and catch hiders by hitting their
  prop; bigger props take more hits. Friendly fire knocks hunters down briefly.
- Round flow with a hiding phase (hunters blinded), the hunt, and a scoreboard - continuous
  rounds with role swaps or a single round back to the hub, plus a visible play-area border.
- Full host control from the phone app: round times, roles, prop HP and changes, abilities,
  the hunter's weapon, play-area size, taunts, time of day and more, with presets. Clients
  follow the live state; only the host changes the rules or kicks players.
- Public lobbies (non-friends can join) and support for up to 20 players via BiggerLobbies.
- Third-person view for hiders and a spectator cam once caught.
