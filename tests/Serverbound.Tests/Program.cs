using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using Serverbound;
using Serverbound.Compatibility;
using HarmonyLib;
using SysConsole = System.Console;

internal static class Program
{
    private static readonly string Lab = Environment.GetEnvironmentVariable("SERVERBOUND_MODS_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "com.kesomannen.gale", "valheim", "profiles", "Deep North");
    private static readonly string Managed = Environment.GetEnvironmentVariable("SERVERBOUND_MANAGED_PATH") ?? Path.Combine(Environment.GetEnvironmentVariable("SERVERBOUND_VALHEIM_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Valheim"),
        "valheim_Data", "Managed");
    private static int passed;

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            InitializePaths();
            ConfigurationTests.Run(Test);
            InstallationTests.Run(Test);
            if (Environment.GetEnvironmentVariable("SERVERBOUND_TEST_MODE") == "server") RunServer();
            else { SimulationTests.Policy(Test); ClientSimulationTests.Run(Lab, Test); }
            SysConsole.WriteLine($"PASS: {passed} test cases"); return 0;
        }
        catch (Exception exception) { SysConsole.Error.WriteLine(exception); return 1; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunServer() { SimulationTests.Run(Lab, Test); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void InitializePaths()
    {
            AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null, new object[]
            {
                typeof(Program).Assembly.Location, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/BepInEx"),
                Managed, new[] { Path.Combine(Lab, "BepInEx/core") }
            });
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string core = Path.Combine(Lab, "BepInEx", "core", name);
        string game = Path.Combine(Managed, name);
        string? file = File.Exists(core) ? core : File.Exists(game) ? game
            : Directory.GetFiles(Path.Combine(Lab, "BepInEx", "plugins"), name, SearchOption.AllDirectories).FirstOrDefault();
        return file == null ? null : Assembly.LoadFrom(file);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Test(string name, Action action)
    {
        action(); passed++; SysConsole.WriteLine("PASS " + name);
    }

}
