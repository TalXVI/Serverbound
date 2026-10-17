using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using Serverbound;
using Serverbound.Compatibility;
using UnityEngine;

internal static class VcpSpawnTests
{
    private const string Vendor = "MidnightsFX.ValheimCommunityPatch";
    private static readonly int Pheromone = "VCP_Pheromone".GetStableHashCode();
    private static readonly List<string> errors = new List<string>();
    private static SE_Stats effect = null!;
    private static float chance, level;
    private static int min, max, count, id = 128000;

    internal static void Run(string lab, Action<string, Action> test)
    {
        Assembly vcp = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ValheimCommunityPatch.dll", SearchOption.AllDirectories).Single());
        // Earlier supported VCP releases do not provide the pheromone fix.
        if (vcp.GetName().Version < new System.Version(0, 37, 0, 0)) return;
        test("VCP pheromone spawns follow remote status on the simulation owner", () =>
        {
            Type group = vcp.GetType("ValheimCommunityPatch.Patches.Correctness.LovePotionSpawnPatch", true)!;
            Type registry = vcp.GetType("ValheimCommunityPatch.FixRegistry", true)!;
            var fixture = new Harmony("Serverbound.Tests.VcpSpawns");
            var core = new Harmony(Plugin.PluginGUID + ".Core");
            var vendor = new Harmony(Vendor);
            Action<string> oldInfo = CompatibilityInstaller.Info, oldWarning = CompatibilityInstaller.Warning, oldError = CompatibilityInstaller.Error;
            Player oldPlayer = Player.m_localPlayer;
            var oldNear = SpawnSystem.m_tempNearPlayers.ToArray();
            FieldInfo dbInstance = AccessTools.Field(typeof(ObjectDB), "m_instance");
            object oldDb = dbInstance.GetValue(null);
            FieldInfo toggle = AccessTools.Field(group, "Enabled");
            object oldToggle = toggle.GetValue(null);
            ZDO zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = new ZDOID(128000L, 1U);
            try
            {
                errors.Clear();
                Player.m_localPlayer = null!;
                dbInstance.SetValue(null, Fake<ObjectDB>());
                var cfg = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/vcp-spawns.cfg"), false) { SaveOnConfigSet = false };
                ConfigEntry<bool> enabled = cfg.Bind("test", "enabled", true);
                object fixSwitch = AccessTools.DeclaredMethod(registry, "SwitchOf").Invoke(null, new object[] { group });
                toggle.SetValue(null, Activator.CreateInstance(toggle.FieldType, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { (object)enabled, fixSwitch }, null));
                AccessTools.DeclaredMethod(registry, "RecordSkipped").Invoke(null, new object[] { group });
                effect = Fake<SE_Stats>();
                effect.m_pheromoneTarget = Fake<GameObject>();
                effect.m_pheromoneSpawnChanceOverride = 50f;
                effect.m_pheromoneMaxInstanceOverride = 2;
                effect.m_pheromoneSpawnMinLevel = 2;
                effect.m_pheromoneLevelUpMultiplier = 2f;
                Player player = Fake<Player>();
                player.m_nview = Fake<ZNetView>();
                player.m_nview.m_zdo = zdo;
                fixture.Patch(AccessTools.DeclaredMethod(typeof(ZDO), "IncreaseDataRevision"), prefix: Hook(nameof(Skip)));
                zdo.Set(Pheromone, 9876, false);
                SpawnSystem.m_tempNearPlayers.Clear(); SpawnSystem.m_tempNearPlayers.Add(player);
                fixture.Patch(AccessTools.DeclaredMethod(typeof(ObjectDB), "GetStatusEffect"), prefix: Hook(nameof(Status)));
                fixture.Patch(AccessTools.DeclaredMethod(typeof(SpawnSystem), "GetLevelUpChance", new[] { typeof(Vector3), typeof(SpawnSystem.SpawnData) }), prefix: Hook(nameof(BaseLevel)));
                // Native creature creation is the output boundary. Observe the effective
                // spawn inputs after the real VCP prefix; its finalizer still restores them.
                fixture.Patch(AccessTools.DeclaredMethod(typeof(SpawnSystem), "UpdateSpawnList"), prefix: new HarmonyMethod(typeof(VcpSpawnTests), nameof(Spawn)) { priority = Priority.Last });

                var spawner = new SpawnSystem.SpawnData { m_prefab = effect.m_pheromoneTarget, m_spawnChance = 100f, m_maxSpawned = 1, m_minLevel = 1, m_maxLevel = 2 };
                Start(core, vendor, vcp);
                Check(errors.Count == 0, string.Join("\n", errors));
                RunSpawn(spawner);
                Check(chance == 100f && count == 2 && min == 2 && max == 3 && level == 20f,
                    "dedicated spawning lost the remote player's pheromone effect or reduced raid chance");
                Check(spawner.m_maxSpawned == 1 && spawner.m_minLevel == 1 && spawner.m_maxLevel == 2, "vendor did not restore shared spawn data");
                CompatibilityInstaller.Verify(); RunSpawn(spawner);
                Check(level == 20f, "verification duplicated the level multiplier");
                enabled.Value = false; RunSpawn(spawner);
                Check(count == 1 && min == 1 && level == 10f, "vendor config disabled but spawn bonus remained");
                enabled.Value = true;
                ZDOExtraData.RemoveInt(zdo.m_uid, Pheromone); RunSpawn(spawner);
                Check(count == 1 && min == 1 && level == 10f, "a player without published status received the bonus");
                zdo.Set(Pheromone, 9876, false);
                Plugin.DisableSimulation(); RunSpawn(spawner);
                Check(count == 1 && min == 1 && level == 10f, "simulation rollback retained spawning hooks");

                // VCP can already own these hooks when Patch Every Side is enabled.
                vendor.CreateClassProcessor(group).Patch();
                Start(core, vendor, vcp); RunSpawn(spawner);
                Check(errors.SequenceEqual(new[] { "Simulation: DISABLED; resolve the rejected integration before admitting players." })
                    && level == 20f, "existing vendor hooks were rejected or duplicated");
                Plugin.DisableSimulation(); RunSpawn(spawner);
                Check(level == 20f, "rollback removed the vendor's own spawning hooks");
            }
            finally
            {
                Plugin.DisableSimulation();
                fixture.UnpatchSelf(); core.UnpatchSelf(); vendor.UnpatchSelf();
                new Harmony(Plugin.PluginGUID + ".VcpObjectPassPatch").UnpatchSelf();
                ZDOExtraData.RemoveInt(zdo.m_uid, Pheromone);
                Player.m_localPlayer = oldPlayer;
                SpawnSystem.m_tempNearPlayers.Clear(); SpawnSystem.m_tempNearPlayers.AddRange(oldNear);
                dbInstance.SetValue(null, oldDb); toggle.SetValue(null, oldToggle);
                CompatibilityInstaller.Info = oldInfo; CompatibilityInstaller.Warning = oldWarning; CompatibilityInstaller.Error = oldError;
                errors.Clear(); effect = null!;
            }
        });
    }

