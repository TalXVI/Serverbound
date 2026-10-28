using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Serverbound.Compatibility;
using Serverbound.Features;

namespace Serverbound
{
    // A registered transpiler can return unchanged IL. Registration alone cannot authorize Core.
    internal static class CoreVerification
    {
        private static readonly List<(MethodBase Target, MethodInfo Hook, HarmonyPatchType Kind)> hooks =
            new List<(MethodBase, MethodInfo, HarmonyPatchType)>();
        private static readonly Dictionary<MethodBase, bool> rewrites = new Dictionary<MethodBase, bool>();
        private static bool verified;

        internal static bool Failed { get; private set; }
        internal static bool Blocked => Failed || (rewrites.Count != 0 && !verified);

        internal static void Begin()
        {
            if (hooks.Count == 0) Prepare();

            rewrites.Clear();
            foreach (var hook in hooks.Where(h => h.Kind == HarmonyPatchType.Transpiler)) rewrites.Add(hook.Target, false);

            Failed = false;
            verified = false;
        }

        private static void Prepare()
        {
            foreach (Type type in typeof(Core).GetNestedTypes())
            {
                HarmonyMethod target = type.GetCustomAttributes<HarmonyPatch>().Single().info;
                MethodInfo method = AccessTools.DeclaredMethod(target.declaringType, target.methodName, target.argumentTypes)
                    ?? throw new MissingMethodException(target.declaringType.FullName, target.methodName);

                foreach (var kind in new[] { HarmonyPatchType.Prefix, HarmonyPatchType.Postfix, HarmonyPatchType.Transpiler, HarmonyPatchType.Finalizer })
                {
                    MethodInfo? hook = AccessTools.DeclaredMethod(type, kind.ToString());
                    if (hook != null) hooks.Add((method, hook, kind));
                }
            }
        }

        internal static void Record(MethodBase method, bool matched)
        {
            if (!rewrites.ContainsKey(method)) return;

            rewrites[method] = matched;
            if (matched || Failed) return;

            Failed = true;
            SimulationPatch.Deactivate();
            Plugin.logger.LogError($"Core: {(verified ? "NO LONGER APPLIED" : "NOT APPLIED")}; required rewrite failed: {method.DeclaringType!.Name}.{method.Name}.");
        }

        internal static void Require(bool allowPending = false)
        {
            if (Failed || rewrites.Count == 0)
                throw new NotSupportedException("Core: a required transpiler did not apply.");

            MethodBase[] pending = rewrites.Where(r => !r.Value).Select(r => r.Key).ToArray();
            if (pending.Length != 0)
            {
                // StartupAccelerator queues wrapper generation until chainloading finishes.
                // Authority stays blocked, and Start requires every queued rewrite to finish.
                if (allowPending) return;

                throw new NotSupportedException("Core: required transpilers did not run: "
                    + string.Join(", ", pending.Select(m => m.DeclaringType!.Name + "." + m.Name)));
            }

            int registered = Harmony.GetAllPatchedMethods().SelectMany(SimulationPatch.All).Count(p => p.owner == SimulationPatch.CoreOwner);
            if (registered != hooks.Count || hooks.Any(h => !Registered(h.Target, h.Hook, h.Kind)))
                throw new NotSupportedException("Core: required Harmony registrations changed.");

            verified = true;
        }

        private static bool Registered(MethodBase target, MethodInfo hook, HarmonyPatchType kind)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            if (info == null) return false;

            IEnumerable<Patch> patches = kind == HarmonyPatchType.Prefix ? info.Prefixes
                : kind == HarmonyPatchType.Postfix ? info.Postfixes
                : kind == HarmonyPatchType.Transpiler ? info.Transpilers : info.Finalizers;

            return patches.Count(p => p.owner == SimulationPatch.CoreOwner && p.PatchMethod == hook) == 1;
        }
    }
}
