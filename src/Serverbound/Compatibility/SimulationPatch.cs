using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Serverbound.Compatibility
{
    // Removal is delayed until Start: StartupAccelerator defers vendor registrations.
    internal static class SimulationPatch
    {
        internal const string ServerboundGuid = Plugin.PluginGUID;
        internal const string CoreOwner = ServerboundGuid + ".Core";
        private const string CompatOwner = ServerboundGuid + ".Compat_ValheimCommunityPatch";
        private const string VpoGuid = "dev.ontrigger.vpo";
        private const string VcpGuid = "MidnightsFX.ValheimCommunityPatch";
        private const string VpoHash = "614CD643343E2E2D182BA4B50AA8C96D8FEDB8E09A16C6AA0018BAD222D6EE70";
        private static Assembly? vpo, vcp;
        private static readonly List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)> takenOver = new List<(MethodBase, Patch, HarmonyPatchType)>();
        private static bool pending;
        internal static Assembly? VcpAssembly => vcp;
        internal static bool Active { get; private set; }

        internal static void Prepare(Func<string, Assembly?> resolve)
        {
            Active = false;
            vpo = resolve(VpoGuid); vcp = resolve(VcpGuid);
            takenOver.Clear();
            pending = true;
        }

        internal static void Deactivate() { Active = false; pending = false; }

        internal static bool CoreActive()
        {
            foreach ((Type type, string target, string patch) in new[]
            {
                (typeof(ZNetScene), "CreateDestroyObjects", "CreateDestroyObjects_Patch"),
                (typeof(ZDOMan), "ReleaseNearbyZDOS", "ZDOMan_ReleaseNearbyZDOS_Patch"),
                (typeof(ZoneSystem), "Update", "ZoneSystem_Update_Patch")
            })
            {
                Type hookType = typeof(Features.Core).GetNestedType(patch)!;
                MethodInfo hook = AccessTools.DeclaredMethod(hookType, "Prefix");
                Patches? info = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(type, target));
                if (info == null || !info.Prefixes.Any(p => p.owner == CoreOwner && p.PatchMethod == hook)) return false;
            }
            return true;
        }

        internal static void Verify()
        {
            if (!pending) return;
            pending = false;
            var removed = new List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)>();
            (MethodInfo Target, MethodInfo Hook)? addedCompat = null;
            bool core = false;
            try
            {
                if (!CoreActive())
                {
                    CompatibilityInstaller.Info("Simulation: Core not active on this process; object-management patches retained.");
                    return;
                }
                core = true;
                
                Guard.Build(typeof(ZNet).Assembly, ExpectedBuilds.Server);
                var hooks = new List<(MethodBase Target, Patch Hook, HarmonyPatchType Kind)>();
                (MethodInfo Target, MethodInfo Hook)? missingCompat = null;
                if (vpo != null)
                {
                    Guard.Build(vpo, VpoHash);
                    Type group = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.ObjectManagement.ZNetSceneObjectManagementPatch");
                    Type release = Guard.Type(vpo, "ValheimPerformanceOptimizations.Patches.ObjectManagement.ZDOManReleaseNearbyPatch");
                    foreach ((Type targetType, string target, string method, HarmonyPatchType kind) in new[]
                    {
                        (typeof(ZNetScene), "CreateDestroyObjects", "ZNetScene_CreateDestroyObjects_Prefix", HarmonyPatchType.Prefix),
                        (typeof(ZDOMan), "AddToSector", "ZDOMan_AddToSector_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZDO), "InvalidateSector", "ZDO_InvalidateSector_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZDOMan), "HandleDestroyedZDO", "ZDOMan_HandleDestroyedZDO_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZDO), "Deserialize", "ZDO_Deserialize_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZNetScene), "AddInstance", "ZNetScene_AddInstance_Postfix", HarmonyPatchType.Postfix),
                        (typeof(ZNetScene), "Destroy", "ZNetScene_Destroy_Prefix", HarmonyPatchType.Prefix),
                        (typeof(ZNetScene), "Shutdown", "ZNetScene_Shutdown_Prefix", HarmonyPatchType.Prefix)
                    }) hooks.Add(ExactHook(targetType, target, AccessTools.DeclaredMethod(group, method), VpoGuid, kind));
                    hooks.Add(ExactHook(typeof(ZDOMan), "ReleaseNearbyZDOS", AccessTools.DeclaredMethod(release, "Prefix"), VpoGuid, HarmonyPatchType.Prefix));
                }
                if (vcp != null)
                {
                    Guard.Build(vcp, ExpectedBuilds.ValheimCommunityPatch);
                    Type group = Guard.Type(vcp, "ValheimCommunityPatch.Patches.Performance.SceneIdleSkipPatch");
                    hooks.Add(ExactHook(typeof(ZNetScene), "CreateDestroyObjects", AccessTools.DeclaredMethod(group, "CreateDestroyObjectsPrefix"), VcpGuid, HarmonyPatchType.Prefix));
                    hooks.Add(ExactHook(typeof(ZNetScene), "CreateDestroyObjects", AccessTools.DeclaredMethod(group, "CreateDestroyObjectsPostfix"), VcpGuid, HarmonyPatchType.Postfix));
                    Type compat = typeof(Features.Compat_ValheimCommunityPatch.ZNetScene_Awake_Patch);
                    MethodInfo target = Guard.Method(typeof(ZNetScene), "Awake", typeof(void));
                    MethodInfo hook = Guard.Method(compat, "Postfix", typeof(void));
                    if (!All(target).Any(p => p.PatchMethod == hook))
                    {
                        if (Features.Compat_ValheimCommunityPatch.ZNetScene_Awake_Patch.s_done)
                            throw new NotSupportedException("Serverbound VCP takeover already ran but its registration is missing.");
                        missingCompat = (target, hook);
                    }
                    else ExactHook(typeof(ZNetScene), "Awake", hook, CompatOwner, HarmonyPatchType.Postfix);
                }
                foreach (var hook in hooks)
                {
                    removed.Add(hook);
                    new Harmony(Plugin.PluginGUID + ".Simulation").Unpatch(hook.Target, hook.Hook.PatchMethod);
                    if (All(hook.Target).Any(p => p.PatchMethod == hook.Hook.PatchMethod))
                        throw new InvalidOperationException("Conflicting vendor hook remains after removal: " + Guard.Name(hook.Hook.PatchMethod));
                }
                if (missingCompat.HasValue)
                {
                    // Recover a missing lifecycle registration before world initialization.
                    addedCompat = missingCompat;
                    new Harmony(CompatOwner).Patch(addedCompat.Value.Target, postfix: new HarmonyMethod(addedCompat.Value.Hook));
                    ExactHook(typeof(ZNetScene), "Awake", addedCompat.Value.Hook, CompatOwner, HarmonyPatchType.Postfix);
                    CompatibilityInstaller.Info("Simulation: registered Serverbound's VCP takeover after chainloading.");
                }
                if (!CoreActive()) throw new InvalidOperationException("Core changed during integration.");
                takenOver.AddRange(removed);
                Active = true;
                CompatibilityInstaller.Info($"Simulation: APPLIED; removed {removed.Count} exact server-side VPO/VCP hooks; Core retained.");
            }
            catch (Exception error)
            {
                if (addedCompat.HasValue)
                {
                    try { new Harmony(CompatOwner).Unpatch(addedCompat.Value.Target, addedCompat.Value.Hook); }
                    catch (Exception rollback) { CompatibilityInstaller.Error("Simulation: Serverbound VCP registration rollback failed. " + rollback); }
                }
                foreach (var hook in removed)
                {
                    try { Restore(hook); }
                    catch (Exception rollback) { CompatibilityInstaller.Error("Simulation: rollback failed; inspect Harmony state before launch. " + rollback); }
                }
                CompatibilityInstaller.Error("Simulation: REJECTED BUILD/REGISTRATIONS; required launch gate failed. " + error);
                // Restored vendor object management cannot coexist with Core.
                if (core) Plugin.DisableSimulation();
            }
        }

        internal static void RememberRemoved((MethodBase Target, Patch Hook, HarmonyPatchType Kind) hook) => takenOver.Add(hook);

        internal static void RestoreVendors()
        {
            var failures = new List<Exception>();
            foreach (var hook in takenOver)
            {
                try { Restore(hook); }
                catch (Exception failure) { failures.Add(failure); }
            }
            takenOver.Clear();
            if (failures.Count != 0) throw new AggregateException("Vendor hook restoration failed; stop the server.", failures);
        }

        internal static IEnumerable<Patch> All(MethodBase target)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            return info == null ? Enumerable.Empty<Patch>() : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers)
                .Concat(info.Finalizers).Concat(info.ILManipulators);
        }

        internal static (MethodBase Target, Patch Hook, HarmonyPatchType Kind) ExactHook(Type type, string name,
            MethodInfo method, string owner, HarmonyPatchType kind)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, name);
            return ExactHook(target, method, owner, kind);
        }

        private static (MethodBase Target, Patch Hook, HarmonyPatchType Kind) ExactHook(MethodBase target,
            MethodInfo method, string owner, HarmonyPatchType kind)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            Patch[] all = All(target).Where(p => p.PatchMethod == method).ToArray();
            IEnumerable<Patch> expected = info == null ? Enumerable.Empty<Patch>()
                : kind == HarmonyPatchType.Prefix ? info.Prefixes : kind == HarmonyPatchType.Postfix ? info.Postfixes : info.Transpilers;
            if (all.Length != 1 || all[0].owner != owner || !expected.Contains(all[0]))
                throw new NotSupportedException($"Expected one {owner} {kind}: {Guard.Name(method)} on {Guard.Name(target)}.");
            return (target, all[0], kind);
        }

        internal static void Restore((MethodBase Target, Patch Hook, HarmonyPatchType Kind) hook)
        {
            if (All(hook.Target).Any(p => p.PatchMethod == hook.Hook.PatchMethod))
            {
                Patch actual = ExactHook(hook.Target, hook.Hook.PatchMethod, hook.Hook.owner, hook.Kind).Hook;
                if (actual.priority != hook.Hook.priority || !actual.before.SequenceEqual(hook.Hook.before)
                    || !actual.after.SequenceEqual(hook.Hook.after) || actual.debug != hook.Hook.debug)
                    throw new InvalidOperationException("Existing vendor hook has changed ordering: " + Guard.Name(hook.Hook.PatchMethod));
                return;
            }
            var method = new HarmonyMethod(hook.Hook.PatchMethod, hook.Hook.priority, hook.Hook.before, hook.Hook.after, hook.Hook.debug);
            var owner = new Harmony(hook.Hook.owner);
            if (hook.Kind == HarmonyPatchType.Prefix) owner.Patch(hook.Target, prefix: method);
            else if (hook.Kind == HarmonyPatchType.Postfix) owner.Patch(hook.Target, postfix: method);
            else owner.Patch(hook.Target, transpiler: method);
            ExactHook(hook.Target, hook.Hook.PatchMethod, hook.Hook.owner, hook.Kind);
        }
    }
}
