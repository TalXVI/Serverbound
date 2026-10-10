using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Serverbound;
using Serverbound.Compatibility;
using UnityEngine;

internal static class OdinShipTests
{
    internal static void Run(string profile, Action<string, Action> test)
    {
        test("OdinShip remains optional when its assembly is absent", () =>
        {
            OdinShipOwnershipPatch.ResetForTests();
            OdinShipOwnershipPatch.Prepare(null);
            Check(!OdinShipOwnershipPatch.IsInstalled, "absent OdinShip installed hooks");
            OdinShipOwnershipPatch.Verify();
        });

        test("OdinShip stays a soft dependency without a compile-time reference", () =>
        {
            Check(typeof(Plugin).GetCustomAttributes<BepInEx.BepInDependency>()
                .Any(dependency => dependency.DependencyGUID == "marlthon.OdinShip"
                    && dependency.Flags == BepInEx.BepInDependency.DependencyFlags.SoftDependency), "OdinShip dependency is not soft");
            Check(!typeof(Plugin).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == "OdinShip"),
                "Serverbound has a compile-time OdinShip reference");
        });

        test("OdinShip rejects an unsupported vendor assembly without partial hooks", () =>
        {
            OdinShipOwnershipPatch.ResetForTests();
            var errors = new List<string>();
            Action<string> oldError = CompatibilityInstaller.Error;
            CompatibilityInstaller.Error = errors.Add;
            try
            {
                OdinShipOwnershipPatch.Prepare(typeof(OdinShipTests).Assembly);
                Check(!OdinShipOwnershipPatch.IsInstalled, "unsupported assembly installed hooks");
                Check(errors.Count == 1 && errors[0].StartsWith("OdinShip: NOT APPLIED"), string.Join("\n", errors));
                Check(!Harmony.GetAllPatchedMethods().SelectMany(method => Harmony.GetPatchInfo(method)!.Owners)
                    .Any(owner => owner == OdinShipOwnershipPatch.Owner), "partial OdinShip hook survived rejection");
            }
            finally { CompatibilityInstaller.Error = oldError; }
        });

