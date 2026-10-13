# Changelog

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
