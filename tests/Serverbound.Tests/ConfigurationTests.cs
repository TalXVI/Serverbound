using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx.Configuration;
using HarmonyLib;
using Serverbound;
using Serverbound.Settings;

internal static class ConfigurationTests
{
    private const string RetiredConfig =
        "[General]\nEnabled = false\n[Networking]\nQueueSizeKB = 31\n[Compatibility]\nClientSkills = false\n"
        + "[FutureSettings]\nUnbound = keep me\n"
        + "[CharacterGuard]\nEnabled = true\nNewCharacters = RequireFresh\nNewCharacterAction = Kick\nChangedAway = Kick\n"
        + "ExemptAdmins = false\nFreshEvent = army_eikthyr\nNewCharacterMessage = blocked\nChangedAwayMessage = changed\n"
        + "[ItemLedger]\nMode = On\nItems = auto\nExtraItems = Wood\nExcludeItems = Stone\nLogAllMovements = true\n"
        + "ExemptAdmins = false\nGraceHours = 0\nMessage = removed\nAltarMessage = refused\n";

    internal static void Run(string lab, Action<string, Action> test)
    {
        // Install the vendor hooks before the first Configuration.Load call. Mono can inline
        // the unpatched config setter if a plain-config test runs first.
        test("StartupAccelerator saves the upgraded config after startup", () => WithDelayedSaves(lab, saveAfterStartup =>
            WithConfig(RetiredConfig, config =>
            {
                Check(!config.SaveOnConfigSet, "vendor did not defer saving");
                Configuration.Load(config);
                Check(!config.SaveOnConfigSet, "vendor save policy changed");
                saveAfterStartup();
                CheckRemoved(config);
                Check(config.SaveOnConfigSet, "vendor did not restore automatic saving");
                Check(!Configuration.modEnabled.Value && Configuration.networkQueueSizeKB.Value == 31, "active settings changed");
                string saved = File.ReadAllText(config.ConfigFilePath);
                Check(saved.Contains("ClientSkills = false") && saved.Contains("Unbound = keep me"), "unbound settings discarded");
                Check(saved.Contains("# Setting type: Boolean"), "active settings were saved before binding");
            })));

        test("StartupAccelerator saves newly bound settings after startup", () => WithDelayedSaves(lab, saveAfterStartup =>
            WithConfig(null, config =>
            {
                Configuration.Load(config);
                saveAfterStartup();
                CheckRemoved(config);
                string saved = File.ReadAllText(config.ConfigFilePath);
                Check(saved.Contains("[General]") && saved.Contains("[Networking]"), "new settings were not saved");
                Check(config.SaveOnConfigSet, "vendor did not restore automatic saving");
            })));

        test("generated configuration omits character and item restrictions", () => WithConfig(null, config =>
        {
            Configuration.Load(config);
            Check(config.SaveOnConfigSet, "automatic saving disabled");
            CheckRemoved(config);
            Check(typeof(Plugin).Assembly.GetType("Serverbound.Features.CharacterGuard") == null
                && typeof(Plugin).Assembly.GetType("Serverbound.Features.ItemLedger") == null
                && typeof(Plugin).Assembly.GetType("Serverbound.Features.GuardLog") == null, "retired implementation remains");
        }));

        test("upgrade automatically removes retired settings before client binding", () => WithConfig(RetiredConfig, config =>
        {
            Configuration.Load(config);
            Check(config.SaveOnConfigSet, "automatic saving disabled");
            CheckRemoved(config);
            Check(!Configuration.modEnabled.Value && Configuration.networkQueueSizeKB.Value == 31, "active settings changed");
            string saved = File.ReadAllText(config.ConfigFilePath);
            Check(saved.Contains("ClientSkills = false") && saved.Contains("Unbound = keep me"), "unbound settings discarded");
        }));

        test("deferred upgrade discards enabled restrictions and preserves other settings", () => WithConfig(RetiredConfig, config =>
        {
            config.SaveOnConfigSet = false;
            config.Bind("CharacterGuard", "Enabled", false);
            Configuration.Load(config);
            Check(!config.SaveOnConfigSet, "save policy changed");
            Check(!Configuration.modEnabled.Value && Configuration.networkQueueSizeKB.Value == 31, "active settings changed");
            Check(!config.Bind("Compatibility", "ClientSkills", true).Value, "client setting changed");
            config.Save();
            CheckRemoved(config);
            Check(File.ReadAllText(config.ConfigFilePath).Contains("Unbound = keep me"), "unrelated unbound setting discarded");
            config.Reload();
            Configuration.Load(config);
            config.Save();
            CheckRemoved(config);
            Check(!Configuration.modEnabled.Value && Configuration.networkQueueSizeKB.Value == 31, "settings changed after reload");
        }));
    }

    private static void WithDelayedSaves(string lab, Action<Action> action)
    {
        string path = Directory.GetFiles(Path.Combine(lab, "BepInEx/patchers"), "StartupAccelerator.dll", SearchOption.AllDirectories).Single();
        using (var hash = SHA256.Create())
            Check(BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant()
                == "489297e61efa1698dbc616068d50ba8e70c290f8695eb1b08e15e1759e163790", "unsupported StartupAccelerator fixture");
        Type vendor = Assembly.LoadFrom(path).GetType("StartupAccelerator.StartupAccelerator", true)!;
        FieldInfo pending = AccessTools.Field(vendor, "changedConfigFiles");
        Check(((IList)pending.GetValue(null)).Count == 0, "vendor has pending saves before test");
        var harmony = new Harmony("Serverbound.Tests.ConfigSaves");
        try
        {
            // Use the vendor's real constructor/setter hooks and its FejdStartup save callback.
            harmony.PatchAll(vendor.GetNestedType("DelayConfigSave", BindingFlags.NonPublic));
            harmony.PatchAll(vendor.GetNestedType("SkipManuallyChangedSaveOnConfigSet", BindingFlags.NonPublic));
            MethodInfo finish = AccessTools.Method(vendor.GetNestedType("ChangeConfigSaveBack", BindingFlags.NonPublic), "Prefix");
            action(() => finish.Invoke(null, null));
        }
        finally
        {
            harmony.UnpatchSelf();
            ((IList)pending.GetValue(null)).Clear();
        }
    }

    private static void CheckRemoved(ConfigFile config)
    {
        Check(!config.Keys.Any(key => key.Section == "CharacterGuard" || key.Section == "ItemLedger"), "retired settings are bound");
        string saved = File.ReadAllText(config.ConfigFilePath);
        Check(!saved.Contains("[CharacterGuard]") && !saved.Contains("[ItemLedger]"), "retired section remains on disk");
    }

    private static void WithConfig(string? initial, Action<ConfigFile> action)
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            if (initial != null) File.WriteAllText(path, initial);
            action(new ConfigFile(path, false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