    private static void Start(Harmony core, Harmony vendor, Assembly vcp)
    {
        foreach (Type type in new[] { typeof(Serverbound.Features.Core.CreateDestroyObjects_Patch), typeof(Serverbound.Features.Core.ZDOMan_ReleaseNearbyZDOS_Patch), typeof(Serverbound.Features.Core.ZoneSystem_Update_Patch) })
            core.CreateClassProcessor(type).Patch();
        Type idle = vcp.GetType("ValheimCommunityPatch.Patches.Performance.SceneIdleSkipPatch", true)!;
        if (!Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects")).Owners.Contains(Vendor))
            vendor.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects"), prefix: new HarmonyMethod(AccessTools.DeclaredMethod(idle, "CreateDestroyObjectsPrefix")), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(idle, "CreateDestroyObjectsPostfix")));
        CompatibilityInstaller.Install(guid => guid == Vendor ? vcp : null, _ => { }, _ => { }, errors.Add, installOwnerSkills: false, dedicated: true);
        CompatibilityInstaller.Verify();
    }
    private static void RunSpawn(SpawnSystem.SpawnData spawner) =>
        AccessTools.DeclaredMethod(typeof(SpawnSystem), "UpdateSpawnList").Invoke(Fake<SpawnSystem>(), new object[] { new List<SpawnSystem.SpawnData> { spawner }, DateTime.UtcNow, true, "e_" });
    private static bool Spawn(List<SpawnSystem.SpawnData> spawners)
    {
        SpawnSystem.SpawnData value = spawners.Single();
        chance = value.m_spawnChance; count = value.m_maxSpawned; min = value.m_minLevel; max = value.m_maxLevel;
        level = SpawnSystem.GetLevelUpChance(Vector3.zero, value);
        return false;
    }
    private static bool Status(int __0, ref StatusEffect __result) { Check(__0 == 9876, "unexpected status lookup"); __result = effect; return false; }
    private static bool BaseLevel(ref float __result) { __result = 10f; return false; }
    private static bool Skip() => false;
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(VcpSpawnTests), name);
    private static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(++id));
        return value;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
