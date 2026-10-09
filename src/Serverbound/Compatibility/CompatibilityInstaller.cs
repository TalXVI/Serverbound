using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Serverbound.Compatibility
{
    public static class CompatibilityInstaller
    {
        internal static Action<string> Info = _ => { };
        internal static Action<string> Warning = _ => { };
        internal static Action<string> Error = _ => { };
        internal const string LegacyGuid = "MVP.Valheim_Serverside_Simulations";

        internal static void CheckInstallation(Func<string, Assembly?> resolve, Func<string, System.Version?> version, bool dedicated = true)
        {
            if (dedicated && resolve(Plugin.ValheimPlusPluginId) != null)
                throw new NotSupportedException("ValheimPlus is outside the audited compatibility matrix. Serverbound simulation cannot start with it until its implementation is validated.");
            if (resolve(LegacyGuid) != null || Harmony.GetAllPatchedMethods().SelectMany(SimulationPatch.All)
                .Any(p => p.owner == LegacyGuid || p.owner.StartsWith(LegacyGuid + ".", StringComparison.Ordinal)))
                throw new NotSupportedException("Remove predecessor simulation DLLs before loading Serverbound. Competing ownership patches are unsafe.");
            System.Version? compat = version("DeepNorthCompat");
            if (compat != null && compat < new System.Version(1, 2, 0))
                throw new NotSupportedException("Upgrade DeepNorthCompat to 1.2.0 or later on every peer. Older builds install competing simulation and skill hooks.");
            if (Harmony.GetAllPatchedMethods().SelectMany(SimulationPatch.All).Any(p =>
                p.owner == "DeepNorthCompat.Simulation" || p.owner == "DeepNorthCompat.OwnerSkills"))
                throw new NotSupportedException("Obsolete DeepNorthCompat simulation/skill hooks remain installed. Stop and replace the old package.");
        }

        public static void Install(Func<string, Assembly?> resolve, Action<string> info,
            Action<string> warning, Action<string> error)
        {
            Info = info; Warning = warning; Error = error;
            SimulationPatch.Prepare(resolve);
            try { OwnerSkillPatch.Prepare(resolve("MidnightsFX.ImpactfulSkills")); }
            catch (Exception failure)
            {
                new Harmony(OwnerSkillPatch.Owner).UnpatchSelf();
                Error("OwnerSkills: NOT APPLIED; optional integration rejected. " + failure);
            }
        }

        public static void Verify()
        {
            SimulationPatch.Verify();
            OwnerSkillPatch.Verify();
        }
    }
}
