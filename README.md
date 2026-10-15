# Serverbound

Serverbound moves Valheim's world and AI simulation onto the dedicated server. Players keep control of their characters and character saves.

## Install

1. Back up the world and configuration, then stop the server.
2. Install BepInExPack_Valheim 5.4.2351 and remove predecessor simulation DLLs.
3. Extract the package into the server directory. The DLL belongs at `BepInEx/plugins/Serverbound/Serverbound.dll`.
4. Start once to generate `BepInEx/config/org.serverbound.valheim.cfg`. Stop before editing it.
5. Restart and check `BepInEx/LogOutput.log` for `Simulation: APPLIED`.

Basic simulation accepts vanilla clients. ImpactfulSkills integration also needs Serverbound and the supported skill mod on participating clients. The same DLL supplies the client bridge and never runs dedicated simulation on clients. Clients without the bridge can join, but cannot supply skill bonuses.

## Upgrade

Remove `Serverside_Simulations.dll`, `Valheim_Serverside.dll`, and `SarkasticGG_Dedicated_Simulation.dll`, including copies in nested plugin folders. Never load a predecessor alongside Serverbound.

Copy the settings you need from `MVP.Valheim_Serverside_Simulations.cfg` into the new config while stopped. Section and key names are unchanged. Serverbound does not migrate the file automatically.

CharacterGuard and ItemLedger have been removed. Their config settings are discarded during startup. Their old tracking files beside the world save are no longer used.

The new assembly and namespace are `Serverbound`; the plugin GUID is `org.serverbound.valheim`. Custom integrations must update old GUID, Harmony-owner, and reflected-type references. Skill RPCs and replicated keys now use the `Serverbound.*` prefix. ImpactfulSkills clients and the server must use matching Serverbound versions. World data keeps its format.

## Supported builds

Serverbound accepts audited Valheim 1.0.16 and 1.0.17 Windows client and Windows/Linux dedicated assemblies, plus 1.0.20 Windows client and dedicated assemblies. It rejects unsupported assembly hashes.

## Verified supported mods

| Mod | Verified version |
|---|---|
| ValheimPerformanceOptimizations | 1.2.3 |
| ValheimCommunityPatch | 0.34.1, 0.35.0, 0.35.1, 0.36.0 |
| ImpactfulSkills | 0.21.0, 0.21.1, 0.21.3, 0.22.0, 0.22.1, 0.23.0 |
| ValheimTune | 0.7.9 |
| Heightmap Unlimited JvL | 2.0.2 |

> **Note**: If you're using ImpactfulSkills, this mod also needs to be installed on each client running ImpactfulSkills for it to work properly.

### OdinShip

Serverbound integrates with the audited OdinShip 0.8.7 build. Install Serverbound and that OdinShip build on the requesting client and the current ship owner, including the dedicated server when it owns the ship. Check for `OdinShip: APPLIED` in the log.

Nearby clients can request customization and turret-mode changes from the current ship owner. Request range is 8 m from ship controls for customization and rename, or from a ship turret for mode changes. The owner checks access before applying rename and routed requests. Direct owner customization and turret writes retain OdinShip's permissions. Requests do not transfer ship ownership.

Rename requests report acceptance or rejection, including submissions by the owner. Requests time out visibly after five seconds without a response. The OdinShip integration remains active if simulation rolls back.

### Heightmap Unlimited JvL

Heightmap Unlimited JvL 2.0.2 registers its terrain patches only on clients. A simulating server without them rebuilds saved terrain within 8 m of the original ground, and creatures it owns can move through ground that players see as solid. When that server owns a terrain area, leveling or raising there also cuts the saved change back to 8 m for everyone.

With that build installed on the dedicated server, Serverbound runs the mod's own terrain patches there. The limits come from the `Max Height` and `Min Height` settings in the server's Heightmap Unlimited JvL config. Check the log for `TerrainLimits: APPLIED`. Clients keep using the mod as before. Serverbound changes no terrain limits on clients.

Serverbound does not restore terrain that an earlier server already cut back to 8 m.

## How ownership works

The server loads zones and objects around ready players and creates nearby objects first. Areas outside every player's active range can unload. Clients can retain valid ownership leases inside their areas; Serverbound does not continuously seize every object.

Player objects remain client-owned. A ship with a valid driver follows that driver's ownership. An empty ship returns to the server, with an open ship container delaying the handoff.

This increases server CPU, memory, and upload needs, especially when players explore separate areas. Remote actions wait for transport and a server frame, so the ownership change can also increase interaction latency.

## Configuration

Settings are local, are not synchronized, and require a restart. The generated config describes every key.

| Section / key | Default | Effect |
|---|---|---|
| MaxObjectsPerFrame / MaxObjects | 100 | More objects load areas sooner but can lengthen frames. |
| Performance / MaxZonesPerTick | 1 | Shared new-zone budget across players. Zero removes the limit. |
| Performance / ServerTargetFps | 60 | Frame target; actual rate depends on CPU headroom. Zero keeps the game's target. |
| Performance / SendIntervalMs | 100 | World-send interval in milliseconds. Zero keeps existing scheduling. |
| Performance / MaxCatchUpMs | 100 | Fixed-step catch-up cap. Under load, a cap can slow game time. Zero keeps the engine setting. |
| Server / UnityJobWorkers | 8 | Worker-count cap. Zero leaves the count alone. |
| Networking / Enabled | true | Queue and Steam send-rate settings. Disable when another mod owns these. |
| Networking / QueueSizeKB | 48 | Per-peer queued world data. Larger queues can increase latency. |

Choose one owner for overlapping networking and simulation settings. Do not copy a pack preset into a standalone installation without checking it.

## Troubleshooting

- `REJECTED BUILD/INSTALLATION`: check game/mod builds against the supported versions and remove predecessor DLLs. Do not bypass guards.
- `Simulation: DISABLED`: fix the reported takeover mismatch and restart. Stop the server if rollback reports hooks still installed.
- `TerrainLimits: NOT APPLIED`: the server still limits terrain to 8 m. Install the supported Heightmap Unlimited JvL build on the server, restart, and keep players from leveling or raising terrain until the log shows `TerrainLimits: APPLIED`.
- Repeated dungeon loading or distant objects flickering with VCP: check the world-start messages confirming spawn-queue and unload-hook removal.
- Missing skill state: check that the client has Serverbound, the supported ImpactfulSkills build, and `Compatibility / ClientSkills=true`.
- Slow loading or delayed interactions: compare frame times, object counts, memory, and send queues. Raising an object budget or queue size can make other delays worse.

Include the full log, game/mod versions, and reproduction steps in a report.

## Attribution

Serverbound is built on [ddormer's valheim-serverside](https://github.com/ddormer/valheim-serverside) and [MistrCech's maintained fork](https://github.com/MistrCech/valheim-serverside).
