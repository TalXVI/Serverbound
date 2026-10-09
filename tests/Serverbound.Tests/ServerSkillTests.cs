using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

// Real vendor calculations and replicated ZDO data run here. Only native scene
// access, object creation, coroutine scheduling and network delivery are fixtures.
internal static class ServerSkillTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<IntPtr> pointers = new List<IntPtr>();
    private static readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
    private static readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
    private static readonly Dictionary<Component, Character> characters = new Dictionary<Component, Character>();
    private static readonly Dictionary<Component, Ship> ships = new Dictionary<Component, Ship>();
    private static readonly List<ItemDrop> spawned = new List<ItemDrop>();
    private static readonly List<(long Peer, string Name, ZPackage Data)> grants = new List<(long, string, ZPackage)>();
    private static readonly List<(long Peer, bool Active)> statusReplies = new List<(long, bool)>();
    private static readonly List<Dictionary<GameObject, int>> miningDrops = new List<Dictionary<GameObject, int>>();
    private static Type skills = null!;
    private static Type config = null!;
    private static Assembly impact = null!;
    private static ConfigFile cfg = null!;
    private static readonly Dictionary<string, ConfigEntry<bool>> switches = new Dictionary<string, ConfigEntry<bool>>();
    private static ItemDrop prefabItem = null!;
    private static Player? attacker;
    private static float health;
    private static int sequence;
    private static Skills.SkillType Animal => (Skills.SkillType)AccessTools.Field(impact.GetType("ImpactfulSkills.patches.AnimalWhisper", true), "AnimalHandling").GetValue(null);

    internal static void Run(Assembly assembly, Type skillPatch, Action<string, Action> test, Action initialize)
    {
        impact = assembly; skills = skillPatch; config = impact.GetType("ImpactfulSkills.ValConfig", true)!;
        void Case(string name, Action action) => test("server skills " + name, () =>
        {
            initialize();
            try { Setup(); action(); }
            finally
            {
                new Harmony("Serverbound.Tests.SkillScene").UnpatchSelf();
                foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
                pointers.Clear(); transforms.Clear(); positions.Clear(); characters.Clear(); ships.Clear();
                spawned.Clear(); grants.Clear(); statusReplies.Clear(); miningDrops.Clear(); produced.Clear(); Player.m_localPlayer = null;
                AccessTools.Method(skills, "Reset").Invoke(null, null);
            }
        });
        Case("reads each remote player's actual replicated factors, including zero", () =>
        {
            Player first = NewPlayer(101, 0), second = NewPlayer(102, 1);
            Check(Factor(first, Skills.SkillType.Pickaxes) == 0 && Factor(second, Skills.SkillType.Pickaxes) == 1, "wrong actor factors");
            ZDO zdo = View(second).GetZDO(); Set(zdo, "Serverbound.woodcutting", 0.25f);
            Check(Factor(second, Skills.SkillType.WoodCutting) == 0.25f, "wrong skill key");
            Set(zdo, "Serverbound.animalHandling", float.NaN);
            Reject(() => Factor(second, Animal));
            Set(zdo, "Serverbound.skillProtocol", 2);
            Reject(() => Factor(second, Skills.SkillType.Pickaxes));
        });
        Case("handshake replies only to a connected ready peer and reports disabled Core", () =>
        {
            var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
            peer.m_uid = 321;
            ZRoutedRpc.instance.m_peers.Add(peer);
            Call("Hello", 0L); Call("Hello", 999L);
            Check(statusReplies.Count == 0, "unknown sender received server status");
            Call("Hello", 321L);
            Check(statusReplies.Count == 1 && statusReplies[0] == (321L, true), "ready peer did not receive active status");
            Serverbound.Compatibility.SimulationPatch.Deactivate();
            Call("Hello", 321L);
            Check(statusReplies.Count == 2 && statusReplies[1] == (321L, false), "disabled Core advertised an active bridge");
        });
        Case("nested and exceptional actor scopes restore the previous player", () =>
        {
            Player first = NewPlayer(103, 0.25f), second = NewPlayer(104, 0.75f);
            Check(Actor() == null, "server local player substituted");
            using (Scope(first))
            {
                Check(Actor() == first, "outer scope");
                try { using (Scope(second)) { Check(Actor() == second, "inner scope"); throw new InvalidOperationException(); } }
                catch (InvalidOperationException) { }
                Check(Actor() == first, "exception contaminated scope");
            }
            Check(Actor() == null, "actor retained after scope");
        });
        Case("missing skill data lets damage proceed without a bonus actor", () =>
        {
            attacker = NewPlayer(105, 0.5f);
            Set(View(attacker).GetZDO(), "Serverbound.skillProtocol", 0);
            var rock = Fake<MineRock5>(); var hit = new HitData();
            object?[] args = { rock, hit, null };
            Call("BeginDamage", args);
            using ((IDisposable)args[2]!) Check(Actor() == null, "unready player selected as bonus actor");
            switches["EnableMining"].Value = false;
            args = new object?[] { rock, hit, null };
            Call("BeginDamage", args);
            using ((IDisposable)args[2]!) Check(Actor() == attacker, "disabled feature interfered");
        });
        Case("server mining notifications pay a bonus only once per destroyed area", () =>
        {
            var rock = Fake<MineRock5>(); ZNetView view = Fake<ZNetView>();
            ZDO zdo = Zdo(500); zdo.SetOwnerInternal(100); AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
            AccessTools.Field(typeof(MineRock5), "m_nview").SetValue(rock, view);
            using (Scope(NewPlayer(106, 1)))
            {
                Check(!(bool)Call("OncePerMiningArea", rock, 3, 5f)!, "living area paid bonus");
                Check((bool)Call("OncePerMiningArea", rock, 3, 0f)!, "first destruction omitted");
                Check(!(bool)Call("OncePerMiningArea", rock, 3, 0f)!, "duplicate destruction paid bonus");
                Check((bool)Call("OncePerMiningArea", rock, 4, -1f)!, "different area suppressed");
            }
            Check(!(bool)Call("OncePerMiningArea", rock, 5, 0f)!, "environmental destruction credited");
        });
        Case("real taming calculation keeps the configured formula and routes XP to its actor", () =>
        {
            var tame = Fake<Tameable>(); Position(tame, Vector3.zero);
            MethodInfo method = Vendor("AnimalWhisper+IncreaseTamingSpeed", "Prefix");
            foreach (float factor in new[] { 0f, 0.5f, 1f })
            {
                Player player = NewPlayer(107 + grants.Count, factor); Position(player, Vector3.zero);
                using (Scope(player))
                {
                    object[] args = { tame, 10f }; method.Invoke(null, args);
                    Check((float)args[1] == 10f * (1 + 3 * factor), "taming formula changed");
                }
                var grant = grants.Last();
                Check(grant.Peer == player.GetOwner() && grant.Name == "Serverbound.OwnerSkillXP", "wrong XP recipient");
                grant.Data.SetPos(0);
                Check(grant.Data.ReadZDOID() == player.GetZDOID() && grant.Data.ReadInt() == (int)Animal && grant.Data.ReadSingle() == 1f, "wrong XP payload");
            }
        });
        Case("real tamed loot scaling uses the selected skill without stacking nearby players", () =>
        {
            var drop = Fake<CharacterDrop>(); Character character = Fake<Character>();
            characters[drop] = character; AccessTools.Field(typeof(Character), "m_tamed").SetValue(character, true);
            var item = Fake<GameObject>(); MethodInfo method = Vendor("AnimalWhisper+ScaleTamedAnimalLoot", "Postfix");
            foreach (float factor in new[] { 0f, 0.5f, 1f })
            {
                Player player = NewPlayer(120 + (int)(factor * 2), factor);
                var list = new List<KeyValuePair<GameObject, int>> { new KeyValuePair<GameObject, int>(item, 8) };
                using (Scope(player)) method.Invoke(null, new object[] { drop, list });
                Check(list[0].Value == 8 + (int)(8 * 0.25f * factor), "loot formula changed");
            }
        });
        Case("real mining and wood helpers retain configured yield at zero, half and full skill", () =>
        {
            var prefab = Fake<GameObject>(); prefabItem = Fake<ItemDrop>();
            prefabItem.m_itemData = new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData { m_maxStackSize = 50 } };
            var drops = new DropTable { m_dropMin = 40, m_dropMax = 40, m_dropChance = 1,
                m_drops = new List<DropTable.DropData> { new DropTable.DropData { m_item = prefab, m_stackMin = 1, m_stackMax = 1, m_weight = 1 } } };
            foreach (float factor in new[] { 0f, 0.5f, 1f })
            {
                Player player = NewPlayer(130 + (int)(factor * 2), factor);
                spawned.Clear(); miningDrops.Clear();
                using (Scope(player))
                {
                    Vendor("Mining", "IncreaseMiningDrops").Invoke(null, new object?[] { drops, Vector3.zero, null });
                    Vendor("Woodcutting", "IncreaseWoodDrops").Invoke(null, new object[] { drops, Vector3.zero });
                }
                int expected = (int)(40 * 0.5f * factor);
                Check(miningDrops.Sum(d => d.Values.Sum()) == expected, "mining yield changed");
                Check(spawned.Sum(i => i.m_itemData.m_stack) == expected, "wood yield changed");
            }
        });
        Case("wild and tamed loot drop without a bonus actor when skill state is missing", () =>
        {
            var drop = Fake<CharacterDrop>(); Character character = Fake<Character>(); characters[drop] = character;
            AccessTools.Field(typeof(CharacterDrop), "m_character").SetValue(drop, character);
            drop.m_drops = new List<CharacterDrop.Drop>();
            Player unready = NewPlayer(140, 0.5f); Set(View(unready).GetZDO(), "Serverbound.skillProtocol", 0);
            AccessTools.Field(typeof(Player), "s_players").SetValue(null, new List<Player> { unready });
            Check(drop.GenerateDropList() != null, "wild loot skipped");
            AccessTools.Field(typeof(Character), "m_tamed").SetValue(character, true);
            Check(drop.GenerateDropList() != null, "tamed loot skipped");
            object?[] args = { drop, null };
            Call("BeginLoot", args);
            using ((IDisposable)args[1]!) Check(Actor() == null, "unready player selected for tamed loot");
        });
        Case("server any-biome flags use the most skilled nearby player's replicated level", () =>
        {
            Bind("EnableFarmingBiomeUnrestricted", true); Bind("EnableBeeBiomeUnrestricted", true);
            foreach (var entry in new[] { ("FarmingBiomeUnrestrictedLevel", 50), ("BeeBiomeUnrestrictedLevel", 25) })
                AccessTools.Field(config, entry.Item1).SetValue(null, cfg.Bind("test", entry.Item1, entry.Item2));
            Player novice = Levels(NewPlayer(150, 0), 40, 20, 0), expert = Levels(NewPlayer(151, 0), 60, 30, 0), distant = Levels(NewPlayer(152, 0), 100, 100, 0);
            Position(novice, Vector3.zero); Position(expert, new Vector3(10, 0, 0)); Position(distant, new Vector3(100, 0, 0));
            MethodInfo plantRule = Vendor("PlantBiome", "IsBiomeUnrestricted"), hiveRule = Vendor("AnimalWhisper+BehivesInAnyBiome", "Prefix");
            foreach (bool skilled in new[] { false, true })
            {
                AccessTools.Field(typeof(Player), "s_players").SetValue(null, skilled ? new List<Player> { novice, expert, distant } : new List<Player> { novice, distant });
                Plant plant = Fake<Plant>(); AccessTools.Field(typeof(Plant), "m_nview").SetValue(plant, OwnedView(501));
                object?[] args = { plant, null };
                Call("BeginPlant", args);
                using ((IDisposable)args[1]!) Check((bool)plantRule.Invoke(null, new object[] { plant }) == skilled, "plant rule used the wrong player");
                Check(View(plant).GetZDO().GetBool("IS_ANYBIOME_PLANT") == skilled, "plant flag");
                Beehive hive = Fake<Beehive>(); AccessTools.Field(typeof(Beehive), "m_nview").SetValue(hive, OwnedView(502));
                args = new object?[] { hive, null };
                Call("BeginHive", args);
                using ((IDisposable)args[1]!)
                {
                    object[] call = { hive, false };
                    Check((bool)hiveRule.Invoke(null, call) == !skilled && (bool)call[1] == skilled, "hive rule used the wrong player");
                }
                Check(View(hive).GetZDO().GetBool("IS_BHIVE") == skilled, "hive flag");
            }
        });
        Case("server boat damage reduction uses the most skilled aboard player's replicated level", () =>
        {
            Bind("EnableBoatDamageReduction", true);
            AccessTools.Field(config, "BoatDamageReductionLevel").SetValue(null, cfg.Bind("test", "BoatDamageReductionLevel", 35));
            AccessTools.Field(config, "VoyagerDamageReductionAmount").SetValue(null, cfg.Bind("test", "VoyagerDamageReductionAmount", 0.5f));
            Player low = Levels(NewPlayer(160, 0), 0, 0, 20), high = Levels(NewPlayer(161, 0), 0, 0, 80), unready = NewPlayer(162, 0);
            Set(View(unready).GetZDO(), "Serverbound.level.voyaging", 100f); Set(View(unready).GetZDO(), "Serverbound.skillProtocol", 0);
            MethodInfo reduction = Vendor("Voyaging+ShipDamageReduction", "Prefix");
            foreach ((List<Player> aboard, float expected) in new[] { (new List<Player>(), 100f), (new List<Player> { low, unready }, 100f), (new List<Player> { low, high, unready }, 60f) })
            {
                WearNTear hull = Fake<WearNTear>(); hull.m_materialType = WearNTear.MaterialType.Wood;
                Ship ship = Fake<Ship>(); AccessTools.Field(typeof(Ship), "m_players").SetValue(ship, aboard); ships[hull] = ship;
                var hit = new HitData(); hit.m_damage.m_blunt = 100;
                object?[] args = { hull, null };
                Call("BeginShipDamage", args);
                using ((IDisposable)args[1]!) { object[] call = { hull, hit }; reduction.Invoke(null, call); hit = (HitData)call[1]; }
                Check(Mathf.Approximately(hit.m_damage.m_blunt, expected), $"boat damage {hit.m_damage.m_blunt}, expected {expected}");
            }
        });
    }

    private static void Setup()
    {
        cfg = new ConfigFile(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/scene-never-written.cfg"), false) { SaveOnConfigSet = false };
        switches.Clear(); health = 0; attacker = null;
        foreach (string key in new[] { "EnableAnimalWhisper", "EnableMining", "EnableWoodcutting", "FractionalDropsAsChance", "AnimalHandlingFractionalDropsAsChance", "SkipNonRockDropIncreases" })
            Bind(key, true);
        foreach (string key in new[] { "SkillLevelBonusEnabledForMiningDropChance", "ReducedChanceDropsForLowAmountDrops" }) Bind(key, false);
        foreach (var entry in new[] { ("AnimalTamingSpeedFactor", 3f), ("AnimalTamingSkillGainRate", 1f), ("TamedAnimalLootIncreaseFactor", 1.25f),
            ("AnimalHandlingLootRange", 20f), ("DistanceMiningDropMultiplierChecks", 20f), ("MiningLootFactor", 1.5f), ("WoodCuttingLootFactor", 1.5f) })
            AccessTools.Field(config, entry.Item1).SetValue(null, cfg.Bind("test", entry.Item1, entry.Item2));
        AccessTools.Field(impact.GetType("ImpactfulSkills.patches.AnimalWhisper", true), "AnimalHandling").SetValue(null, (Skills.SkillType)991);
        AccessTools.Field(impact.GetType("ImpactfulSkills.patches.Voyaging", true), "VoyagingSkill").SetValue(null, (Skills.SkillType)992);
        var fixture = new Harmony("Serverbound.Tests.SkillScene");
        Hook(fixture, typeof(ZDOMan), "GetSessionID", nameof(Session));
        Hook(fixture, typeof(ZDO), "IncreaseDataRevision", nameof(Skip));
        Hook(fixture, typeof(Component), "get_transform", nameof(Transform));
        Hook(fixture, typeof(Transform), "get_position", nameof(ReadPosition));
        Hook(fixture, typeof(UnityEngine.Object), "get_name", nameof(Name));
        Hook(fixture, typeof(Character), "IsTamed", nameof(Tamed));
        Hook(fixture, typeof(Character), "GetHealth", nameof(Health));
        Hook(fixture, typeof(HitData), "GetAttacker", nameof(Attacker));
        fixture.Patch(AccessTools.Method(typeof(ZRoutedRpc), "InvokeRoutedRPC", new[] { typeof(long), typeof(string), typeof(object[]) }), prefix: Hook(nameof(Grant)));
        fixture.Patch(AccessTools.Method(skills, "RunCoroutine"), prefix: Hook(nameof(Coroutine)));
        foreach (MethodInfo method in new[] { Vendor("AnimalWhisper+ScaleTamedAnimalLoot", "Postfix"), Vendor("Woodcutting", "IncreaseWoodDrops"), AccessTools.Method(skills, "BeginLoot"), AccessTools.Method(skills, "BeginSlaughter"),
            AccessTools.Method(skills, "BeginShipDamage"), Vendor("Voyaging+ShipDamageReduction", "Prefix") })
            fixture.Patch(method, transpiler: Hook(nameof(NativeComponents)));
        MethodInfo instantiate = AccessTools.GetDeclaredMethods(typeof(UnityEngine.Object)).Single(m => m.Name == "Instantiate" && !m.IsGenericMethod
            && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) }));
        fixture.Patch(instantiate, prefix: Hook(nameof(Instantiate)));
        var rpc = new ZRoutedRpc(true); rpc.SetUID(100);
    }
    private static void Bind(string key, bool value)
    { var entry = cfg.Bind("test", key, value); switches[key] = entry; AccessTools.Field(config, key).SetValue(null, entry); }
    private static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long)); pointers.Add(pointer); Marshal.WriteInt32(pointer, ++sequence);
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, pointer); return value;
    }
    private static ZDO Zdo(long uid) { var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO)); zdo.m_uid = new ZDOID(uid, (uint)++sequence); return zdo; }
    private static Player NewPlayer(long uid, float factor)
    {
        var player = Fake<Player>(); var view = Fake<ZNetView>(); ZDO zdo = Zdo(uid); zdo.SetOwnerInternal(uid);
        AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo); AccessTools.Field(typeof(Character), "m_nview").SetValue(player, view);
        Set(zdo, "Serverbound.skillProtocol", 1);
        foreach (string key in new[] { "Serverbound.mining", "Serverbound.woodcutting", "Serverbound.animalHandling" }) Set(zdo, key, factor);
        return player;
    }
    private static ZNetView View(Component owner) => (ZNetView)AccessTools.Field(owner is Player ? typeof(Character) : owner.GetType(), "m_nview").GetValue(owner);
    private static ZNetView OwnedView(long uid)
    { var view = Fake<ZNetView>(); ZDO zdo = Zdo(uid); zdo.SetOwnerInternal(100); AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo); return view; }
    private static Player Levels(Player player, float farming, float animal, float voyaging)
    {
        ZDO zdo = View(player).GetZDO();
        Set(zdo, "Serverbound.level.farming", farming); Set(zdo, "Serverbound.level.animalHandling", animal); Set(zdo, "Serverbound.level.voyaging", voyaging);
        return player;
    }
    private static void Set(ZDO zdo, string key, float value) => zdo.Set(key, value);
    private static void Set(ZDO zdo, string key, int value) => zdo.Set(key, value);
    private static void Position(Component component, Vector3 position)
    { if (!transforms.TryGetValue(component, out Transform transform)) transforms[component] = transform = Fake<Transform>(); positions[transform] = position; }
    private static IDisposable Scope(Player player) => (IDisposable)Activator.CreateInstance(skills.GetNestedType("Scope", Flags)!, Flags, null, new object[] { player }, null);
    private static Player? Actor() => (Player?)Call("Actor");
    private static float Factor(Player player, Skills.SkillType skill) => (float)Call("Factor", player, skill)!;
    private static object? Call(string name, params object?[] args) => AccessTools.Method(skills, name).Invoke(null, args);
    private static MethodInfo Vendor(string name, string method) => AccessTools.Method(impact.GetType("ImpactfulSkills.patches." + name, true), method);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(typeof(ServerSkillTests), name));
    private static void Hook(Harmony h, Type t, string name, string prefix) => h.Patch(AccessTools.Method(t, name), prefix: Hook(prefix));
    private static bool Skip() => false;
    private static bool Session(ref long __result) { __result = 100; return false; }
    private static bool Transform(Component __instance, ref Transform __result) { if (!transforms.ContainsKey(__instance)) Position(__instance, Vector3.zero); __result = transforms[__instance]; return false; }
    private static bool ReadPosition(Transform __instance, ref Vector3 __result) { __result = positions[__instance]; return false; }
    private static bool Name(ref string __result) { __result = "fixture"; return false; }
    private static bool Tamed(Character __instance, ref bool __result) { __result = (bool)AccessTools.Field(typeof(Character), "m_tamed").GetValue(__instance); return false; }
    private static bool Health(ref float __result) { __result = health; return false; }
    private static bool Attacker(ref Character __result) { __result = attacker!; return false; }
    private static bool Grant(long __0, string __1, object[] __2)
    {
        if (__1 == "Serverbound.OwnerSkillStatus") statusReplies.Add((__0, (bool)__2[0]));
        else grants.Add((__0, __1, (ZPackage)__2[0]));
        return false;
    }
    private static bool Coroutine(IEnumerator __1, ref UnityEngine.Coroutine __result)
    { miningDrops.Add((Dictionary<GameObject, int>)__1.GetType().GetFields(Flags).First(f => f.FieldType == typeof(Dictionary<GameObject, int>)).GetValue(__1)); __result = null!; return false; }
    private static bool Instantiate(ref UnityEngine.Object __result)
    {
        var item = Fake<ItemDrop>(); item.m_itemData = new ItemDrop.ItemData { m_shared = prefabItem.m_itemData.m_shared }; spawned.Add(item);
        // The generic call's cast needs a GameObject; its GetComponent boundary is replaced below.
        var go = Fake<GameObject>(); produced[go] = item; __result = go; return false;
    }
    private static readonly Dictionary<GameObject, ItemDrop> produced = new Dictionary<GameObject, ItemDrop>();
    private static Character GetCharacter(Component target) => characters[target];
    private static Ship GetShip(Component target) => ships[target];
    private static ItemDrop GetItem(GameObject target) => produced.TryGetValue(target, out ItemDrop item) ? item : prefabItem;
    private static IEnumerable<CodeInstruction> NativeComponents(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var source in instructions)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && method.Name == "GetComponent" && method.IsGenericMethod)
            {
                if (method.GetGenericArguments()[0] == typeof(Character)) code.operand = AccessTools.Method(typeof(ServerSkillTests), nameof(GetCharacter));
                if (method.GetGenericArguments()[0] == typeof(ItemDrop)) code.operand = AccessTools.Method(typeof(ServerSkillTests), nameof(GetItem));
                if (method.GetGenericArguments()[0] == typeof(Ship)) code.operand = AccessTools.Method(typeof(ServerSkillTests), nameof(GetShip));
                code.opcode = OpCodes.Call;
            }
            yield return code;
        }
    }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return; } throw new Exception("Expected invalid skill state rejection"); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
