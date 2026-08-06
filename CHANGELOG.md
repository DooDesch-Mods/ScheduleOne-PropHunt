# Changelog

All notable changes to PropHunt are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.4] - 2026-08-06

### Fixed

- The sewer stays reachable and needs no key. A round plays in a brand new save, where the game still asks for the
  Sewer Key that nobody in it can have - and a round starting next to a hatch shut it as well. Either way a hider
  ended up sealed in, unreachable, with no way for the hunters to follow.
- You can always stand up again. Crawl spaces crouch you on the way in and leave it to you to get up, which a prop
  was not allowed to do - so one low passage left you stuck crouched for the rest of the round.
- Hunters can use the phone torch again, and a hider who was already carrying a lit one stops glowing inside their
  prop. A round blocked the torch for everybody instead of only for hiders, and putting it out worked on the
  owner's screen only, so every other player still saw the light.
- Every preset starts the play area at 50m, so the radius follows your lobby size again instead of the preset's own
  number. Infection jumped to 75m and Side Hustle Party to 90m, whatever the lobby size said.
- The hunter ratio defaults to 4: five players is 1 hunter and 4 props, ten is 2 and 8. Its description also
  promised the count rounds up, while the role assignment divides down.

## [1.3.0] - 2026-08-06

### Added
- Click to lock your prop where it stands, mid-air included. Click again to drop. There is no timer and
  no cost: a crate wedged under a ceiling can stay there all round.
- You are physically the size of your prop, not a person wearing one. A thin sign fits into a gap no
  person gets through, and a hunter shooting at a knee-high box hits a knee-high body.
  - `Hiders are prop-sized` on the host form turns it off.
- Try props on in the lobby before the match starts. Everyone sees you, `2` rolls another one, hold `F`
  to turn it, and the camera swings out so you can look at yourself.
  - Decoys, concussions and taunts stay out of the lobby - those are round mechanics with budgets.
- Props can be shuffled during the hunt, so nobody sits out a whole round in one perfect corner.
  - `Prop rotation` on the host form, in seconds. 0 keeps it off. A forced change refills your decoys
    and concussions and does not count against your own prop changes.
  - `NEW PROP IN 15s` counts down above your hotbar first, so the change is never a surprise.
- The play area is drawn on the phone map: everything outside it is tinted red, with a line where the
  wall stands. Left alone, the area now grows with the lobby - 50m up to ten players, 60m past that.
- Grabbing at a suspicious prop with the trash grabber makes it whistle if it is a player. A hunter
  finally gets an answer instead of a mis-click, and a found hider should run.
- Hosts can end a round early from the phone, and switch `Auto-start next round` off while one is
  running. `End round` scores it as a hunter win and drops you back into the round setup.
- Deep water counts as leaving the play area: a warning, ten seconds, then the same result. How deep is
  measured against your prop, so a sign counts as submerged long before a vending machine does. Wading
  is still fine, and the sewers stay passable.

### Fixed
- Shots register when you are right in front of a hider, and when their prop stands against a wall.
  Both used to be swallowed with no hit and no sound.
- The round timer and the whistle countdown agree on every machine, and the hunt opens with a whistle
  instead of a silent first interval. Two PCs whose clocks differ by a few seconds used to show
  different numbers, and the whistle went off while the countdown still had time on it.
- The first round no longer always picks the same hunter. Who hunts first is rolled per match; after
  that everyone still takes their turn in order.
- Spectators no longer get the out-of-bounds warning and the beeping while they fly around.
- A hunter who has been stunned can select a weapon again. Standing back up left the hotbar dead for
  the rest of the round.
- The sewer king is removed during a round. He attacks on sight, and a crate cannot fight or run.
- A prop has no hotbar, no flashlight and cannot crouch. `2` reached the inventory instead of your
  disguise (and needed pressing twice), and aiming with `F` switched a light on inside your crate.
- The golden toilet, the toilet, the jukebox and the laundering station sat 50 cm above the ground.
- Joining players were never sent Sideload, so WhatsDab had nothing to run on and the phone had no
  chat. Both ship with the round now.

### Changed
- `Catch range` controls melee reach. It had no effect at all before. A gun still catches as far as it
  shoots, so the difficulty stays in how small a prop you picked.
- Changing `Prop rotation` mid-round takes effect from the NEXT round. It used to move the schedule
  instantly and change props on the spot.
- The sewer goblin is allowed by default. It is a nuisance; the sewer king was the actual problem.
- The `Props` tab is the lobby dressing room now, not a list of names.
- The trash grabber holds ten times as much, so it stays useful for a whole round.

### Removed
- The two outside doors are gone from the prop list. As a disguise they dragged a whole building along.

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
