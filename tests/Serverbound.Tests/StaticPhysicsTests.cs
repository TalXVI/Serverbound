using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

internal static class StaticPhysicsTests
{
    private static readonly Vector3 Start = new Vector3(6400, 100, 6400);
    private static readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
    private static readonly Dictionary<Component, GameObject> objects = new Dictionary<Component, GameObject>();
    private static readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
    private static int id;

    internal static void Run(Action<string, Action> test)
    {
        test("static objects settle and publish their height around any ready player", () =>
        {
            var fixture = new Harmony("serverbound.tests.static-physics.fixture");
            var feature = new Harmony("serverbound.tests.static-physics.feature");
            ZNet oldNet = ZNet.m_instance;
            ZoneSystem oldZones = ZoneSystem.s_instance;
            ZNetScene oldScene = ZNetScene.s_instance;
            try
            {
                ZNet.m_instance = Fake<ZNet>();
                ZNet.m_instance.m_referencePosition = Vector3.zero;
                ZoneSystem.s_instance = Fake<ZoneSystem>();
                ZoneSystem.s_instance.m_zoneSize = 64f;
                ZNetScene.s_instance = Fake<ZNetScene>();

                // Supply scene inputs and native operations. Keep the game's area gate,
                // falling logic, ownership guard, and ZDO position write executable.
                var scene = new HarmonyMethod(typeof(StaticPhysicsTests), nameof(Scene));
                foreach (string method in new[] { "SUpdate", "CheckFall", "GetFallHeight", "Fall", "FallUpdate", "StopFalling", "PushUp" })
                    fixture.Patch(AccessTools.DeclaredMethod(typeof(StaticPhysics), method), transpiler: scene);
                Patch(fixture, typeof(ZNet), "GetSyncedSimulationDistance", nameof(Distance));
                Patch(fixture, typeof(ZNetView), "IsOwner", nameof(Owner));
                Patch(fixture, typeof(ZDO), "SetSector", nameof(Skip));
                Patch(fixture, typeof(ZDO), "IncreaseDataRevision", nameof(Skip));
                feature.CreateClassProcessor(typeof(Serverbound.Features.Core.ZNetScene_OutsideActiveArea_Patch)).Patch();

                ZNetPeer ready = Peer(42, Start);
                ZNetPeer distant = Peer(43, Vector3.zero);
                ZNetPeer unready = Peer(0, Start);
                foreach (var sample in new[]
                {
                    (peers: new[] { ready }, fall: true, height: 0f, label: "remote ready player"),
                    (peers: new[] { unready, distant, ready }, fall: true, height: 0f, label: "second ready player's area"),
                    (peers: new[] { distant }, fall: true, height: 100f, label: "outside all ready players"),
                    (peers: new[] { unready }, fall: true, height: 100f, label: "unready player"),
                    (peers: Array.Empty<ZNetPeer>(), fall: true, height: 100f, label: "no players"),
                    (peers: new[] { ready }, fall: false, height: 100f, label: "fall-disabled object"),
                })
                {
                    AccessTools.Field(typeof(ZNet), "m_peers").SetValue(ZNet.m_instance, new List<ZNetPeer>(sample.peers));
                    StaticPhysics physics = Fake<StaticPhysics>();
                    Transform transform = Fake<Transform>();
                    transforms[physics] = transform;
                    objects[physics] = Fake<GameObject>();
                    positions[transform] = Start;
                    physics.m_fall = sample.fall;
                    physics.m_pushUp = true;
                    physics.m_updateTime = -1f;
                    physics.m_nview = Fake<ZNetView>();
                    ZDO zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
                    AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
                    zdo.m_position = Start;
                    physics.m_nview.m_zdo = zdo;

                    physics.SUpdate(1f, ZoneSystem.GetZone(ZNet.m_instance.GetReferencePosition()));
                    int steps = 0;
                    while (physics.IsFalling)
                    {
                        Check(++steps <= 501, "falling did not stop");
                        physics.FallUpdate();
                    }
                    Check(positions[transform].y == sample.height, sample.label + ": tree did not settle at the expected height");
                    Check(zdo.GetPosition().y == sample.height, sample.label + ": owner did not publish the settled height");
                    Check(ZNetScene.s_instance.OutsideActiveArea(Start) == (sample.peers.Length == 0 || Array.IndexOf(sample.peers, ready) < 0),
                        sample.label + ": one-argument caller disagrees with the ready-player area");
                    Check(!ZNetScene.InActiveArea(Start, ZoneSystem.GetZone(Vector3.zero)), "per-player geometry became global");
                }
            }
            finally
            {
                feature.UnpatchSelf();
                fixture.UnpatchSelf();
                ZNet.m_instance = oldNet;
                ZoneSystem.s_instance = oldZones;
                ZNetScene.s_instance = oldScene;
                transforms.Clear(); objects.Clear(); positions.Clear();
            }
        });
    }

    private static void Patch(Harmony harmony, Type type, string method, string prefix) =>
        harmony.Patch(AccessTools.DeclaredMethod(type, method), prefix: new HarmonyMethod(typeof(StaticPhysicsTests), prefix));
    private static ZNetPeer Peer(long uid, Vector3 position)
    {
        var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_uid = uid; peer.m_refPos = position;
        return peer;
    }
    private static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id));
        return value;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Skip() => false;
    private static bool Owner(ref bool __result) { __result = true; return false; }
    private static bool Distance(ref SimulationDistance __result) { __result = SimulationDistance.OriginalDistance; return false; }
    private static Transform TransformOf(Component component) => transforms[component];
    private static GameObject ObjectOf(Component component) => objects[component];
    private static Vector3 PositionOf(Transform transform) => positions[transform];
    private static void PositionTo(Transform transform, Vector3 value) => positions[transform] = value;
    private static bool Ground(ZoneSystem zones, Vector3 point, out float height) { height = 0f; return true; }
    private static void Static(GameObject obj, bool value) { }
    private static void Schedule(MonoBehaviour component, string method, float time, float interval) => Check(method == "FallUpdate", "unexpected repeating callback");
    private static void Cancel(MonoBehaviour component, string method) => Check(method == "FallUpdate", "unexpected cancelled callback");

    private static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method)
            {
                string? name = (method.DeclaringType.Name, method.Name) switch
                {
                    ("Component", "get_transform") => nameof(TransformOf),
                    ("Component", "get_gameObject") => nameof(ObjectOf),
                    ("Transform", "get_position") => nameof(PositionOf),
                    ("Transform", "set_position") => nameof(PositionTo),
                    ("GameObject", "set_isStatic") => nameof(Static),
                    ("MonoBehaviour", "InvokeRepeating") => nameof(Schedule),
                    ("MonoBehaviour", "CancelInvoke") when method.GetParameters().Length == 1 => nameof(Cancel),
                    ("ZoneSystem", "GetGroundHeight") => nameof(Ground),
                    _ => null,
                };
                if (name != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.DeclaredMethod(typeof(StaticPhysicsTests), name);
                }
            }
            yield return instruction;
        }
    }
}
