#nullable disable
using Serverbound.FeatureModel;
using HarmonyLib;
using Serverbound.Settings;

namespace Serverbound.Features
{
	/*
		A crafting station's player-base area keeps creatures from spawning around it. The area is as
		wide as the station's build range, which grows with each upgrade built next to it:
		CraftingStation.GetExtensions counts the upgrades and sizes both (Valheim 1.0.17).

		The game calls GetExtensions only for a local player: placing a piece, opening the station,
		drawing the HUD. A vanilla dedicated server never needs the area, because the player who owns
		the zone runs the spawner against their own copy of the station. This mod runs the spawner on
		the server, where nothing ever asks. Every station there kept the size its prefab ships with:
		no upgrade bonus, and a mod's larger build range never reached the area.

		Fix: after the station's own update, run the game's refresh whenever its two-second timer
		allows it, which is as often as a player holding a hammer does.
	*/
	public class StationRange : IFeature
	{
		public bool FeatureEnabled()
		{
			return Configuration.fixStationRange.Value;
		}

		[HarmonyPatch(typeof(CraftingStation), "CustomUpdate")]
		public static class CraftingStation_CustomUpdate_Patch
		{
			static void Postfix(CraftingStation __instance)
			{
				if (__instance.m_updateExtensionTimer >= CraftingStation.m_updateExtensionInterval)
				{
					__instance.GetExtensions();
				}
			}
		}
	}
}
