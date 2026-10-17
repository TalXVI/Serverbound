using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Serverbound.Compatibility
{
    // VCP skips this class on headless processes, where Core now runs spawning.
    internal static class VcpSpawningPatch
    {
        internal const string Owner = Plugin.PluginGUID + ".VcpSpawning";
        private const string Vendor = "MidnightsFX.ValheimCommunityPatch";
        private static Action? restoreRegistry;

        // SimulationPatch has already verified the exact vendor and dedicated game builds.
        internal static void Install(Assembly vcp)
        {
            if (vcp.GetName().Version < new System.Version(0, 37, 0, 0)) return;
            Type group = Guard.Type(vcp, "ValheimCommunityPatch.Patches.Correctness.LovePotionSpawnPatch");
            var hooks = new[]
            {
                (Target: AccessTools.DeclaredMethod(typeof(SpawnSystem), "UpdateSpawnList"), Hook: AccessTools.DeclaredMethod(group, "UpdateSpawnListPrefix"), Kind: HarmonyPatchType.Prefix),
                (Target: AccessTools.DeclaredMethod(typeof(SpawnSystem), "UpdateSpawnList"), Hook: AccessTools.DeclaredMethod(group, "UpdateSpawnListFinalizer"), Kind: HarmonyPatchType.Finalizer),
                (Target: AccessTools.DeclaredMethod(typeof(SpawnSystem), "GetLevelUpChance", new[] { typeof(UnityEngine.Vector3), typeof(SpawnSystem.SpawnData) }), Hook: AccessTools.DeclaredMethod(group, "GetLevelUpChancePostfix"), Kind: HarmonyPatchType.Postfix),
                (Target: AccessTools.DeclaredMethod(typeof(SEMan), "Update"), Hook: AccessTools.DeclaredMethod(group, "SEManUpdatePostfix"), Kind: HarmonyPatchType.Postfix),
            };
            int present = 0;
            foreach (var hook in hooks)
            {
                Patch[] matches = SimulationPatch.All(hook.Target).Where(p => p.PatchMethod == hook.Hook).ToArray();
                if (matches.Length == 0) continue;
                if (matches.Length != 1 || (matches[0].owner != Vendor && matches[0].owner != Owner)
                    || !Kind(hook.Target, hook.Kind).Contains(matches[0]))
                    throw new NotSupportedException("Unexpected VCP spawning registration: " + Guard.Name(hook.Hook));
                present++;
            }
            // Patch Every Side can already have installed the complete vendor class.
            if (present == hooks.Length) return;
            if (present != 0) throw new NotSupportedException("VCP spawning hooks are only partly installed.");

            Type registry = Guard.Type(vcp, "ValheimCommunityPatch.FixRegistry");
            Type entryType = Guard.Type(vcp, "ValheimCommunityPatch.FixRegistry+Entry");
            object entry = Guard.Method(registry, "Get", entryType, typeof(Type)).Invoke(null, new object[] { group });
            FieldInfo outcome = AccessTools.Field(entryType, "Outcome");
            object gate = AccessTools.Field(registry, "Gate").GetValue(null);
            object previous;
            lock (gate) previous = outcome.GetValue(entry);
            restoreRegistry = () => { lock (gate) outcome.SetValue(entry, previous); };
            new Harmony(Owner).CreateClassProcessor(group).Patch();
            foreach (var hook in hooks)
            {
                Patch[] matches = SimulationPatch.All(hook.Target).Where(p => p.PatchMethod == hook.Hook).ToArray();
                if (matches.Length != 1 || matches[0].owner != Owner || !Kind(hook.Target, hook.Kind).Contains(matches[0]))
                    throw new InvalidOperationException("VCP spawning hook did not install: " + Guard.Name(hook.Hook));
            }
            Guard.Method(registry, "RecordPatched", typeof(void), typeof(Type), typeof(bool)).Invoke(null, new object[] { group, true });
            CompatibilityInstaller.Info("VCP.Spawning: APPLIED; vendor pheromone logic follows dedicated spawning and retains its config.");
        }

        internal static void Reset()
        {
            new Harmony(Owner).UnpatchSelf();
            restoreRegistry?.Invoke();
            restoreRegistry = null;
        }

        private static System.Collections.Generic.IEnumerable<Patch> Kind(MethodBase target, HarmonyPatchType kind)
        {
            Patches info = Harmony.GetPatchInfo(target)!;
            return kind == HarmonyPatchType.Prefix ? info.Prefixes : kind == HarmonyPatchType.Finalizer ? info.Finalizers : info.Postfixes;
        }
    }
}
