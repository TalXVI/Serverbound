using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Serverbound.Compatibility
{
    /*
        Heightmap Unlimited JvL registers its three TerrainComp transpilers only on processes with a
        graphics device. A vanilla dedicated server never builds terrain, so that was harmless. Under
        Serverbound the server reconstructs saved terrain and applies terrain operations it owns, and
        without those transpilers it clamps both to 8 m while clients use the configured limits.
        This runs the vendor's own transpilers on the dedicated server, so the limits come from the
        vendor's configuration and stay identical to its clients.
    */
    internal static class TerrainLimitsPatch
    {
        internal const string Owner = Plugin.PluginGUID + ".TerrainLimits";
        internal const string VendorGuid = "digitalroot.mods.heightmapunlimitedjvl";
        private const float VanillaLimit = 8f;

        private sealed class Site
        {
            internal MethodInfo Target = null!;
            internal MethodInfo VendorTranspiler = null!;
            internal (float Constant, MethodInfo Replacement)[] Limits = null!;
            internal bool Rewritten;
            internal string? Failure;
        }

        private static readonly Dictionary<string, Site> sites = new Dictionary<string, Site>();
        private static Func<string, Assembly?>? resolveAssembly;
        private static bool pending;

        internal static bool Applied { get; private set; }

        internal static void Prepare(Func<string, Assembly?> resolve, bool dedicated)
        {
            new Harmony(Owner).UnpatchSelf();
            sites.Clear();
            Applied = false;
            resolveAssembly = dedicated ? resolve : null;
            pending = dedicated;
        }

        // Runs from Start: the vendor binds its configuration in Awake, and no zone exists yet.
        internal static void Verify()
        {
            if (!pending || resolveAssembly == null) return;
            pending = false;
            Assembly? vendor = resolveAssembly(VendorGuid);
            if (vendor == null)
            {
                CompatibilityInstaller.Info("TerrainLimits: optional mod absent; vanilla terrain limits retained.");
                return;
            }

            var harmony = new Harmony(Owner);
            try
            {
                Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Server);
                Guard.Build(vendor, ExpectedBuilds.HeightmapUnlimited);
                Type main = Guard.Type(vendor, "Digitalroot.HeightmapUnlimitedJvL.Main");
                Type group = Guard.Type(vendor, "Digitalroot.HeightmapUnlimitedJvL.Patch+PatchTerrainComp");
                MethodInfo min = Guard.Method(main, "Min", typeof(float));
                MethodInfo max = Guard.Method(main, "Max", typeof(float));
                MethodInfo minAbs = Guard.Method(main, "MinAbs", typeof(float));
                // The vendor's transpilers trace through its plugin instance and read these entries.
                RequireStartupValue(main, "Instance", main);
                RequireStartupValue(main, "MaxHeight", typeof(ConfigEntry<float>));
                RequireStartupValue(main, "MinHeight", typeof(ConfigEntry<float>));

                Site[] audited =
                {
                    Audited(group, "TerrainCompLevelTerrainTranspiler",
                        Guard.Method(typeof(TerrainComp), "LevelTerrain", typeof(void), typeof(Vector3), typeof(float), typeof(bool)),
                        (-VanillaLimit, min), (VanillaLimit, max)),
                    Audited(group, "TerrainCompRaiseTerrainTranspiler",
                        Guard.Method(typeof(TerrainComp), "RaiseTerrain", typeof(void), typeof(Vector3), typeof(float), typeof(float),
                            typeof(bool), typeof(float)),
                        (-VanillaLimit, min), (VanillaLimit, max)),
                    Audited(group, "TerrainCompApplyToHeightmapTranspiler",
                        Guard.Method(typeof(TerrainComp), "ApplyToHeightmap", typeof(void), typeof(Texture2D), typeof(List<float>),
                            typeof(float[]), typeof(float[]), typeof(Heightmap)),
                        (VanillaLimit, minAbs), (VanillaLimit, max))
                };

                int vendorRegistered = audited.Count(VendorRegistered);
                if (vendorRegistered == audited.Length)
                {
                    CompatibilityInstaller.Info("TerrainLimits: Heightmap Unlimited JvL registered its own terrain patches on this process; Serverbound adds none.");
                    return;
                }
                if (vendorRegistered != 0)
                    throw new NotSupportedException("Heightmap Unlimited JvL registered only part of its terrain patches.");

                foreach (Site site in audited) sites.Add(site.Target.Name, site);
                foreach (Site site in audited)
                    harmony.Patch(site.Target, transpiler: Guard.Hook(typeof(TerrainLimitsPatch), nameof(VendorLimits), Priority.Normal));
                MethodInfo hook = AccessTools.DeclaredMethod(typeof(TerrainLimitsPatch), nameof(VendorLimits));
                foreach (Site site in audited)
                {
                    if (!site.Rewritten)
                        throw new NotSupportedException($"{Guard.Name(site.Target)} did not match the audited IL. {site.Failure}");
                    SimulationPatch.ExactHook(typeof(TerrainComp), site.Target.Name, hook, Owner, HarmonyPatchType.Transpiler);
                }

                Applied = true;
                CompatibilityInstaller.Info("TerrainLimits: APPLIED; dedicated terrain reconstruction, leveling, and raising use "
                    + $"Heightmap Unlimited JvL's configured limits ({min.Invoke(null, null)} m to {max.Invoke(null, null)} m).");
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                sites.Clear();
                Applied = false;
                CompatibilityInstaller.Error("TerrainLimits: NOT APPLIED; this server limits terrain to 8 m while Heightmap Unlimited JvL "
                    + "clients use their configured limits. Resolve this before admitting players. " + error);
            }
        }

        private static Site Audited(Type group, string transpiler, MethodInfo target, params (float, MethodInfo)[] limits)
        {
            return new Site
            {
                Target = target,
                VendorTranspiler = Guard.Method(group, transpiler, typeof(IEnumerable<CodeInstruction>), typeof(IEnumerable<CodeInstruction>)),
                Limits = limits
            };
        }

        private static void RequireStartupValue(Type main, string name, Type type)
        {
            FieldInfo? field = AccessTools.DeclaredField(main, name);
            if (field == null || !field.IsStatic || field.FieldType != type)
                throw new MissingFieldException(main.FullName, name);
            if (field.GetValue(null) == null)
                throw new NotSupportedException($"Heightmap Unlimited JvL has not set {name}; its startup did not complete.");
        }

        // True only for the vendor's exact transpiler under the vendor's owner. Anything else that
        // involves the vendor or an earlier Serverbound registration is unaudited.
        private static bool VendorRegistered(Site site)
        {
            Patch[] related = SimulationPatch.All(site.Target)
                .Where(p => p.owner == VendorGuid || p.owner == Owner || p.PatchMethod == site.VendorTranspiler).ToArray();
            if (related.Length == 0) return false;
            Patches info = Harmony.GetPatchInfo(site.Target)!;
            if (related.Length != 1 || related[0].owner != VendorGuid || related[0].PatchMethod != site.VendorTranspiler
                || !info.Transpilers.Contains(related[0]))
                throw new NotSupportedException("Unexpected terrain-limit registration on " + Guard.Name(site.Target) + ".");
            return true;
        }

        // Harmony reruns this whenever another patch lands on the same method. A mismatch keeps the
        // incoming instructions, so the method stays valid while the failure is reported.
        private static IEnumerable<CodeInstruction> VendorLimits(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> source = instructions.ToList();
            if (!sites.TryGetValue(original.Name, out Site? site) || site.Target != original) return source;
            try
            {
                List<CodeInstruction> rewritten =
                    ((IEnumerable<CodeInstruction>)site.VendorTranspiler.Invoke(null, new object[] { source })!).ToList();
                RequireAuditedRewrite(site, source, rewritten);
                site.Rewritten = true;
                site.Failure = null;
                return rewritten;
            }
            catch (Exception error)
            {
                site.Rewritten = false;
                site.Failure = ((error as TargetInvocationException)?.InnerException ?? error).ToString();
                if (Applied)
                {
                    Applied = false;
                    CompatibilityInstaller.Error("TerrainLimits: NO LONGER APPLIED; a later patch changed "
                        + Guard.Name(original) + ". " + site.Failure);
                }
                return source;
            }
        }

        private static void RequireAuditedRewrite(Site site, List<CodeInstruction> source, List<CodeInstruction> rewritten)
        {
            if (rewritten.Count != source.Count)
                throw new NotSupportedException("The vendor transpiler changed the instruction count.");
            int next = 0;
            for (int index = 0; index < source.Count; index++)
            {
                CodeInstruction before = source[index], after = rewritten[index];
                if (!(before.opcode == OpCodes.Ldc_R4 && before.operand is float constant && Math.Abs(constant) == VanillaLimit))
                {
                    if (!ReferenceEquals(before, after))
                        throw new NotSupportedException("The vendor transpiler changed an instruction outside the limits.");
                    continue;
                }
                if (next == site.Limits.Length || constant != site.Limits[next].Constant)
                    throw new NotSupportedException($"Unexpected limit constant {constant} at instruction {index}.");
                if (before.labels.Count != 0 || before.blocks.Count != 0)
                    throw new NotSupportedException($"The limit constant at instruction {index} carries a label or exception block.");
                if (after.opcode != OpCodes.Call || !Equals(after.operand, site.Limits[next].Replacement))
                    throw new NotSupportedException($"The vendor transpiler did not replace the limit at instruction {index} as audited.");
                next++;
            }
            if (next != site.Limits.Length)
                throw new NotSupportedException($"Expected {site.Limits.Length} limit constants, found {next}.");
        }
    }
}
