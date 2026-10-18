using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Serverbound.Compatibility;
using UnityEngine;

internal static class ComfortPortalTests
{
    internal static void Run(string profile, bool server, Action<string, Action> test)
    {
        string path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "ComfortTweaks.dll", SearchOption.AllDirectories).Single();
        Assembly vendor = Assembly.LoadFrom(path);
        Type group = vendor.GetType("ComfortTweaks.ComfortTweaks+PlayerUsesPortal", true)!;
        MethodInfo prefix = AccessTools.DeclaredMethod(group, "Prefix");
        MethodInfo finalizer = AccessTools.DeclaredMethod(group, "Finalizer");
        MethodInfo target = AccessTools.DeclaredMethod(typeof(TeleportWorldTrigger), "OnTriggerEnter", new[] { typeof(Collider) });

        test("ComfortTweaks portal rule stays on clients and leaves dedicated nonplayer entries to the game", () =>
        {
            Harmony original = Harmony.CreateAndPatchAll(group);
            var foreign = new Harmony("Serverbound.Tests.OtherPortal");
            var errors = new List<string>();
            var messages = new List<string>();
            try
            {
                // Another prefix under the same owner must survive the exact vendor removal.
                original.Patch(target, prefix: new HarmonyMethod(typeof(ComfortPortalTests), nameof(OtherPrefix))
                    { priority = Priority.First, before = new[] { foreign.Id }, after = new[] { "unrelated.portal.owner" } });
                foreign.Patch(target, postfix: new HarmonyMethod(typeof(ComfortPortalTests), nameof(OtherPostfix)));
                Patch[] before = SimulationPatch.All(target).ToArray();
                Check(before.Count(p => p.PatchMethod == prefix) == 1, "vendor fixture did not register its real prefix");
                CompatibilityInstaller.Install(g => g == "xyz.alcan.comfortcalc" ? vendor : null,
                    messages.Add, _ => { }, errors.Add, installOwnerSkills: false, dedicated: server);
                CompatibilityInstaller.Verify();
                Check(errors.Count == 0, string.Join("\n", errors));
                Patch[] after = SimulationPatch.All(target).ToArray();
                Check(after.Count(p => p.PatchMethod == prefix) == (server ? 0 : 1), "headless portal rule was not separated from client behavior");
                Check(after.Count(p => p.PatchMethod == finalizer) == 1, "vendor finalizer changed");
                foreach (Patch old in before.Where(p => !server || p.PatchMethod != prefix))
                    Check(after.Any(p => p.PatchMethod == old.PatchMethod && p.owner == old.owner && p.priority == old.priority
                        && p.before.SequenceEqual(old.before) && p.after.SequenceEqual(old.after) && p.debug == old.debug), "unrelated portal hook or ordering changed");
                Check(after.Length == before.Length - (server ? 1 : 0), "unexpected portal registration change");
                CompatibilityInstaller.Verify();
                Check(SimulationPatch.All(target).Count() == after.Length, "deferred verification was not idempotent");
                Check(!server || messages.Count(m => m.StartsWith("ComfortPortals: APPLIED")) == 1, "dedicated result was not reported");
            }
            finally
            {
                original.UnpatchSelf(); foreign.UnpatchSelf();
                CompatibilityInstaller.Install(_ => null, _ => { }, _ => { }, _ => { }, installOwnerSkills: false);
            }
        });

        if (!server) return;
        test("ComfortTweaks rejects unsupported builds and incomplete or duplicated portal registrations without removing hooks", () =>
        {
            foreach (string problem in new[] { "build", "missing prefix", "missing finalizer", "duplicate prefix" })
            {
                Harmony original = Harmony.CreateAndPatchAll(group);
                var foreign = new Harmony("Serverbound.Tests.DuplicateComfortPortal");
                var errors = new List<string>();
                try
                {
                    if (problem == "missing prefix") original.Unpatch(target, prefix);
                    if (problem == "missing finalizer") original.Unpatch(target, finalizer);
                    if (problem == "duplicate prefix") foreign.Patch(target, prefix: new HarmonyMethod(prefix));
                    Patch[] before = SimulationPatch.All(target).ToArray();
                    CompatibilityInstaller.Install(g => g == "xyz.alcan.comfortcalc" ? (problem == "build" ? typeof(ComfortPortalTests).Assembly : vendor) : null,
                        _ => { }, _ => { }, errors.Add, installOwnerSkills: false, dedicated: true);
                    CompatibilityInstaller.Verify();
                    Check(errors.Count == 1 && errors[0].StartsWith("ComfortPortals: NOT APPLIED"), problem + ": " + string.Join("\n", errors));
                    Patch[] after = SimulationPatch.All(target).ToArray();
                    Check(before.Length == after.Length && before.All(old => after.Any(p => p.PatchMethod == old.PatchMethod && p.owner == old.owner)),
                        problem + " changed a rejected registration");
                }
                finally { original.UnpatchSelf(); foreign.UnpatchSelf(); }
            }
            var absentErrors = new List<string>();
            CompatibilityInstaller.Install(_ => null, _ => { }, _ => { }, absentErrors.Add, installOwnerSkills: false, dedicated: true);
            CompatibilityInstaller.Verify();
            Check(absentErrors.Count == 0 && !SimulationPatch.All(target).Any(), "absent ComfortTweaks requires no takeover");
        });
    }

    private static void OtherPrefix() { }
    private static void OtherPostfix() { }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
