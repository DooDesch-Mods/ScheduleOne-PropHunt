# Changelog

All notable changes to PropHunt are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