        string? candidate = Environment.GetEnvironmentVariable("SERVERBOUND_ODINSHIP_DLL")
            ?? Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "OdinShip.dll", SearchOption.AllDirectories).SingleOrDefault();
        if (candidate == null) return;
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException("SERVERBOUND_ODINSHIP_DLL does not identify an OdinShip fixture.", candidate);
        }

        Assembly vendor = Assembly.LoadFrom(Path.GetFullPath(candidate));
        test("OdinShip 0.8.7 installs owner-routed hooks on audited methods", () =>
        {
            OdinShipOwnershipPatch.ResetForTests();
            OdinShipOwnershipPatch.Prepare(vendor);
            Check(!OdinShipOwnershipPatch.IsInstalled, "integration enabled before deferred verification");
            OdinShipOwnershipPatch.Verify();
            Check(OdinShipOwnershipPatch.IsInstalled, "audited candidate was not applied");

            Type customization = vendor.GetType("OdinShip.ShipCustomization", true)!;
            Type customizationPatch = vendor.GetType("OdinShip.ShipCustomizationPatch", true)!;
            Type turretPatch = vendor.GetType("OdinShip.TurretModePatch", true)!;
            AssertHook(customization.GetMethod("Setup", BindingFlags.Instance | BindingFlags.NonPublic)!, "postfix", "RegisterShipRpcs");
            AssertHook(customization.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public)!, "prefix", "TrackNameRequest");
            AssertHook(customization.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public)!, "finalizer", "FinishNameSubmission");
            AssertHook(customization.GetMethod("RPC_SetShipName", BindingFlags.Instance | BindingFlags.NonPublic)!, "prefix", "ValidateNameRequest");
            AssertHook(customization.GetMethod("RPC_SetShipName", BindingFlags.Instance | BindingFlags.NonPublic)!, "postfix", "CompleteNameRequest");
            AssertHook(customizationPatch.GetMethod("CycleObject", BindingFlags.Static | BindingFlags.NonPublic)!, "prefix", "RouteObjectCustomization");
            AssertHook(customizationPatch.GetMethod("CycleMaterial", BindingFlags.Static | BindingFlags.NonPublic)!, "prefix", "RouteMaterialCustomization");
            AssertHook(customizationPatch.GetMethod("Player_Update_Postfix", BindingFlags.Static | BindingFlags.Public)!, "transpiler", "CustomizationInputGate");
            AssertHook(turretPatch.GetMethod("Player_Update_Postfix", BindingFlags.Static | BindingFlags.Public)!, "transpiler", "TurretInputGate");

            CheckInputCallsRemain("CustomizationInputGate", "CanUseCustomizationInputs");
            CheckInputCallsRemain("TurretInputGate", "TryRouteTurretMode");
        });

        test("OdinShip input IL mismatch leaves instructions intact and rejects only its own hooks", () =>
        {
            OdinShipOwnershipPatch.ResetForTests();
            var info = new List<string>();
            var errors = new List<string>();
            Action<string> previousInfo = CompatibilityInstaller.Info;
            Action<string> previousError = CompatibilityInstaller.Error;
            var other = new Harmony("Serverbound.Tests.OdinShip.Other");
            MethodInfo target = AccessTools.Method(vendor.GetType("OdinShip.ShipCustomization", true), "Setup");
            try
            {
                CompatibilityInstaller.Info = info.Add;
                CompatibilityInstaller.Error = errors.Add;
                other.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(OdinShipTests), nameof(OtherModHook))));
                OdinShipOwnershipPatch.Prepare(vendor);
                Check(!info.Any(message => message.Contains("APPLIED")), "prepare reported APPLIED before verification");
                MethodInfo gate = AccessTools.Method(typeof(OdinShipOwnershipPatch), "CustomizationInputGate");
                var original = new[] { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ret) };
                var result = ((IEnumerable<CodeInstruction>)gate.Invoke(null, new object[] { original })!).ToArray();
                Check(result.Length == original.Length && result.Zip(original, (a, b) => a.opcode == b.opcode && Equals(a.operand, b.operand)).All(same => same),
                    "mismatched IL changed or threw instead of being retained");
                OdinShipOwnershipPatch.Verify();
                Check(!OdinShipOwnershipPatch.IsInstalled && errors.Count == 1, "deferred mismatch did not reject the integration");
                Check(!info.Any(message => message.Contains("APPLIED")), "rejected integration reported APPLIED");
                Check(Harmony.GetPatchInfo(target)!.Postfixes.Any(patch => patch.owner == other.Id), "verification removed another mod's hook");
                Check(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)!.Owners.Contains(OdinShipOwnershipPatch.Owner)),
                    "partial owner-routing hooks remain after mismatch");
            }
            finally
            {
                other.UnpatchSelf();
                OdinShipOwnershipPatch.ResetForTests();
                CompatibilityInstaller.Info = previousInfo;
                CompatibilityInstaller.Error = previousError;
            }
        });

        test("OdinShip deferred verification rejects a missing action or rename finalizer hook", () =>
        {
            OdinShipOwnershipPatch.ResetForTests();
            var errors = new List<string>();
            Action<string> previousError = CompatibilityInstaller.Error;
            try
            {
                CompatibilityInstaller.Error = errors.Add;
                foreach (var missing in new[]
                {
                    (Type: "OdinShip.ShipCustomizationPatch", Method: "CycleMaterial", Kind: HarmonyPatchType.Prefix),
                    (Type: "OdinShip.ShipCustomization", Method: "SetText", Kind: HarmonyPatchType.Finalizer)
                })
                {
                    errors.Clear();
                    OdinShipOwnershipPatch.Prepare(vendor);
                    MethodInfo target = AccessTools.Method(vendor.GetType(missing.Type, true), missing.Method);
                    new Harmony(OdinShipOwnershipPatch.Owner).Unpatch(target, missing.Kind, OdinShipOwnershipPatch.Owner);
                    OdinShipOwnershipPatch.Verify();
                    Check(!OdinShipOwnershipPatch.IsInstalled && errors.Count == 1, "missing hook escaped verification: " + missing.Method);
                }
            }
            finally
            {
                OdinShipOwnershipPatch.ResetForTests();
                CompatibilityInstaller.Error = previousError;
            }
        });

        test("OdinShip owner validation rejects stale ownership and unauthorized access", () =>
        {
            Check(Allowed(owns: true, ready: true, player: true, alive: true, near: true, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false), "nearby player on an idle ship rejected");
            Check(Allowed(owns: true, ready: true, player: true, alive: true, near: true, ship: true,
                helmOccupied: true, atHelm: true, cargoInUse: false, cargoOwner: false), "current helm user rejected");
            Check(Allowed(owns: true, ready: true, player: true, alive: true, near: true, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: true, cargoOwner: true), "current cargo owner rejected");
            Reject(owns: false, ready: true, player: true, alive: true, near: true, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "ownership changed");
            Reject(owns: true, ready: false, player: true, alive: true, near: true, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "connection");
            Reject(owns: true, ready: true, player: false, alive: false, near: false, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "player is no longer");
            Reject(owns: true, ready: true, player: true, alive: false, near: true, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "alive");
            Reject(owns: true, ready: true, player: true, alive: true, near: false, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "closer");
            Reject(owns: true, ready: true, player: true, alive: true, near: true, ship: false,
                helmOccupied: false, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "unavailable");
            Reject(owns: true, ready: true, player: true, alive: true, near: true, ship: true,
                helmOccupied: true, atHelm: false, cargoInUse: false, cargoOwner: false, contains: "helm user");
            Reject(owns: true, ready: true, player: true, alive: true, near: true, ship: true,
                helmOccupied: false, atHelm: false, cargoInUse: true, cargoOwner: false, contains: "cargo is in use");
        });

        test("OdinShip option cycling handles invalid saved indices and empty lists", () =>
        {
            Check(OdinShipOwnershipPatch.TryNextIndex(0, 3, out int first) && first == 1, "first option");
            Check(OdinShipOwnershipPatch.TryNextIndex(2, 3, out int wrapped) && wrapped == 0, "wraparound");
            Check(OdinShipOwnershipPatch.TryNextIndex(-1, 3, out int invalid) && invalid == 0, "negative saved index");
            Check(!OdinShipOwnershipPatch.TryNextIndex(0, 0, out _), "empty list accepted");
        });

        test("OdinShip installer verifies the optional group with client skills disabled", () =>
        {
            OdinShipOwnershipPatch.ResetForTests();
            Action<string> previousInfo = CompatibilityInstaller.Info, previousWarning = CompatibilityInstaller.Warning, previousError = CompatibilityInstaller.Error;
            var errors = new List<string>();
            try
            {
                CompatibilityInstaller.Install(guid => guid == "marlthon.OdinShip" ? vendor : null, _ => { }, _ => { }, errors.Add, installOwnerSkills: false);
                Check(!OdinShipOwnershipPatch.IsInstalled, "installer enabled requests before verification");
                CompatibilityInstaller.CheckInstallation(_ => null, _ => new System.Version(1, 2, 0));
                CompatibilityInstaller.Verify();
                Check(OdinShipOwnershipPatch.IsInstalled && errors.Count == 0, "optional group failed installer verification: " + string.Join("; ", errors));
            }
            finally
            {
                OdinShipOwnershipPatch.ResetForTests();
                CompatibilityInstaller.Info = previousInfo; CompatibilityInstaller.Warning = previousWarning; CompatibilityInstaller.Error = previousError;
            }
        });

        OdinShipRuntimeTests.Run(vendor, test);
        OdinShipOwnershipPatch.ResetForTests();
    }

    private static bool Allowed(bool owns, bool ready, bool player, bool alive, bool near, bool ship,
        bool helmOccupied, bool atHelm, bool cargoInUse, bool cargoOwner)
    {
        return OdinShipOwnershipPatch.IsAuthorizedRequest(owns, ready, player, alive, near, ship,
            helmOccupied, atHelm, cargoInUse, cargoOwner, out _);
    }

    private static void Reject(bool owns, bool ready, bool player, bool alive, bool near, bool ship,
        bool helmOccupied, bool atHelm, bool cargoInUse, bool cargoOwner, string contains)
    {
        Check(!OdinShipOwnershipPatch.IsAuthorizedRequest(owns, ready, player, alive, near, ship,
            helmOccupied, atHelm, cargoInUse, cargoOwner, out string rejection), "request was authorized");
        Check(rejection.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0, rejection);
    }

    private static void AssertHook(MethodBase target, string kind, string hookName)
    {
        Patches info = Harmony.GetPatchInfo(target)!;
        IEnumerable<Patch> patches = kind == "prefix" ? info.Prefixes : kind == "postfix" ? info.Postfixes
            : kind == "finalizer" ? info.Finalizers : info.Transpilers;
        Check(patches.Any(patch => patch.owner == OdinShipOwnershipPatch.Owner && patch.PatchMethod.Name == hookName),
            target.DeclaringType?.FullName + "." + target.Name + " missing " + hookName);
    }

    private static void CheckInputCallsRemain(string transpilerName, string ownerHelperName)
    {
        MethodInfo transpiler = typeof(OdinShipOwnershipPatch).GetMethod(transpilerName, BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo helper = typeof(OdinShipOwnershipPatch).GetMethod(ownerHelperName, BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo ownerCheck = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner), Type.EmptyTypes);
        MethodInfo keyCheck = typeof(OdinShipTests).GetMethod(nameof(KeyInputMarker), BindingFlags.Static | BindingFlags.NonPublic)!;
        var sample = new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Callvirt, ownerCheck),
            new CodeInstruction(OpCodes.Ldc_I4_0),
            new CodeInstruction(OpCodes.Call, keyCheck)
        };
        var rewritten = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { sample })!).ToList();
        Check(rewritten.Count(code => code.Calls(keyCheck)) == 1, transpilerName + " changed the vendor key input call");
        Check(rewritten.Count(code => code.Calls(helper)) == 1, transpilerName + " did not route the owner check");
        Check(!rewritten.Any(code => code.Calls(ownerCheck)), transpilerName + " left the local-owner gate in place");
    }

    private static bool KeyInputMarker(KeyCode key) => false;
    private static void OtherModHook() { }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
