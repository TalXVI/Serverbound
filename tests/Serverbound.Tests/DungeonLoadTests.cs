using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using Serverbound;
using Serverbound.Features;
using SoftReferenceableAssets;
using UnityEngine;

// Native bundle I/O is pending. Reference counting, cancellation callbacks and dungeon cleanup run unchanged.
internal static class DungeonLoadTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("unloading a pending dungeon releases each room once and preserves another consumer", () =>
        {
            var fixture = new Harmony("Serverbound.Tests.DungeonLoad.Scene");
            var feature = new Harmony("Serverbound.Tests.DungeonLoad.Feature");
            IAssetLoader previous = Runtime.s_assetLoader;
            AssetBundleLoader previousBundleLoader = AssetBundleLoader.Instance;
            try
            {
                Plugin.logger ??= new BepInEx.Logging.ManualLogSource("offline Serverbound");
                fixture.Patch(AccessTools.DeclaredMethod(typeof(DungeonLoadGuard.DungeonGenerator_OnRoomLoaded_Patch), "Prefix"),
                    transpiler: new HarmonyMethod(typeof(DungeonLoadTests), nameof(Scene)));
                feature.CreateClassProcessor(typeof(DungeonLoadGuard.DungeonGenerator_OnRoomLoaded_Patch)).Patch();

                var loader = (AssetBundleLoader)FormatterServices.GetUninitializedObject(typeof(AssetBundleLoader));
                Runtime.s_assetLoader = loader;
                AssetBundleLoader.Instance = loader;
                loader.Initialized = true;
                loader.m_bundleNameToLoaderIndex = new Dictionary<string, int> { ["rooms"] = 0 };
                loader.m_bundleLoaders = new[] { new BundleLoader("rooms", "rooms") };
                loader.m_bundleLoaders[0].SetDependencies(Array.Empty<string>());
                var first = new AssetID(0, 0, 0, 1);
                var second = new AssetID(0, 0, 0, 2);
                loader.m_assetIDToLoaderIndex = new Dictionary<AssetID, int> { [first] = 0, [second] = 1 };
                loader.m_assetLoaders = new[]
                {
                    new AssetLoader(first, new AssetLocation("rooms", "first.prefab")),
                    new AssetLoader(second, new AssetLocation("rooms", "second.prefab")),
                };
                var dungeon = (DungeonGenerator)FormatterServices.GetUninitializedObject(typeof(DungeonGenerator));
                AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(dungeon, new IntPtr(84001));
                dungeon.m_roomsToLoad = 2;
                dungeon.m_heldReferences = new List<IReferenceCounted>();
                foreach (AssetID assetID in new[] { first, second })
                {
                    var room = new SoftReference<GameObject>(assetID);
                    room.HoldReference();
                    dungeon.m_heldReferences.Add(room);
                    ref AssetLoader asset = ref loader.m_assetLoaders[loader.m_assetIDToLoaderIndex[assetID]];
                    asset.m_shouldBeLoaded = true;
                    asset.m_bundlesToLoad = 1;
                    asset.AddLoadedCallback(dungeon.OnRoomLoaded);
                }
                loader.IncrementReferenceCounter(second);

                dungeon.OnDestroy();

                Check(loader.m_assetLoaders[0].ReferenceCount == 0, "cancelled room reference count underflowed");
                Check(loader.m_assetLoaders[1].ReferenceCount == 1, "released another consumer's room reference");
                Check(loader.m_bundleLoaders[0].ReferenceCount == 1, "released another consumer's bundle reference");
                loader.Release(second);
                Check(loader.m_assetLoaders[1].ReferenceCount == 0 && loader.m_bundleLoaders[0].ReferenceCount == 0,
                    "the remaining consumer could not release its room and bundle");
            }
            finally
            {
                feature.UnpatchSelf();
                fixture.UnpatchSelf();
                Runtime.s_assetLoader = previous;
                AssetBundleLoader.Instance = previousBundleLoader;
            }
        });
        test("a failed native bundle load does not adopt another owner's bundle", () =>
        {
            var fixture = new Harmony("Serverbound.Tests.DungeonLoad.Native");
            var feature = new Harmony("Serverbound.Tests.DungeonLoad.FailedBundle");
            try
            {
                fixture.Patch(AccessTools.PropertyGetter(typeof(AssetBundleCreateRequestWrapper), "AssetBundle"),
                    prefix: new HarmonyMethod(typeof(DungeonLoadTests), nameof(MissingBundle)));
                fixture.Patch(AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.GetAllLoadedAssetBundles)),
                    prefix: new HarmonyMethod(typeof(DungeonLoadTests), nameof(LoadedBundles)));
                fixture.Patch(AccessTools.PropertyGetter(typeof(UnityEngine.Object), "name"),
                    prefix: new HarmonyMethod(typeof(DungeonLoadTests), nameof(BundleName)));
                feature.CreateClassProcessor(typeof(DungeonLoadGuard.BundleLoader_OnLoadAsyncCompleted_Patch)).Patch();
                var bundle = new BundleLoader("rooms", "rooms");
                bundle.m_shouldBeLoaded = true;
                bundle.m_asyncLoadRequest = (AssetBundleCreateRequestWrapper)FormatterServices.GetUninitializedObject(typeof(AssetBundleCreateRequestWrapper));

                bundle.OnLoadAsyncCompleted();

                Check(bundle.Bundle == null && bundle.LoadStatus == LoadStatus.NotLoaded,
                    "adopted a bundle owned by another loader after its own native request failed");
            }
            finally
            {
                feature.UnpatchSelf();
                fixture.UnpatchSelf();
            }
        });
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool MissingBundle(ref AssetBundle? __result) { __result = null; return false; }
    private static bool BundleName(ref string __result) { __result = "rooms"; return false; }
    private static bool LoadedBundles(ref IEnumerable<AssetBundle> __result)
    {
        var other = (AssetBundle)FormatterServices.GetUninitializedObject(typeof(AssetBundle));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(other, new IntPtr(84002));
        __result = new[] { other };
        return false;
    }
    private static Transform TransformOf(Component component) => null!;
    private static Vector3 PositionOf(Transform transform) => Vector3.zero;
    private static string NameOf(UnityEngine.Object value) => "pending dungeon";

    private static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method)
            {
                string? replacement = (method.DeclaringType.Name, method.Name) switch
                {
                    ("Component", "get_transform") => nameof(TransformOf),
                    ("Transform", "get_position") => nameof(PositionOf),
                    ("Object", "get_name") => nameof(NameOf),
                    _ => null,
                };
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.DeclaredMethod(typeof(DungeonLoadTests), replacement);
                }
            }
            yield return instruction;
        }
    }
}
