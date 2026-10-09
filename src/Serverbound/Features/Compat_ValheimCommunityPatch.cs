#nullable disable
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Serverbound.FeatureModel;
using HarmonyLib;
using Serverbound.Settings;
using System.Linq;
using System.Reflection;
using System;
using System.Collections.Generic;
using Serverbound.Compatibility;

namespace Serverbound.Features
{
	/*
		ValheimCommunityPatch speeds up the object pass by assuming what a client can assume: that
		objects are created and dropped around one point, ZNet.GetReferencePosition(). On a dedicated
		server running this mod that point stays at the world origin while the server simulates around
		every player, so two of its patches work against this mod:

		- the spawn queue (a prefix on ZNetScene.CreateObjectsSorted that always skips the original)
		  orders new objects by distance from the origin, so this mod's ordering by the nearest player,
		  a transpiler on that same method, never runs;
		- the zone-diff unload (a prefix on ZNetScene.RemoveObjects) drops everything outside the
		  simulation distance of the origin whenever the object lists look untouched since the game
		  filled them -- and with players far enough apart that their areas do not overlap, the lists
		  this mod fills really are untouched. Objects around the players are then destroyed and created
		  again on every pass: dungeons reloading dozens of times a second, their room bundles loaded
		  and unloaded faster than Unity finishes ("another AssetBundle with the same files is already
		  loaded"), doors, beds and items that cannot be used because they keep being recreated
		  (ddormer/valheim-serverside#119, #120).

		Neither has a switch of its own. Each is removed once the world starts unless its [Compat]
		setting keeps it; the rest of ValheimCommunityPatch is left alone.
	*/
	public class Compat_ValheimCommunityPatch : IFeature
	{
		public const string PluginId = "MidnightsFX.ValheimCommunityPatch";

		// Its zone-diff unload was kept: the object pass marks its lists as edited so it takes the
		// game's own unload check, its own path for mods like this one (see Core.CreateDestroyObjects).
		public static bool UnloadKept;

		public bool FeatureEnabled()
		{
			// Install the lifecycle hook even when VCP chainloads after Serverbound.
			return true;
		}

		private static void TakeOver(string method, ConfigEntry<bool> keep, string what, string cost)
		{
			MethodBase target = AccessTools.DeclaredMethod(typeof(ZNetScene), method);
			if (keep.Value)
			{
				if (method == "RemoveObjects")
				{
					UnloadKept = true;
					Plugin.logger.LogInfo($"Keeping ValheimCommunityPatch's {what} on ZNetScene.{method} ([Compat] {keep.Definition.Key} = true); the object lists are marked as edited on every pass so it uses the game's own unload check.");
					return;
				}
				Plugin.logger.LogWarning($"Keeping ValheimCommunityPatch's {what} on ZNetScene.{method} ([Compat] {keep.Definition.Key} = true): {cost}");
				return;
			}
			string group = method == "RemoveObjects" ? "ZoneDiffRemovalPatch" : "SpawnQueueCachePatch";
			string hookName = method == "RemoveObjects" ? "RemoveObjectsPrefix" : "CreateObjectsSortedPrefix";
			Assembly vendor = SimulationPatch.VcpAssembly;
			Guard.Build(vendor, "E48804F2280878B1BB30393C57C354EFDAA68F106A0B2C77F150C113A25EAA68");
			MethodInfo hook = AccessTools.DeclaredMethod(Guard.Type(vendor, "ValheimCommunityPatch.Patches.Performance." + group), hookName);
			var registration = SimulationPatch.ExactHook(typeof(ZNetScene), method, hook, PluginId, HarmonyPatchType.Prefix);
			removed.Add(registration);
			Plugin.harmony.Unpatch(target, hook);
			if (SimulationPatch.All(target).Any(patch => patch.PatchMethod == hook))
			{
				throw new InvalidOperationException($"Could not remove ValheimCommunityPatch's {what} from ZNetScene.{method}: {cost}");
			}
			Plugin.logger.LogInfo($"Removed ValheimCommunityPatch's {what} from ZNetScene.{method}: it works around the server's reference position, the world origin on a dedicated server, not around the players. Set [Compat] {keep.Definition.Key} = true to keep it.");
		}

		[HarmonyPatch(typeof(ZNetScene), "Awake")]
		public static class ZNetScene_Awake_Patch
		{
			internal static bool s_done;

			// By now every plugin has run its Awake, and ValheimCommunityPatch patches in its Awake.
			static void Postfix()
			{
				if (s_done || !SimulationPatch.Active || SimulationPatch.VcpAssembly == null)
				{
					return;
				}
				s_done = true;
				removed.Clear();
				try
				{
				TakeOver("CreateObjectsSorted", Configuration.compatVcpSpawnQueue, "spawn queue",
					"new objects are created in order of distance from the world origin, not from the nearest player.");
				TakeOver("RemoveObjects", Configuration.compatVcpUnload, "zone-diff unload",
					"objects around players far from the world origin can be destroyed and created again on every pass.");
					foreach (var hook in removed) SimulationPatch.RememberRemoved(hook);
				}
				catch (Exception error)
				{
					foreach (var hook in removed)
					{
						try { SimulationPatch.Restore(hook); }
						catch (Exception rollback) { Plugin.logger.LogError("VCP takeover rollback failed; stop the server. " + rollback); }
					}
					Plugin.logger.LogError("VCP takeover rejected. " + error);
					Plugin.DisableSimulation();
				}
				finally { removed.Clear(); }
			}
		}
		private static readonly List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)> removed = new List<(MethodBase, Patch, HarmonyPatchType)>();
	}
}
