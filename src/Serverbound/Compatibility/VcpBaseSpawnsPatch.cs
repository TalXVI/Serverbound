using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Serverbound.Compatibility
{
    // VCP skips its client spawning fix on headless servers. Core runs spawning there.
    internal static class VcpBaseSpawnsPatch
    {
        internal const string Owner = Plugin.PluginGUID + ".VcpBaseSpawns";
        private const string Vendor = "MidnightsFX.ValheimCommunityPatch";
        private static Func<bool> enabled = null!;
        private static Func<Vector3, bool> underUnloadedBase = null!;
        private static Action? restoreRegistry;
        private static (MethodBase Target, Patch Hook, HarmonyPatchType Kind)? removedVendor;
        private static bool installed;

        // SimulationPatch has already checked the exact VCP and dedicated game builds.
        internal static void Install(Assembly vcp)
        {
            if (vcp.GetName().Version < new System.Version(0, 42, 0, 0)) return;

            Type group = Guard.Type(vcp, "ValheimCommunityPatch.Patches.Correctness.SpawnInsideBasePatch");
            Type toggleType = Guard.Type(vcp, "ValheimCommunityPatch.FixToggle");
            FieldInfo toggleField = AccessTools.DeclaredField(group, "Enabled");
            MethodInfo valueGetter = Guard.Method(toggleType, "get_Value", typeof(bool));
            MethodInfo baseCheck = Guard.Method(group, "IsUnderUnloadedBase", typeof(bool), typeof(Vector3));
            MethodInfo target = Guard.Method(typeof(SpawnSystem), "IsSpawnPointGood", typeof(bool),
                typeof(SpawnSystem.SpawnData), typeof(Vector3).MakeByRefType());
            MethodInfo vendorHook = Guard.Method(group, "IsSpawnPointGoodPostfix", typeof(void),
                typeof(SpawnSystem.SpawnData), typeof(Vector3).MakeByRefType(), typeof(bool).MakeByRefType());
            MethodInfo hook = Guard.Method(typeof(VcpBaseSpawnsPatch), nameof(Postfix), typeof(void),
                typeof(SpawnSystem.SpawnData), typeof(Vector3).MakeByRefType(), typeof(bool).MakeByRefType());
            if (toggleField == null || !toggleField.IsStatic || toggleField.FieldType != toggleType || valueGetter.IsStatic || !baseCheck.IsStatic)
                throw new NotSupportedException("VCP base-spawn members changed.");

            var readEnabled = new DynamicMethod("VcpBaseSpawnsEnabled", typeof(bool), Type.EmptyTypes,
                typeof(VcpBaseSpawnsPatch), true);
            ILGenerator il = readEnabled.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, toggleField);
            il.Emit(OpCodes.Callvirt, valueGetter);
            il.Emit(OpCodes.Ret);
            enabled = (Func<bool>)readEnabled.CreateDelegate(typeof(Func<bool>));
            underUnloadedBase = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), baseCheck);

            try
            {
                // Patch Every Side can install the client postfix on a dedicated server.
                // Its reference-position check uses the world origin there, so replace it.
                if (SimulationPatch.All(target).Any(p => p.PatchMethod == vendorHook))
                {
                    removedVendor = SimulationPatch.ExactHook(typeof(SpawnSystem), "IsSpawnPointGood", vendorHook, Vendor, HarmonyPatchType.Postfix);
                    new Harmony(Owner).Unpatch(target, vendorHook);
                    if (SimulationPatch.All(target).Any(p => p.PatchMethod == vendorHook))
                        throw new InvalidOperationException("VCP client base-spawn hook remains installed.");
                }

                new Harmony(Owner).Patch(target, postfix: new HarmonyMethod(hook));
                SimulationPatch.ExactHook(typeof(SpawnSystem), "IsSpawnPointGood", hook, Owner, HarmonyPatchType.Postfix);
                restoreRegistry = VcpFixRegistry.MarkApplied(vcp, group);
                installed = true;
                CompatibilityInstaller.Info("VCP.BaseSpawns: APPLIED; unloaded base areas follow dedicated spawning and retain the vendor setting.");
            }
            catch
            {
                Reset();
                throw;
            }
        }

        private static void Postfix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
        {
            if (installed && __result && !spawn.m_insidePlayerBase && enabled() && underUnloadedBase(spawnPoint))
                __result = false;
        }

        internal static void Reset()
        {
            installed = false;
            new Harmony(Owner).UnpatchSelf();
            if (removedVendor.HasValue) SimulationPatch.Restore(removedVendor.Value);
            removedVendor = null;
            restoreRegistry?.Invoke();
            restoreRegistry = null;
        }
    }
}
