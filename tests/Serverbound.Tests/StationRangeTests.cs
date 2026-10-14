using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using Serverbound;
using UnityEngine;

// Runs the game's real station update, range refresh, and upgrade search the way a dedicated server's
// frame does. Positions and the native radius setter have no scene to answer them, so they are fixtures.
internal static class StationRangeTests
{
    private const string Fixture = "Serverbound.Tests.StationRange";
    private static readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
    private static readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
    private static readonly List<(Collider Area, float Radius)> sized = new List<(Collider, float)>();
    private static uint id = 97000;

    internal static void Run(Action<string, Action> test)
    {
        test("station range: a dedicated server sizes a station's base area with its upgrades", () =>
        {
            var fixture = new Harmony(Fixture);
            var feature = new Harmony(Plugin.PluginGUID + ".StationRange");
            StationExtension[] loaded = StationExtension.m_allExtensions.ToArray();
            try
            {
                var scene = new HarmonyMethod(typeof(StationRangeTests), nameof(Scene));
                fixture.Patch(AccessTools.DeclaredMethod(typeof(CraftingStation), "GetExtensions"), transpiler: scene);
                fixture.Patch(AccessTools.DeclaredMethod(typeof(StationExtension), "FindExtensions"), transpiler: scene);
                // The plugin patches every nested type of an enabled feature.
                foreach (Type patch in typeof(Plugin).Assembly.GetType("Serverbound.Features.StationRange")?.GetNestedTypes() ?? Type.EmptyTypes)
                    new PatchClassProcessor(feature, patch).Patch();

                var area = Fake<CapsuleCollider>();
                CraftingStation bench = Station("$piece_workbench", Vector3.zero, range: 20, perUpgrade: 4, area);
                CraftingStation forge = Station("$piece_forge", new Vector3(2, 0, 2), range: 20, perUpgrade: 3, Fake<CapsuleCollider>());
                Upgrade(bench, "$piece_workbench_ext1", new Vector3(3, 0, 0));
                Upgrade(bench, "$piece_workbench_ext2", new Vector3(0, 0, 4));
                Upgrade(bench, "$piece_workbench_ext3", new Vector3(40, 0, 0));
                Upgrade(forge, "$piece_forge_ext1", new Vector3(1, 0, 0));

                Frame(bench);
                Check(sized.SequenceEqual(new[] { ((Collider)area, 28f) }), "two upgrades in reach: " + Sized());
                Frame(bench); Frame(bench); Frame(bench);
                Check(sized.Count == 1, "sized again inside the game's two-second interval: " + Sized());
                Upgrade(bench, "$piece_workbench_ext4", new Vector3(-2, 0, 0));
                Frame(bench);
                Check(sized.SequenceEqual(new[] { ((Collider)area, 28f), ((Collider)area, 32f) }), "a third upgrade built later: " + Sized());
            }
            finally
            {
                feature.UnpatchSelf(); fixture.UnpatchSelf();
                StationExtension.m_allExtensions.Clear(); StationExtension.m_allExtensions.AddRange(loaded);
                transforms.Clear(); positions.Clear(); sized.Clear();
            }
        });
    }

    // One server frame for a loaded station: the call MonoUpdaters makes, half a second apart.
    private static void Frame(CraftingStation station) => station.CustomUpdate(0.5f, 0f);

    // A station as CraftingStation.Start leaves it on a server.
    private static CraftingStation Station(string name, Vector3 position, float range, float perUpgrade, Collider area)
    {
        var station = Fake<CraftingStation>();
        station.m_name = name; station.m_rangeBuild = range; station.m_extraRangePerLevel = perUpgrade; station.m_effectAreaCollider = area;
        station.m_nview = Fake<ZNetView>();
        station.m_nview.m_zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
        station.m_attachedExtensions = new List<StationExtension>();
        station.m_updateExtensionTimer = 2f;
        Place(station, position);
        return station;
    }

    private static void Upgrade(CraftingStation station, string name, Vector3 position)
    {
        var upgrade = Fake<StationExtension>();
        upgrade.m_craftingStation = station; upgrade.m_maxStationDistance = 5f;
        upgrade.m_piece = Fake<Piece>(); upgrade.m_piece.m_name = name;
        Place(upgrade, position);
        StationExtension.m_allExtensions.Add(upgrade);
    }

    private static void Place(Component component, Vector3 position) { transforms[component] = Fake<Transform>(); positions[transforms[component]] = position; }
    private static string Sized() => sized.Count == 0 ? "none" : string.Join(", ", sized.Select(write => write.Radius));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Fake<T>() where T : UnityEngine.Object
    { var value = (T)FormatterServices.GetUninitializedObject(typeof(T)); AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id)); return value; }

    private static Transform TransformOf(Component component) => transforms[component];
    private static Vector3 PositionOf(Transform transform) => positions[transform];
    private static void Radius(CapsuleCollider area, float value) => sized.Add((area, value));
    private static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method)
            {
                string? replacement = method.DeclaringType == typeof(Component) && method.Name == "get_transform" ? nameof(TransformOf)
                    : method.DeclaringType == typeof(Transform) && method.Name == "get_position" ? nameof(PositionOf)
                    : method.DeclaringType == typeof(CapsuleCollider) && method.Name == "set_radius" ? nameof(Radius) : null;
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.DeclaredMethod(typeof(StationRangeTests), replacement);
                }
            }
            yield return instruction;
        }
    }
}
