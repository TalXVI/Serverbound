# Changelog

## 0.1.21

- Supports ValheimCommunityPatch 0.40.0 while retaining previously supported builds and its shield fixes.
- Lets fully loaded player areas create objects while other players' areas load. Terrain and object-type loading requirements remain enforced.
- Avoids repeated sector queries for players in the same zone and repeated skill reads when selecting a nearby actor.

## 0.1.20

- Supports Valheim 1.0.21 Windows clients and Windows/Linux dedicated servers, and ValheimCommunityPatch 0.39.1. Earlier supported builds remain supported.
- Stops reporting false warnings for absent optional Core hooks. Required patch checks remain in place.

## 0.1.19

- Supports ValheimCommunityPatch 0.39.0 while retaining previously supported builds.
- Saves local portal changes in the portal save chunk, including when the community patch handles received changes.
- Validates ship-control grants against the sending owner and the ready player aboard the ship.
- Disables simulation when a required Core rewrite fails, including after another mod repatches it.
- Shares zone generation fairly among ready players when more than one zone can be generated per tick.

## 0.1.18

- Supports ImpactfulSkills 0.26.0 while retaining previously supported builds. Owner-side skill bonuses keep their existing behavior.

## 0.1.17

- Supports ValheimCommunityPatch 0.38.0. Dedicated simulation retains the existing object-management takeover and Love Potion spawning correction. Previously supported builds remain supported.

## 0.1.16

- Fixes a server crash while exploring caves. When a dungeon room failed to load, the server released that dungeon's room assets twice, which unloaded them while other dungeons were still using them.
- No longer reuses an asset bundle the game's loader doesn't own when Unity refuses to load a second copy. The affected dungeon now skips spawning and is tried again later, instead of risking a crash when either copy unloads.

## 0.1.15

- Supports ImpactfulSkills 0.25.0 while retaining previously supported builds. Mining, woodcutting, loot, plant, beehive, and boat bonuses still use the player's own skill. 0.25.0 handles taming speed and taming XP itself, so Serverbound no longer touches that code.

## 0.1.14

- Turns off OdinShip customization and ballista-mode routing if another mod later changes the same OdinShip input code, and logs `OdinShip: NO LONGER APPLIED`. OdinShip's own owner-only controls take over, and a ship owner rejects routed change requests instead of applying them.
- Leaves a game method unchanged, with a warning in the log, when it no longer has the shape a patch expects. Several patches used to rewrite whatever matches they found, so a game update could leave a method half patched.
- Reads ImpactfulSkills settings and game fields through compiled accessors instead of reflection on every mining, woodcutting, taming, and boat-damage hit.

## 0.1.13

- Removes ComfortTweaks 3.3.11's client-only rested check from dedicated-server portal triggers. Nonplayer colliders previously caused repeated null-reference exceptions. Clients keep the existing portal rule.

## 0.1.12

- Supports ValheimCommunityPatch 0.37.0 and ImpactfulSkills 0.24.0 while retaining previously supported builds.
- Runs VCP's Love Potion spawn correction on the dedicated server when server simulation is active, preserving its configuration and existing vendor registrations.
- Lets static objects settle around remote players after terrain changes, so chopping a lowered tree uses its settled position for the stump.

## 0.1.11

- Supports the Valheim 1.0.20 Linux dedicated build. Unsupported game detection previously prevented server simulation and the configured server FPS target from applying on this build.

## 0.1.10

- Supports ValheimCommunityPatch 0.36.1 while retaining the previously supported builds.

## 0.1.9

- Supports Valheim 1.0.20 Windows clients and dedicated servers and ImpactfulSkills 0.23.0. Previously supported builds remain supported.

## 0.1.8

- Makes smoke on the dedicated server near players, so smoke blocks what the server runs the same way it does on a player's machine. The server never made smoke before, so a fireplace that players saw smothered kept burning fuel and setting nearby things alight, fires under a roof never choked, and smelters, kilns, and furnaces with blocked chimneys kept working. Those stations now stop until their chimney is clear, as they do without this mod. The new `[Fixes] ServerSmoke` setting is on by default.
- Puts out a fire the server took over from a player once the piece, tree, or log it burns on is destroyed. The server used to keep such a fire burning in mid-air until it timed out. The new `[Fixes] HandedOverFires` setting is on by default.

## 0.1.7

- Keeps the no-spawn area around each crafting station as wide as its build range on the dedicated server, including the range each upgrade adds and any larger range set by another mod. The server used to keep the area at its default 20 m, so creatures could spawn inside a base. The new `[Fixes] StationRange` setting is on by default.

## 0.1.6

- Supports ValheimCommunityPatch 0.36.0 while retaining the previously supported builds.

## 0.1.5

- Applies Heightmap Unlimited JvL 2.0.2's configured terrain limits on the dedicated server. The server no longer rebuilds terrain raised or dug beyond 8 m at the 8 m limit, and no longer cuts saved terrain back to 8 m when it levels or raises ground it owns.

## 0.1.4

- Supports ImpactfulSkills 0.22.0 and 0.22.1, plus ValheimCommunityPatch 0.35.1. Previously supported builds remain accepted.

## 0.1.3

- Adds optional owner-routed customization and turret-mode requests for the audited OdinShip 0.8.7 build.
- Measures request range from ship controls or the relevant turret and reports owner rename results.
- Keeps the OdinShip integration active when simulation rolls back. Owner customization and turret permissions retain vendor behavior.
- Supports ImpactfulSkills 0.21.3 and ValheimCommunityPatch 0.35.0 while retaining the previously supported builds.
- Preserves replicated skill reads through ImpactfulSkills' new bonus-cap helper.

## 0.1.2

- Supports ImpactfulSkills 0.21.1 while retaining support for 0.21.0.
- Preserves StartupAccelerator's deferred configuration saves when removing obsolete settings.

## 0.1.1

- Removes CharacterGuard and ItemLedger, including character entry restrictions, item tracking and removal, and the `characters` and `allow` console commands.
- Removes obsolete settings during startup while preserving other configuration values.

## 0.1.0

- Introduces Serverbound's assembly, plugin GUID, namespaces, configuration file and version series.
- Adds VPO/VCP takeover and the ImpactfulSkills server/client bridge.
- Fixes VCP detection timing and rejects competing predecessor.
- Preserves raid transpilers, ready-peer filtering, ship validity checks and dungeon patch ordering.
- Builds and packages `bin/Release/Serverbound.dll`, records tested source/artifact hashes, and verifies identical bytes after a clean rebuild.
- Keeps CharacterGuard disabled and ItemLedger off.
