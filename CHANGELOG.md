# Changelog

## 0.1.0

- Introduces Serverbound's assembly, plugin GUID, namespaces, configuration file and version series.
- Adds VPO/VCP takeover and the ImpactfulSkills server/client bridge.
- Fixes VCP detection timing and rejects competing predecessor.
- Preserves raid transpilers, ready-peer filtering, ship validity checks and dungeon patch ordering.
- Builds and packages `bin/Release/Serverbound.dll`, records tested source/artifact hashes, and verifies identical bytes after a clean rebuild.
- Keeps CharacterGuard disabled and ItemLedger off. Multiplayer and extended performance acceptance remain follow-up work.
