using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using Serverbound;
using UnityEngine;

// Runs the game's real smoke spawn and the fire update hook the way a dedicated server's frame does.
// Transforms, physics queries, components and instantiation have no scene to answer them, so they are fixtures.
internal static class FireTests
{
    private const string Fixture = "Serverbound.Tests.Fires";
    private static readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
    private static readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
    private static readonly Dictionary<(Component, Type), Component> components = new Dictionary<(Component, Type), Component>();
    private static readonly Dictionary<Component, GameObject> objects = new Dictionary<Component, GameObject>();
    private static readonly Dictionary<Collider, float> surfaces = new Dictionary<Collider, float>();
    private static readonly HashSet<Collider> triggers = new HashSet<Collider>();
    private static readonly HashSet<(Transform Child, Transform Parent)> children = new HashSet<(Transform, Transform)>();
    private static readonly List<Collider> touching = new List<Collider>();
    private static readonly List<Vector3> puffs = new List<Vector3>();
    private static bool blocked;
    private static Vector3 lastFire;
    private static uint id = 98000;

    internal static void Run(Action<string, Action> test)
    {
        test("server smoke: smoke sources puff only for a player within 64 m, under the game's cap", () =>
        {
            var fixture = new Harmony(Fixture);
            var feature = new Harmony(Plugin.PluginGUID + ".ServerSmoke");
            List<Player> loaded = Player.s_players.ToList();
            List<Smoke> smoke = Smoke.s_smoke.ToList();
            try
            {
                Plugin.logger ??= new BepInEx.Logging.ManualLogSource("offline Serverbound");
                var scene = new HarmonyMethod(typeof(FireTests), nameof(Scene));
                fixture.Patch(AccessTools.DeclaredMethod(typeof(SmokeSpawner), "Spawn"), transpiler: scene);
                fixture.Patch(AccessTools.DeclaredMethod(typeof(SmokeSpawner), "TestBlocked"), prefix: new HarmonyMethod(typeof(FireTests), nameof(TestBlocked)));
                fixture.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.GetClosestPlayer)), transpiler: scene);
                Type features = typeof(Plugin).Assembly.GetType("Serverbound.Features.ServerSmoke", true);
                fixture.Patch(AccessTools.DeclaredMethod(features.GetNestedType("SmokeSpawner_Spawn_Patch"), "Watcher"), transpiler: scene);
                foreach (Type patch in features.GetNestedTypes()) new PatchClassProcessor(feature, patch).Patch();
                Check(!PatchProcessor.GetCurrentInstructions(AccessTools.DeclaredMethod(typeof(SmokeSpawner), "Spawn"))
                    .Any(c => c.opcode == OpCodes.Ldsfld && c.operand is FieldInfo f && f.Name == "m_localPlayer"), "local player read retained");

                Player.s_players.Clear(); Smoke.s_smoke.Clear();
                var source = Fake<SmokeSpawner>();
                source.m_smokePrefab = Fake<GameObject>();
                Place(source, new Vector3(100, 30, 100));

                source.Spawn(10f);
                Check(puffs.Count == 0 && source.m_lastSpawnTime == 10f, "smoke with nobody on the server");
                Player far = Fake<Player>(); Place(far, new Vector3(100, 30, 166)); Player.s_players.Add(far);
                source.Spawn(11f);
                Check(puffs.Count == 0 && source.m_lastSpawnTime == 11f, "smoke for a player 66 m away");
                Player near = Fake<Player>(); Place(near, new Vector3(130, 30, 100)); Player.s_players.Add(near);
                source.Spawn(12f);
                Check(puffs.SequenceEqual(new[] { new Vector3(100, 30, 100) }) && source.m_lastSpawnTime == 12f, "no smoke for a player 30 m away");
                blocked = true;
                source.Spawn(13f);
                Check(puffs.Count == 1 && source.m_lastSpawnTime == 12f, "a blocked source still counted as puffing");
                blocked = false;

                for (int i = 0; i < 150; i++) Smoke.s_smoke.Add(Puff());
                source.Spawn(14f);
                Check(Smoke.GetTotalSmoke() == 149, "the game's cap of 100 puffs not applied");
            }
            finally
            {
                feature.UnpatchSelf(); fixture.UnpatchSelf();
                Player.s_players.Clear(); Player.s_players.AddRange(loaded);
                Smoke.s_smoke.Clear(); Smoke.s_smoke.AddRange(smoke);
                Reset();
            }
        });

        test("handed-over fires: the server finds what a fire it took over burns on, once", () =>
        {
            var fixture = new Harmony(Fixture);
            var feature = new Harmony(Plugin.PluginGUID + ".HandedOverFires");
            try
            {
                var scene = new HarmonyMethod(typeof(FireTests), nameof(Scene));
                Type features = typeof(Plugin).Assembly.GetType("Serverbound.Features.HandedOverFires", true);
                fixture.Patch(AccessTools.DeclaredMethod(features.GetNestedType("Fire_UpdateFire_Patch"), "Postfix"), transpiler: scene);
                fixture.Patch(AccessTools.DeclaredMethod(features, "BurningOn"), transpiler: scene);
                fixture.Patch(AccessTools.DeclaredMethod(features, "Burnable"), transpiler: scene);
                // The game's own update needs a scene; only the hook after it runs.
                fixture.Patch(AccessTools.DeclaredMethod(typeof(Fire), "UpdateFire"), prefix: new HarmonyMethod(typeof(FireTests), nameof(Skip)));
                foreach (Type patch in features.GetNestedTypes()) new PatchClassProcessor(feature, patch).Patch();

                (Fire fire, CinderSpawner spawner) = Burning(Vector3.zero, owned: false);
                // The physics query returns colliders in no particular order: an angled beam whose
                // bounding box holds the flame comes back ahead of the support the fire burns on.
                Touching(Piece(burnable: true), 0.32f);
                Touching(Piece(burnable: false), 0.08f);
                triggers.Add(Touching(Piece(burnable: true), 0.03f));
                Collider own = Touching(Piece(burnable: true), 0f);
                children.Add((transforms[own], transforms[fire]));
                Collider support = Touching(Piece(burnable: true), 0.12f);

                fire.UpdateFire();
                Check(!spawner.m_hasAttachObj, "a player's fire changed on the server");
                Own(fire);
                fire.UpdateFire();
                Check(spawner.m_hasAttachObj && spawner.m_attachObj == objects[support], "not attached to the nearest burnable piece");

                touching.Clear();
                (Fire far, CinderSpawner farSpawner) = Burning(new Vector3(-5, 0, 0), owned: true);
                Touching(Piece(burnable: true), 0.55f);
                far.UpdateFire();
                Check(!farSpawner.m_hasAttachObj, "attached to a piece the flame does not touch");

                touching.Clear();
                (Fire log, CinderSpawner logSpawner) = Burning(new Vector3(5, 0, 0), owned: true);
                Collider trunk = Touching(Fake<CapsuleCollider>(), 0.25f);
                components[(trunk, typeof(TreeLog))] = Fake<TreeLog>();
                log.UpdateFire();
                Check(logSpawner.m_attachObj == objects[trunk], "not attached to a log");

                touching.Clear();
                (Fire ground, CinderSpawner groundSpawner) = Burning(new Vector3(10, 0, 0), owned: true);
                ground.UpdateFire();
                Touching(Piece(burnable: true), 0.1f);
                ground.UpdateFire();
                Check(!groundSpawner.m_hasAttachObj, "searched again after the first update");

                (Fire lit, CinderSpawner litSpawner) = Burning(new Vector3(15, 0, 0), owned: true);
                GameObject grass = Fake<GameObject>();
                litSpawner.m_attachObj = grass; litSpawner.m_hasAttachObj = true;
                lit.UpdateFire();
                Check(litSpawner.m_attachObj == grass, "replaced what the server's own spark set");
            }
            finally
            {
                feature.UnpatchSelf(); fixture.UnpatchSelf();
                Reset();
            }
        });
    }

    private static (Fire, CinderSpawner) Burning(Vector3 position, bool owned)
    {
        var fire = Fake<Fire>();
        fire.m_nview = Fake<ZNetView>();
        fire.m_nview.m_zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
        var spawner = Fake<CinderSpawner>();
        components[(fire, typeof(CinderSpawner))] = spawner;
        Place(fire, position);
        lastFire = position;
        if (owned) Own(fire);
        return (fire, spawner);
    }

    private static void Own(Fire fire) => AccessTools.Property(typeof(ZDO), "Owner").SetValue(fire.m_nview.m_zdo, true);

    private static Collider Piece(bool burnable)
    {
        var collider = Fake<BoxCollider>();
        var piece = Fake<WearNTear>(); piece.m_burnable = burnable;
        components[(collider, typeof(WearNTear))] = piece;
        return collider;
    }

    // A collider whose surface is this far from the fire most recently made.
    private static Collider Touching(Collider collider, float distance)
    {
        surfaces[collider] = distance;
        objects[collider] = Fake<GameObject>();
        Place(collider, lastFire);
        touching.Add(collider);
        return collider;
    }

    private static Smoke Puff()
    {
        var puff = Fake<Smoke>();
        puff.m_added = true; puff.m_fadeTimer = -1f;
        return puff;
    }

    private static void Reset()
    {
        transforms.Clear(); positions.Clear(); components.Clear(); objects.Clear(); surfaces.Clear();
        triggers.Clear(); children.Clear(); touching.Clear(); puffs.Clear(); blocked = false;
    }

    private static void Place(Component component, Vector3 position) { transforms[component] = Fake<Transform>(); positions[transforms[component]] = position; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Fake<T>() where T : UnityEngine.Object
    { var value = (T)FormatterServices.GetUninitializedObject(typeof(T)); AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id)); return value; }

    private static bool Skip() => false;
    private static bool TestBlocked(ref bool __result) { __result = blocked; return false; }

    private static Transform TransformOf(Component component) => transforms[component];
    private static Vector3 PositionOf(Transform transform) => positions[transform];
    private static GameObject GameObjectOf(Component component) => objects[component];
    private static T ComponentOf<T>(Component component) where T : Component => components.TryGetValue((component, typeof(T)), out Component found) ? (T)found : null!;
    private static bool ChildOf(Transform child, Transform parent) => children.Contains((child, parent));
    private static int Mask(string[] layers) => 1;
    private static Quaternion Rotation() => Quaternion.identity;
    private static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : UnityEngine.Object { puffs.Add(position); return original; }
    private static int Overlap(Vector3 position, float radius, Collider[] results, int mask, QueryTriggerInteraction query)
    {
        int count = 0;
        foreach (Collider collider in touching)
        {
            if (count == results.Length) break;
            if (surfaces[collider] <= radius && !(query == QueryTriggerInteraction.Ignore && triggers.Contains(collider))) results[count++] = collider;
        }
        return count;
    }

    private static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method)
            {
                MethodInfo? replacement = Replacement(method);
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
            }
            yield return instruction;
        }
    }

    private static MethodInfo? Replacement(MethodInfo method)
    {
        Type type = method.DeclaringType;
        string? name = (type.Name, method.Name) switch
        {
            ("Component", "get_transform") => nameof(TransformOf),
            ("Component", "get_gameObject") => nameof(GameObjectOf),
            ("Component", "GetComponent") when method.IsGenericMethod => nameof(ComponentOf),
            ("Component", "GetComponentInParent") when method.IsGenericMethod && method.GetParameters().Length == 0 => nameof(ComponentOf),
            ("Transform", "get_position") => nameof(PositionOf),
            ("Transform", "IsChildOf") => nameof(ChildOf),
            ("Physics", "OverlapSphereNonAlloc") when method.GetParameters().Length == 5 => nameof(Overlap),
            ("LayerMask", "GetMask") => nameof(Mask),
            ("Random", "get_rotation") => nameof(Rotation),
            ("Object", "Instantiate") when method.IsGenericMethod && method.GetParameters().Length == 3 => nameof(Instantiate),
            _ => null,
        };
        if (name == null) return null;
        MethodInfo fake = AccessTools.DeclaredMethod(typeof(FireTests), name);
        return method.IsGenericMethod ? fake.MakeGenericMethod(method.GetGenericArguments()) : fake;
    }
}
