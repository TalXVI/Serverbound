using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using Serverbound;
using Serverbound.Compatibility;
using UnityEngine;

// Runs the game's real TerrainComp methods and the installed vendor transpilers. Transform lookups
// are the only fixtures; no scene, save, or RPC is exercised.
internal static class TerrainLimitsTests
{
    private const string Fixture = "Serverbound.Tests.TerrainFixture";
    private static readonly List<string> messages = new List<string>();
    private static readonly List<string> errors = new List<string>();
    private static readonly MethodInfo Level = AccessTools.DeclaredMethod(typeof(TerrainComp), "LevelTerrain");
    private static readonly MethodInfo Raise = AccessTools.DeclaredMethod(typeof(TerrainComp), "RaiseTerrain");
    private static readonly MethodInfo Apply = AccessTools.DeclaredMethod(typeof(TerrainComp), "ApplyToHeightmap");

    internal static void Run(string profile, bool server, Action<string, Action> test)
    {
        string? path = Directory.GetFiles(Path.Combine(profile, "BepInEx/plugins"), "Digitalroot.HeightmapUnlimitedJvL.dll",
            SearchOption.AllDirectories).SingleOrDefault();
        Assembly? vendor = path == null ? null : Assembly.LoadFrom(path);
        Func<string, Assembly?> installed = guid => guid == TerrainLimitsPatch.VendorGuid ? vendor : null;

        void Case(string name, Action action) => test("terrain limits " + name, () =>
        {
            Action<string> info = CompatibilityInstaller.Info, error = CompatibilityInstaller.Error;
            messages.Clear(); errors.Clear();
            CompatibilityInstaller.Info = messages.Add; CompatibilityInstaller.Error = errors.Add;
            var fixture = new Harmony(Fixture);
            try
            {
                foreach (MethodInfo method in new[] { Level, Raise, AccessTools.DeclaredMethod(typeof(Heightmap), "WorldToVertex") })
                    fixture.Patch(method, transpiler: new HarmonyMethod(typeof(TerrainLimitsTests), nameof(NativeBoundaries)));
                action();
            }
            finally
            {
                TerrainLimitsPatch.Prepare(_ => null, dedicated: false);
                new Harmony(TerrainLimitsPatch.VendorGuid).UnpatchSelf();
                fixture.UnpatchSelf();
                CompatibilityInstaller.Info = info; CompatibilityInstaller.Error = error;
            }
        });

        if (!server)
        {
            Case("install nothing on a client", () =>
            {
                TerrainLimitsPatch.Prepare(installed, dedicated: false);
                TerrainLimitsPatch.Verify();
                Check(!TerrainLimitsPatch.Applied && !Hooked() && messages.Count == 0 && errors.Count == 0, "client role changed terrain limits");
            });
            return;
        }

        Case("stay vanilla when Heightmap Unlimited JvL is absent", () =>
        {
            TerrainLimitsPatch.Prepare(_ => null, dedicated: true);
            TerrainLimitsPatch.Verify();
            Check(!TerrainLimitsPatch.Applied && !Hooked() && errors.Count == 0, "absent vendor installed hooks");
            Check(messages.Count == 1 && messages[0].StartsWith("TerrainLimits: optional mod absent"), string.Join("\n", messages));
            Check(Reconstructed(10, 60) == 18, "vanilla reconstruction changed");
        });
        Case("reject an unsupported vendor build without hooks", () =>
        {
            TerrainLimitsPatch.Prepare(guid => guid == TerrainLimitsPatch.VendorGuid ? typeof(TerrainLimitsTests).Assembly : null, dedicated: true);
            TerrainLimitsPatch.Verify();
            Check(errors.Count == 1 && errors[0].StartsWith("TerrainLimits: NOT APPLIED") && errors[0].Contains("Re-audit required."),
                string.Join("\n", errors));
            Check(!TerrainLimitsPatch.Applied && !Hooked() && !messages.Any(m => m.Contains("APPLIED")), "rejected build installed hooks");
        });
        if (vendor == null) return;

        Type main = vendor.GetType("Digitalroot.HeightmapUnlimitedJvL.Main", true)!;
        Type group = vendor.GetType("Digitalroot.HeightmapUnlimitedJvL.Patch+PatchTerrainComp", true)!;
        FieldInfo maxField = AccessTools.DeclaredField(main, "MaxHeight"), minField = AccessTools.DeclaredField(main, "MinHeight");
        var config = new ConfigFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/terrain-limits.cfg"), false) { SaveOnConfigSet = false };
        ConfigEntry<float> max = config.Bind("General", "Max Height", 200f), min = config.Bind("General", "Min Height", -200f);
        // The vendor's plugin constructor and Awake set these three; no plugin is constructed here.
        void BindVendorConfig()
        {
            max.Value = 200f; min.Value = -200f; maxField.SetValue(null, max); minField.SetValue(null, min);
            AccessTools.DeclaredField(main, "Instance").SetValue(null, FormatterServices.GetUninitializedObject(main));
        }

        Case("apply the vendor's configured limits to all three dedicated methods", () =>
        {
            BindVendorConfig();
            Check(Reconstructed(10, 60) == 18 && Leveled(60, 70, 90) == 8 && Raised(60, 70, 1) == 8, "fixture no longer reproduces the 8 m clamp");

            TerrainLimitsPatch.Prepare(installed, dedicated: true);
            Check(!Hooked(), "hooks installed before deferred verification");
            TerrainLimitsPatch.Verify();
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(TerrainLimitsPatch.Applied && messages.Count(m => m.StartsWith("TerrainLimits: APPLIED")) == 1, string.Join("\n", messages));
            Check(OwnTranspilers().SequenceEqual(new[] { 1, 1, 1 }), "expected one Serverbound transpiler per terrain method");
            Check(new[] { Level, Raise }.All(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Fixture)), "unrelated transpiler removed");

            Check(Reconstructed(10, 60) == 70, "saved 60 m raise did not survive reconstruction");
            Check(Reconstructed(26, 4) == 30, "raise within 8 m changed");
            Check(Reconstructed(10, -60) == -50, "saved 60 m dig did not survive reconstruction");
            Check(Leveled(60, 70, 90) == 80, "leveling clamped the stored delta");
            Check(Raised(60, 70, 1) == 61, "raising clamped the stored delta");
            Check(Leveled(60, 70, 900) == 200 && Leveled(-60, -50, -900) == -200, "configured limits not enforced");
            max.Value = 30f; min.Value = -20f;
            Check(Leveled(0, 10, 900) == 30 && Leveled(0, 10, -900) == -20 && Reconstructed(10, 60) == 40 && Reconstructed(10, -60) == -10,
                "limits did not follow the vendor configuration");
            BindVendorConfig();

            TerrainLimitsPatch.Verify();
            Check(OwnTranspilers().SequenceEqual(new[] { 1, 1, 1 }), "repeated verification registered twice");
            Plugin.DisableSimulation();
            Check(OwnTranspilers().SequenceEqual(new[] { 1, 1, 1 }) && Reconstructed(10, 60) == 70, "simulation rollback removed the terrain limits");
            TerrainLimitsPatch.Prepare(installed, dedicated: true);
            TerrainLimitsPatch.Verify();
            Check(TerrainLimitsPatch.Applied && OwnTranspilers().SequenceEqual(new[] { 1, 1, 1 }), "repeated installation registered twice");
        });
        Case("keep the incoming IL and report when it stops matching the audit", () =>
        {
            BindVendorConfig();
            TerrainLimitsPatch.Prepare(installed, dedicated: true);
            TerrainLimitsPatch.Verify();
            Check(TerrainLimitsPatch.Applied && errors.Count == 0, string.Join("\n", errors));
            MethodInfo hook = AccessTools.DeclaredMethod(typeof(TerrainLimitsPatch), "VendorLimits");
            var changed = new[] { new CodeInstruction(OpCodes.Ldc_R4, 8f), new CodeInstruction(OpCodes.Ret) };
            var result = ((IEnumerable<CodeInstruction>)hook.Invoke(null, new object[] { changed, Level })!).ToArray();
            Check(result.SequenceEqual(changed), "mismatched IL was rewritten");
            Check(!TerrainLimitsPatch.Applied && errors.Count == 1 && errors[0].StartsWith("TerrainLimits: NO LONGER APPLIED"), string.Join("\n", errors));
        });
        Case("reject a vendor whose configuration is not bound", () =>
        {
            BindVendorConfig();
            maxField.SetValue(null, null);
            try
            {
                TerrainLimitsPatch.Prepare(installed, dedicated: true);
                TerrainLimitsPatch.Verify();
                Check(errors.Count == 1 && errors[0].StartsWith("TerrainLimits: NOT APPLIED") && errors[0].Contains("MaxHeight"), string.Join("\n", errors));
                Check(!TerrainLimitsPatch.Applied && !Hooked(), "unbound configuration installed hooks");
            }
            finally { BindVendorConfig(); }
        });
        Case("add nothing when the vendor already registered its patches", () =>
        {
            BindVendorConfig();
            var owner = new Harmony(TerrainLimitsPatch.VendorGuid);
            owner.PatchAll(group);
            string[] before = Snapshot();
            TerrainLimitsPatch.Prepare(installed, dedicated: true);
            TerrainLimitsPatch.Verify();
            Check(errors.Count == 0 && messages.Count == 1 && messages[0].Contains("Serverbound adds none"), string.Join("\n", messages.Concat(errors)));
            Check(!TerrainLimitsPatch.Applied && !Hooked() && before.SequenceEqual(Snapshot()), "vendor-patched process changed");
            Check(Reconstructed(10, 60) == 70 && Leveled(60, 70, 90) == 80 && Raised(60, 70, 1) == 61, "vendor registration lost its limits");
        });
        Case("reject a partial vendor registration and leave it in place", () =>
        {
            BindVendorConfig();
            new Harmony(TerrainLimitsPatch.VendorGuid).Patch(Apply,
                transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(group, "TerrainCompApplyToHeightmapTranspiler")));
            string[] before = Snapshot();
            TerrainLimitsPatch.Prepare(installed, dedicated: true);
            TerrainLimitsPatch.Verify();
            Check(errors.Count == 1 && errors[0].StartsWith("TerrainLimits: NOT APPLIED") && errors[0].Contains("only part"), string.Join("\n", errors));
            Check(!TerrainLimitsPatch.Applied && !Hooked() && before.SequenceEqual(Snapshot()), "partial registration changed");
        });
    }

    private static T Fake<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static Transform TransformOf(Component unused) => Fake<Transform>();
    private static Vector3 PositionOf(Transform unused) => Vector3.zero;

    private static IEnumerable<CodeInstruction> NativeBoundaries(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method)
            {
                string? replacement = method.DeclaringType == typeof(Component) && method.Name == "get_transform" ? nameof(TransformOf)
                    : method.DeclaringType == typeof(Transform) && method.Name == "get_position" ? nameof(PositionOf) : null;
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.DeclaredMethod(typeof(TerrainLimitsTests), replacement);
                }
            }
            yield return instruction;
        }
    }

    // One-vertex terrain: the reconstructed height for a saved level delta over an original height.
    private static float Reconstructed(float baseHeight, float savedDelta)
    {
        var comp = Fake<TerrainComp>();
        comp.m_initialized = true;
        comp.m_width = 0;
        comp.m_levelDelta = new[] { savedDelta };
        comp.m_smoothDelta = new float[1];
        comp.m_modifiedPaint = new bool[1];
        var heights = new List<float> { baseHeight };
        Apply.Invoke(comp, new object?[] { null, heights, new[] { baseHeight }, new[] { baseHeight }, null });
        return heights[0];
    }

    private static TerrainComp Operated(float savedDelta, float currentHeight)
    {
        var heightmap = Fake<Heightmap>();
        heightmap.m_width = 0;
        heightmap.m_scale = 1;
        AccessTools.DeclaredField(typeof(Heightmap), "m_heights").SetValue(heightmap, new List<float> { currentHeight });
        var comp = Fake<TerrainComp>();
        comp.m_hmap = heightmap;
        comp.m_width = 0;
        comp.m_levelDelta = new[] { savedDelta };
        comp.m_smoothDelta = new float[1];
        comp.m_modifiedHeight = new bool[1];
        return comp;
    }

    // The stored delta after the owner levels the vertex to a target height.
    private static float Leveled(float savedDelta, float currentHeight, float targetHeight)
    {
        TerrainComp comp = Operated(savedDelta, currentHeight);
        Level.Invoke(comp, new object[] { new Vector3(0, targetHeight, 0), 1f, true });
        return comp.m_levelDelta[0];
    }

    // The stored delta after the owner raises the vertex from its current height.
    private static float Raised(float savedDelta, float currentHeight, float amount)
    {
        TerrainComp comp = Operated(savedDelta, currentHeight);
        Raise.Invoke(comp, new object[] { new Vector3(0, currentHeight, 0), 1f, amount, true, 1f });
        return comp.m_levelDelta[0];
    }

    private static IEnumerable<Patch> Transpilers(MethodInfo target) =>
        Harmony.GetPatchInfo(target)?.Transpilers ?? Enumerable.Empty<Patch>();
    private static int[] OwnTranspilers() => new[] { Level, Raise, Apply }
        .Select(target => Transpilers(target).Count(p => p.owner == TerrainLimitsPatch.Owner)).ToArray();
    private static bool Hooked() => Harmony.GetAllPatchedMethods()
        .Any(method => Harmony.GetPatchInfo(method)!.Owners.Contains(TerrainLimitsPatch.Owner));
    private static string[] Snapshot() => new[] { Level, Raise, Apply }.SelectMany(target => SimulationPatch.All(target)
        .Select(p => $"{target.Name}|{p.owner}|{p.PatchMethod.DeclaringType!.FullName}.{p.PatchMethod.Name}|{p.priority}")).ToArray();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
