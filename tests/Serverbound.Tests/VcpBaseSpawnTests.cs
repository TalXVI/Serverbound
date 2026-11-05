using System;
using System.Collections;
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

internal static class VcpBaseSpawnTests
{
    private const string Vendor = "MidnightsFX.ValheimCommunityPatch";
    private static bool vanillaResult;

    internal static void Run(string lab, Action<string, Action> test)
    {
        Assembly vcp = Assembly.LoadFrom(Directory.GetFiles(Path.Combine(lab, "BepInEx/plugins"), "ValheimCommunityPatch.dll", SearchOption.AllDirectories).Single());
        if (vcp.GetName().Version < new System.Version(0, 42, 0, 0)) return;

        test("VCP blocks dedicated spawns over unloaded base objects", () => Exercise(vcp));
    }

    private static void Exercise(Assembly vcp)
    {
        Type group = vcp.GetType("ValheimCommunityPatch.Patches.Correctness.SpawnInsideBasePatch", true)!;
        Type registry = vcp.GetType("ValheimCommunityPatch.FixRegistry", true)!;
        Type areaType = group.GetNestedType("BaseArea", BindingFlags.NonPublic)!;
        MethodInfo target = AccessTools.DeclaredMethod(typeof(SpawnSystem), "IsSpawnPointGood");
        MethodInfo vendorHook = AccessTools.DeclaredMethod(group, "IsSpawnPointGoodPostfix");
        FieldInfo toggle = AccessTools.Field(group, "Enabled");
        FieldInfo tableScene = AccessTools.Field(group, "s_tableScene");
        FieldInfo tableCount = AccessTools.Field(group, "s_tablePrefabCount");
        FieldInfo searchRadius = AccessTools.Field(group, "s_searchRadius");
        var areas = (IDictionary)AccessTools.Field(group, "AreasByPrefab").GetValue(null);
        DictionaryEntry[] previousAreas = areas.Cast<DictionaryEntry>().ToArray();
        object oldToggle = toggle.GetValue(null), oldTableScene = tableScene.GetValue(null);
        object oldCount = tableCount.GetValue(null), oldRadius = searchRadius.GetValue(null);
        ZDOMan oldManager = ZDOMan.s_instance;
        ZNetScene oldScene = ZNetScene.s_instance;
        var fixture = new Harmony("Serverbound.Tests.BaseSpawn");
        var vendor = new Harmony(Vendor);
        var core = new Harmony(SimulationPatch.CoreOwner);
        var errors = new List<string>();
        Action<string> oldInfo = CompatibilityInstaller.Info, oldWarning = CompatibilityInstaller.Warning, oldError = CompatibilityInstaller.Error;
        try
        {
            var cfg = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/vcp-base-spawns.cfg"), false) { SaveOnConfigSet = false };
            ConfigEntry<bool> enabled = cfg.Bind("test", "enabled", true);
            object fixSwitch = AccessTools.DeclaredMethod(registry, "SwitchOf").Invoke(null, new object[] { group });
            toggle.SetValue(null, Activator.CreateInstance(toggle.FieldType, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { (object)enabled, fixSwitch }, null));
            AccessTools.DeclaredMethod(registry, "RecordSkipped").Invoke(null, new object[] { group });

            var manager = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
            ZDOMan.s_instance = manager;
            Set(manager, "m_objectsBySector", new List<ZDO>[512 * 512]);
            Set(manager, "m_portalObjects", new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>());
            ZNetScene scene = Fake<ZNetScene>();
            ZNetScene.s_instance = scene;
            Set(scene, "m_namedPrefabs", new Dictionary<int, GameObject>());
            Set(scene, "m_instances", new Dictionary<ZDO, ZNetView>());

            Vector3 point = new Vector3(1024, 0, 1024);
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_position = point;
            Set(zdo, "m_prefab", 12345);
            manager.m_objectsBySector[ZoneSystem.SectorToIndex(ZoneSystem.GetZone(point)).Sector] = new List<ZDO> { zdo };
            object area = Activator.CreateInstance(areaType)!;
            AccessTools.Field(areaType, "Radius").SetValue(area, 20f);
            Array areaArray = Array.CreateInstance(areaType, 1);
            areaArray.SetValue(area, 0);
            areas.Clear();
            areas.Add(12345, areaArray);
            tableScene.SetValue(null, scene);
            tableCount.SetValue(null, 0);
            searchRadius.SetValue(null, 20f);
            // The fixture supplies the prefab geometry and Unity rotation. It does not instantiate a native scene.
            fixture.Patch(AccessTools.DeclaredMethod(typeof(ZDO), "GetRotation"), prefix: new HarmonyMethod(typeof(VcpBaseSpawnTests), nameof(Rotation)));
            fixture.Patch(target, prefix: new HarmonyMethod(typeof(VcpBaseSpawnTests), nameof(Vanilla)));
            var spawn = new SpawnSystem.SpawnData();
            vanillaResult = true;
            Check(Spawn(target, spawn, point), "fixture did not reproduce the unloaded-base window");

            VcpSpawnTests.Start(core, vendor, vcp, errors.Add);
            Check(SimulationPatch.Active && errors.Count == 0, "dedicated integration did not activate: " + string.Join("; ", errors));
            Check(!Spawn(target, spawn, point), "an unloaded base did not reject the spawn");
            Check(Spawn(target, spawn, new Vector3(1124, 0, 1124)), "an unrelated location was rejected");
            scene.m_instances[zdo] = Fake<ZNetView>();
            Check(Spawn(target, spawn, point), "a loaded base did not defer to the vanilla result");
            scene.m_instances.Clear();
            spawn.m_insidePlayerBase = true;
            Check(Spawn(target, spawn, point), "a spawn allowed in player bases was rejected");
            spawn.m_insidePlayerBase = false;
            enabled.Value = false;
            Check(Spawn(target, spawn, point), "the vendor setting was ignored");
            enabled.Value = true;
            vanillaResult = false;
            Check(!Spawn(target, spawn, point), "an existing rejection was reversed");
            vanillaResult = true;
            Plugin.DisableSimulation();
            Check(Spawn(target, spawn, point), "rollback retained the adapter");
            Check(!SimulationPatch.All(target).Any(p => p.owner == VcpBaseSpawnsPatch.Owner), "rollback retained the adapter hook");

            vendor.Patch(target, postfix: new HarmonyMethod(vendorHook) { priority = Priority.Low });
            errors.Clear();
            VcpSpawnTests.Start(core, vendor, vcp, errors.Add);
            Check(SimulationPatch.Active && errors.Count == 0, "dedicated integration rejected the vendor postfix");
            Check(!SimulationPatch.All(target).Any(p => p.PatchMethod == vendorHook), "unsafe client postfix remained on the server");
            Check(!Spawn(target, spawn, point), "replacing the client postfix lost unloaded-base protection");
            Plugin.DisableSimulation();
            Patch restored = Harmony.GetPatchInfo(target).Postfixes.Single(p => p.PatchMethod == vendorHook);
            Check(restored.owner == Vendor && restored.priority == Priority.Low, "rollback did not restore the vendor postfix metadata");
            vendor.UnpatchSelf();

            vendor.Patch(target, prefix: new HarmonyMethod(vendorHook));
            errors.Clear();
            VcpSpawnTests.Start(core, vendor, vcp, errors.Add);
            Check(!SimulationPatch.Active && errors.Any(message => message.Contains("Simulation: REJECTED BUILD/REGISTRATIONS"))
                && Harmony.GetPatchInfo(target).Prefixes.Any(p => p.PatchMethod == vendorHook)
                && !SimulationPatch.All(target).Any(p => p.owner == VcpBaseSpawnsPatch.Owner), "unexpected registration was not rejected intact");
        }
        finally
        {
            Plugin.DisableSimulation();
            vendor.UnpatchSelf();
            core.UnpatchSelf();
            fixture.UnpatchSelf();
            ZDOMan.s_instance = oldManager;
            ZNetScene.s_instance = oldScene;
            toggle.SetValue(null, oldToggle);
            tableScene.SetValue(null, oldTableScene);
            tableCount.SetValue(null, oldCount);
            searchRadius.SetValue(null, oldRadius);
            areas.Clear();
            foreach (DictionaryEntry previous in previousAreas) areas.Add(previous.Key, previous.Value);
            CompatibilityInstaller.Info = oldInfo;
            CompatibilityInstaller.Warning = oldWarning;
            CompatibilityInstaller.Error = oldError;
        }
    }

    private static bool Spawn(MethodInfo target, SpawnSystem.SpawnData spawn, Vector3 point) =>
        (bool)target.Invoke(Fake<SpawnSystem>(), new object[] { spawn, point });
    private static bool Vanilla(ref bool __result) { __result = vanillaResult; return false; }
    private static bool Rotation(ref Quaternion __result) { __result = Quaternion.identity; return false; }
    private static void Set(object instance, string name, object value) => AccessTools.Field(instance.GetType(), name).SetValue(instance, value);
    private static T Fake<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr").SetValue(value, new IntPtr(4321));
        return value;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
