using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Serverbound;
using Serverbound.Compatibility;
using HarmonyLib;

internal static class InstallationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Serverbound identity is independent and predecessor loading is incompatible", () =>
        {
            var metadata = typeof(Plugin).GetCustomAttribute<BepInEx.BepInPlugin>()!;
            Check(metadata.GUID == "org.serverbound.valheim" && metadata.Name == "Serverbound" && metadata.Version.ToString() == "0.1.1", "identity");
            Check(typeof(Plugin).GetCustomAttribute<BepInEx.BepInIncompatibility>()!.IncompatibilityGUID == CompatibilityInstaller.LegacyGuid, "loader conflict guard");
            Check(!typeof(Plugin).Assembly.GetReferencedAssemblies().Any(a => a.Name == "DeepNorthCompat" || a.Name == "ImpactfulSkills"), "mandatory compatibility dependency");
        });
        test("basic simulation works without optional mods", () =>
        {
            var errors = new List<string>();
            CompatibilityInstaller.Install(_ => null, _ => { }, _ => { }, errors.Add);
            Check(errors.Count == 0, string.Join("\n", errors));
            if (!Guard.KnownServerBuild()) return;
            var harmony = new Harmony(SimulationPatch.CoreOwner);
            try
            {
                foreach (Type type in typeof(Serverbound.Features.Core).GetNestedTypes())
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0) harmony.PatchAll(type);
                CompatibilityInstaller.Verify();
                Check(SimulationPatch.Active, "Core inactive without VPO/VCP/skills/DeepNorthCompat");
            }
            finally { harmony.UnpatchSelf(); SimulationPatch.Deactivate(); }
        });
        test("old DeepNorthCompat and predecessor assemblies reject before takeover", () =>
        {
            Reject(() => CompatibilityInstaller.CheckInstallation(g => g == CompatibilityInstaller.LegacyGuid ? typeof(InstallationTests).Assembly : null, _ => null));
            Reject(() => CompatibilityInstaller.CheckInstallation(_ => null, _ => new System.Version(1,1,6)));
            CompatibilityInstaller.CheckInstallation(_ => null, _ => new System.Version(1,2,0));
        });
        test("unaudited ValheimPlus implementation rejects before patch installation", () =>
        {
            Reject(() => CompatibilityInstaller.CheckInstallation(g => g == Plugin.ValheimPlusPluginId ? typeof(InstallationTests).Assembly : null, _ => null));
            CompatibilityInstaller.CheckInstallation(g => g == Plugin.ValheimPlusPluginId ? typeof(InstallationTests).Assembly : null, _ => null, dedicated: false);
        });
        test("obsolete Harmony ownership rejects even without loader metadata", () =>
        {
            var old = new Harmony("DeepNorthCompat.OwnerSkills");
            try
            {
                old.Patch(AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"), postfix: new HarmonyMethod(typeof(InstallationTests), nameof(Noop)));
                Reject(() => CompatibilityInstaller.CheckInstallation(_ => null, _ => null));
            }
            finally { old.UnpatchSelf(); }
        });
        test("rollback rejects an existing vendor hook registered under a different owner", () =>
        {
            MethodInfo target = AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects");
            MethodInfo method = AccessTools.Method(typeof(InstallationTests), nameof(Noop));
            var original = new Harmony("Serverbound.Tests.RestoreOriginal");
            var foreign = new Harmony("Serverbound.Tests.RestoreForeign");
            try
            {
                original.Patch(target, prefix: new HarmonyMethod(method));
                var saved = SimulationPatch.ExactHook(typeof(ZNetScene), "CreateDestroyObjects", method, original.Id, HarmonyPatchType.Prefix);
                original.UnpatchSelf();
                foreign.Patch(target, prefix: new HarmonyMethod(method));
                Reject(() => SimulationPatch.Restore(saved));
                Check(SimulationPatch.All(target).Single(p => p.PatchMethod == method).owner == foreign.Id, "foreign hook changed during rejected rollback");
            }
            finally { original.UnpatchSelf(); foreign.UnpatchSelf(); }
        });
        test("updated DeepNorthCompat installs no simulation, skill or Serverbound RPC hooks", () =>
        {
            string path = Environment.GetEnvironmentVariable("SERVERBOUND_COMPAT_PATH") ?? throw new Exception("Set SERVERBOUND_COMPAT_PATH to the coordinated DeepNorthCompat candidate DLL.");
            Assembly assembly = Assembly.LoadFrom(path);
            Type plugin = assembly.GetType("DeepNorthCompat.Plugin", true)!;
            Check(plugin.GetCustomAttribute<BepInEx.BepInPlugin>()!.Version >= new System.Version(1,2,0), "old companion");
            Check(assembly.GetType("DeepNorthCompat.SimulationPatch") == null && assembly.GetType("DeepNorthCompat.OwnerSkillPatch") == null, "duplicate implementation retained");
            var errors = new List<string>();
            assembly.GetType("DeepNorthCompat.CompatibilityInstaller", true)!.GetMethod("Install")!.Invoke(null, new object[]
            {
                new Func<string, Assembly?>(_ => null), new Action<string>(_ => { }), new Action<string>(_ => { }), new Action<string>(errors.Add)
            });
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(!Harmony.GetAllPatchedMethods().SelectMany(SimulationPatch.All).Any(p => p.owner == "DeepNorthCompat.Simulation" || p.owner == "DeepNorthCompat.OwnerSkills"), "duplicate hooks");
            foreach (string owner in Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners).Distinct().Where(o => o.StartsWith("DeepNorthCompat.")).ToArray()) new Harmony(owner).UnpatchSelf();
        });
    }
    private static void Noop() { }
    private static void Reject(Action action) { try { action(); } catch (NotSupportedException) { return; } throw new Exception("expected rejection"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
