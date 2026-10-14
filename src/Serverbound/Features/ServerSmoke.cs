#nullable disable
using Serverbound.FeatureModel;
using HarmonyLib;
using Serverbound.Settings;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Serverbound.Features
{
	/*
		Smoke is what blocks a smoke source. SmokeSpawner.Spawn puffs every half second unless
		something in the smoke or solid layers already sits on it, and IsBlocked reports a source
		that has not puffed for 4 s. Fireplace.CheckUnderTerrain then counts the fireplace as not
		burning (no fuel used, no sparks, no ignition), Smelter.UpdateSmoke stops production, and
		Fire.UpdateFire chokes a fire whose smoke has nowhere to go (Valheim 1.0.17).

		Spawn only does any of this while the local player is within 64 m. Otherwise it records a
		puff without making one, so the source never counts as blocked. A vanilla dedicated server
		never needs smoke, because the player who owns these objects simulates them. This mod runs
		them on the server, which has no local player, so nothing it ran was ever blocked: a
		fireplace every player saw smothered kept burning on the server, and a smelter with a
		blocked chimney kept working.

		Fix: the single local-player read in Spawn becomes the nearest player. The game's cap of
		100 puffs stays as it is, now shared by every player's surroundings, so with several smoky
		bases the oldest puffs fade sooner than on a player's machine.
	*/
	public class ServerSmoke : IFeature
	{
		public bool FeatureEnabled()
		{
			return Configuration.fixServerSmoke.Value;
		}

		[HarmonyPatch(typeof(SmokeSpawner), "Spawn")]
		public static class SmokeSpawner_Spawn_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
			{
				FieldInfo localPlayer = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
				List<CodeInstruction> codes = instructions.ToList();
				List<CodeInstruction> reads = codes.FindAll(code => code.LoadsField(localPlayer));
				if (reads.Count != 1)
				{
					// A partial swap would tie smoke to the wrong player, so the method stays as the game ships it.
					Plugin.logger.LogWarning($"{__originalMethod.DeclaringType.Name}.{__originalMethod.Name}: found {reads.Count} Player.m_localPlayer read(s), expected 1. The game changed this method; server smoke is off until the patch is reviewed.");
					return codes;
				}
				// Rewritten in place so the read's labels and exception blocks stay where branches expect them.
				CodeInstruction read = reads[0];
				read.opcode = OpCodes.Ldarg_0;
				read.operand = null;
				codes.Insert(codes.IndexOf(read) + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmokeSpawner_Spawn_Patch), nameof(Watcher))));
				return codes;
			}

			// Spawn measures its own 64 m from the player this returns.
			public static Player Watcher(SmokeSpawner spawner)
			{
				return Player.GetClosestPlayer(spawner.transform.position, float.PositiveInfinity);
			}
		}
	}
}
