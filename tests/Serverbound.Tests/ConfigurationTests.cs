using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
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

    internal static void Run(Action<string, Action> test)
    {
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
