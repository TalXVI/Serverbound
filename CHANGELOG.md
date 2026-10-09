# Changelog

## 0.1.2

- Supports ImpactfulSkills 0.21.1 while retaining support for 0.21.0.
- Preserves StartupAccelerator's deferred configuration saves when removing obsolete settings.

If you also use DeepNorthCompat, update it to 1.2.1 for its fixes with ImpactfulSkills 0.21.1.

## 0.1.1

- Removes CharacterGuard and ItemLedger, including character entry restrictions, item tracking and removal, and the `characters` and `allow` console commands.
- Removes obsolete settings during startup while preserving other configuration values.

## 0.1.0

- Introduces Serverbound's assembly, plugin GUID, namespaces, configuration file and version series.
- Adds VPO/VCP takeover and the ImpactfulSkills server/client bridge.
- Fixes VCP detection timing and rejects competing predecessor.
- Preserves raid transpilers, ready-peer filtering, ship validity checks and dungeon patch ordering.
- Builds and packages `bin/Release/Serverbound.dll`, records tested source/artifact hashes, and verifies identical bytes after a clean rebuild.
- Keeps CharacterGuard disabled and ItemLedger off. Multiplayer and extended performance acceptance remain follow-up work.
