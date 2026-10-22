#nullable disable
using Serverbound.FeatureModel;
using HarmonyLib;
using Serverbound.Settings;
using SoftReferenceableAssets;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Serverbound.Features
{
	/*
		A dungeon whose room bundle Unity refuses to load never spawns, and its zone stays flagged as
		loading for good. In the game's asset loader (SoftReferenceableAssets, Valheim 1.0.15),
		Unity refuses a bundle with "another AssetBundle with the same files is already loaded" and
		returns null. BundleLoader.OnLoadAsyncCompleted stores the null and reports success.
		AssetLoader.LoadAssetFromBundleAsync then dereferences it and throws, so
		DungeonGenerator.OnRoomLoaded never hears back.

		A vanilla dedicated server never instantiates dungeons near players, so it never gets here.
		This mod does it for every player. Seen on a 1.0.15 Linux server as an endless loop over the
		same two dungeons (ddormer/valheim-serverside#120).

		Three guards, each idle unless that failure happens:
		1. OnLoadAsyncCompleted without a bundle reports the load as failed. It does not adopt a
		   bundle Unity already holds under the same name. Only this loader handles that name, so
		   the held copy belongs to code outside it, and an Unload(true) from either side would
		   leave the other reading a closed archive.
		2. LoadAssetFromBundleAsync without a bundle reports the asset as failed instead of throwing.
		3. OnRoomLoaded with a Failed room lets the dungeon go. It does not spawn, and it releases
		   its rooms and its zone, so the zone keeps working and the dungeon is tried again the
		   next time it is created. Aborted is not a failure. ReleaseHeldReferences sends Aborted
		   to every pending room, and treating it as a failure ran the release loop again, releasing
		   rooms other dungeons still held. The guard skips a call an earlier prefix already handled,
		   because ValheimCommunityPatch has its own fix for this callback and spawns the dungeon
		   without the missing room.
	*/
	public class DungeonLoadGuard : IFeature
	{
		private static readonly HashSet<string> s_logged = new HashSet<string>();

		public bool FeatureEnabled()
		{
			return Configuration.fixDungeonLoadGuard.Value;
		}

		private static void LogOnce(string key, string text)
		{
			if (s_logged.Add(key))
			{
				Plugin.logger.LogWarning(text);
			}
		}

		[HarmonyPatch(typeof(BundleLoader), "OnLoadAsyncCompleted", new Type[0])]
		public static class BundleLoader_OnLoadAsyncCompleted_Patch
		{
			static bool Prefix(ref BundleLoader __instance)
			{
				AssetBundleCreateRequestWrapper request = __instance.m_asyncLoadRequest;
				if (request == null || request.AssetBundle != null)
				{
					return true;
				}
				__instance.m_bundle = null;
				__instance.m_asyncLoadRequest = null;
				LogOnce("bundle " + __instance.m_bundleName, $"Unity returned no bundle for {__instance.m_bundleName}; reporting its assets as failed.");
				if (__instance.m_shouldBeLoaded)
				{
					__instance.InvokeCallbacks(LoadResult.Failed);
				}
				else if (BundleLoader.s_tickOnAsyncOperationComplete)
				{
					__instance.TickAsync();
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(AssetLoader), "LoadAssetFromBundleAsync")]
		public static class AssetLoader_LoadAssetFromBundleAsync_Patch
		{
			static bool Prefix(ref AssetLoader __instance)
			{
				AssetBundleLoader loader = AssetBundleLoader.Instance;
				if (loader == null || loader.m_bundleLoaders[__instance.m_bundleLoaderIndex].Bundle != null)
				{
					return true;
				}
				LogOnce("asset " + __instance.m_assetID, $"Asset {__instance.m_assetID} ({__instance.m_assetPathInBundle}) has no bundle to load from; reporting it as failed.");
				__instance.InvokeCallbacks(LoadResult.Failed);
				return false;
			}
		}

		[HarmonyPatch(typeof(DungeonGenerator), "OnRoomLoaded")]
		[HarmonyAfter(Compatibility.VcpObjectPassPatch.PluginId)]
		public static class DungeonGenerator_OnRoomLoaded_Patch
		{
			static bool Prefix(DungeonGenerator __instance, LoadResult result, bool __runOriginal)
			{
				// Aborted is emitted during ReleaseHeldReferences; treating it as failure reenters cleanup.
				if (!__runOriginal || result != LoadResult.Failed || __instance == null || !__instance)
				{
					return true;
				}
				Vector3 position = __instance.transform.position;
				LogOnce("dungeon " + ZoneSystem.GetZone(position), $"Dungeon {__instance.name} at {position:F0}: a room failed to load ({result}). Letting it go so its zone keeps working; it is tried again the next time it is created.");
				// Never reaches zero now, so rooms that still arrive do not spawn half a dungeon.
				__instance.m_roomsToLoad = int.MaxValue;
				__instance.ReleaseHeldReferences();
				return false;
			}
		}
	}
}
