#nullable disable
using BepInEx.Configuration;

namespace Serverbound.Settings
{
	public class Configuration
	{
		public static ConfigEntry<bool> modEnabled;

		public static ConfigEntry<bool> maxObjectsPerFrameEnabled;
		public static ConfigEntry<int> maxObjectsPerFrame;

		public static ConfigEntry<bool> networkingEnabled;
		public static ConfigEntry<int> networkQueueSizeKB;
		public static ConfigEntry<int> networkSendRateMinKB;
		public static ConfigEntry<int> networkSendRateMaxKB;
		public static ConfigEntry<int> networkStatsMinutes;

		public static ConfigEntry<bool> consoleCommandsEnabled;
		public static ConfigEntry<int> unityJobWorkers;

		public static ConfigEntry<int> sendIntervalMs;
		public static ConfigEntry<int> maxCatchUpMs;
		public static ConfigEntry<int> maxZonesPerTick;
		public static ConfigEntry<int> performanceStatsMinutes;
		public static ConfigEntry<int> serverTargetFps;

		public static ConfigEntry<int> maxGiveAmount;
		public static ConfigEntry<int> consoleInputCodePage;

		public static ConfigEntry<bool> fixSaveClientChanges;
		public static ConfigEntry<bool> fixDungeonLoadGuard;
		public static ConfigEntry<bool> compatVcpSpawnQueue;
		public static ConfigEntry<bool> compatVcpUnload;
		public static ConfigEntry<bool> fixTeleportGhosts;
		public static ConfigEntry<bool> fixStationRange;
		public static ConfigEntry<bool> fixServerSmoke;
		public static ConfigEntry<bool> fixHandedOverFires;

		public static void Load(ConfigFile config)
		{
			RemoveRetiredSettings(config);
			modEnabled = config.Bind<bool>("General", "Enabled", true, "Enable or disable the mod");

			maxObjectsPerFrameEnabled = config.Bind<bool>("MaxObjectsPerFrame", "Enabled", true, "Enable or disable the feature");
			maxObjectsPerFrame = config.Bind<int>("MaxObjectsPerFrame", "MaxObjects", 100,
				new ConfigDescription("Maximum number of objects the server can create per frame. A vanilla dedicated server creates 100 (the game's loading-screen rate: a server has no player of its own); lower values make areas fill in more slowly.",
					new AcceptableValueRange<int>(1, 10000)));

			networkingEnabled = config.Bind<bool>("Networking", "Enabled", true,
				"Raise the limits on how fast the server sends world data to each player (server-side part of BetterNetworking). Needs a restart.");
			networkQueueSizeKB = config.Bind<int>("Networking", "QueueSizeKB", 48,
				new ConfigDescription("World data queued per player before sending pauses for that tick. A larger queue can delay new updates. Match the Steam send rate to the server's upload capacity and measure queue occupancy under load.",
					new AcceptableValueRange<int>(10, 80)));
			networkSendRateMinKB = config.Bind<int>("Networking", "SteamSendRateMinKB", 256,
				new ConfigDescription("Minimum rate Steam attempts to send to each player, KB/s. Valheim: 150. Keep it below the server's upload speed divided by the number of players.",
					new AcceptableValueRange<int>(64, 4096)));
			networkSendRateMaxKB = config.Bind<int>("Networking", "SteamSendRateMaxKB", 1024,
				new ConfigDescription("Maximum rate Steam sends to each player, KB/s. Valheim: 150.",
					new AcceptableValueRange<int>(64, 4096)));
			networkStatsMinutes = config.Bind<int>("Networking", "StatsIntervalMinutes", 5,
				"Every this many minutes, log per player how often their send queue was full. 0 disables.");

			consoleCommandsEnabled = config.Bind<bool>("Server", "ConsoleCommands", true,
				"Read commands from standard input: save, stop, players, give <item> <amount> <player>, broadcast <text>, event <name> <player>, events, help; anything else goes to the game's own console (kick, ban, unban, banned, stopevent, devcommands, skiptime ...). In AMP set App.HasWriteableConsole=True to type them into its console, and App.ExitMethod=String with App.ExitString=stop to shut down cleanly. On Windows also set [Logging.Console] Enabled = false in BepInEx.cfg, or BepInEx's own console takes over standard input.");
			maxGiveAmount = config.Bind<int>("Server", "MaxGiveAmount", 1000,
				"Most items one give command may drop.");
			consoleInputCodePage = config.Bind<int>("Server", "ConsoleInputCodePage", 1250,
				new ConfigDescription("How to read a console line that is not valid UTF-8 (UTF-8 is always tried first). Panels on Windows may write their code page instead: 1250 Central European (Czech), 852 Central European DOS, 1252 Western, 65001 UTF-8 only. The log says which bytes arrived the first time this is needed.",
					new AcceptableValueList<int>(1250, 852, 1252, 65001)));
			unityJobWorkers = config.Bind<int>("Server", "UnityJobWorkers", 8,
				"Upper limit on Unity job worker threads. Unity starts one per CPU core, and on many-core hosts the idle ones still use CPU. Only ever lowers the count. 0 leaves Unity's default.");

			sendIntervalMs = config.Bind<int>("Performance", "SendIntervalMs", 100,
				new ConfigDescription("How often each player is sent world updates, in real milliseconds. Valheim sends to one player per frame, so each player waits players+1 frames: at 15 FPS with 4 players ~330 ms. Every send builds that player's list of nearby objects, so shorter intervals cost server CPU. 0 keeps Valheim's behaviour.",
					new AcceptableValueRange<int>(0, 1000)));
			maxCatchUpMs = config.Bind<int>("Performance", "MaxCatchUpMs", 100,
				new ConfigDescription("Longest frame the server counts in full (Unity's maximum allowed timestep). After a slow frame Unity runs physics and every creature's fixed update again for each 20 ms it fell behind; Valheim allows 200 ms (10 steps), 100 caps it at 5, so one slow frame does not make the next one slow too. Game time runs slightly slower during such frames. Needs a restart. 0 keeps the game's setting.",
					new AcceptableValueRange<int>(0, 1000)));
			maxZonesPerTick = config.Bind<int>("Performance", "MaxZonesPerTick", 1,
				new ConfigDescription("Most new zones the server generates per zone tick (10 ticks a second), shared by all players in turn. A new zone is generated in full in one frame, so several players exploring at once used to cost one zone each in the same frame. 0 = one per player per tick, as before.",
					new AcceptableValueRange<int>(0, 100)));
			serverTargetFps = config.Bind<int>("Performance", "ServerTargetFps", 60,
				new ConfigDescription("Frame rate the server aims for. The game sets a dedicated server to 30, so even with time to spare a frame waits 33 ms; every reaction to a player (a hit, a felled tree) waits for a server frame. 60 halves that wait whenever the server has the headroom, and costs nothing when it has not. Needs a restart. 0 keeps the game's 30.",
					new AcceptableValueRange<int>(0, 240)));
			performanceStatsMinutes = config.Bind<int>("Performance", "StatsIntervalMinutes", 5,
				"Every this many minutes, log frame times, physics steps per frame, the cost of sending world updates and of generating zones. 0 disables.");

			fixSaveClientChanges = config.Bind<bool>("Fixes", "SaveClientChanges", true,
				"Mark a world chunk as changed when a player's own change to an object arrives, so the next save writes it. Valheim 1.0 only rewrites changed chunks and does not count changes received from players, so what a player just built or moved could be missing after a restart.");
			fixDungeonLoadGuard = config.Bind<bool>("Fixes", "DungeonLoadGuard", true,
				"Keep a dungeon whose room bundle fails to load from leaving its zone stuck loading. The game reports a failed bundle load as a success and then throws, so the dungeon never spawns. With this on, the mod reports the failure, the dungeon releases its zone, and the game tries the dungeon again the next time it is created. A vanilla dedicated server never loads dungeons. This mod does.");
			compatVcpSpawnQueue = config.Bind<bool>("Compat", "ValheimCommunityPatchSpawnQueue", false,
				"Only matters with ValheimCommunityPatch installed. Its spawn queue replaces ZNetScene.CreateObjectsSorted, orders new objects from the world origin, and waits for every player's area to load. Keeping it bypasses Serverbound's nearest-player ordering and independent area readiness. Off: that patch of ValheimCommunityPatch is removed when the world starts. On: it is kept.");
			compatVcpUnload = config.Bind<bool>("Compat", "ValheimCommunityPatchUnload", false,
				"Only matters with ValheimCommunityPatch installed. Its zone-diff unload replaces ZNetScene.RemoveObjects and, whenever the object lists look untouched, drops everything outside the simulation distance of the server's reference position -- the world origin on a dedicated server. With players whose areas do not overlap, objects around them are then destroyed and created again on every pass (dungeons reloading dozens of times a second, doors, beds and items that cannot be used). Off: that patch of ValheimCommunityPatch is removed when the world starts. On: it is kept, and this mod marks its object lists as edited on every pass so that patch takes the game's own unload check.");
			fixTeleportGhosts = config.Bind<bool>("Fixes", "TeleportGhosts", true,
				"Tell the players near the old spot to drop a player who teleported away. Valheim 1.0 checks whether an object left their area before it stores the new position, so the teleported player stayed there for them, frozen, until they next crossed a zone line.");
			fixStationRange = config.Bind<bool>("Fixes", "StationRange", true,
				"Keep the no-spawn area around each crafting station as wide as its build range, upgrades included. The game only sizes that area for a player on their own machine, so on this server a workbench kept its base 20 m however many upgrades it had, and a mod's larger build range never reached it.");
			fixServerSmoke = config.Bind<bool>("Fixes", "ServerSmoke", true,
				"Let fireplaces, fires and smelters make smoke on the server while a player is within 64 m of them, as they do on that player's machine. The game only makes smoke near its own local player, and a server has none, so smoke never blocked anything the server ran: a fireplace every player saw smothered kept burning fuel and lighting what touched it, a fire under a roof never choked, and a smelter, kiln or furnace with a blocked chimney kept working.");
			fixHandedOverFires = config.Bind<bool>("Fixes", "HandedOverFires", true,
				"Put out a fire that the server took over from a player once the piece, tree or log it burns on is gone. The game only tells the machine that lit a fire what it burns on, so a fire whose player left kept burning in mid-air on the server after its piece was destroyed.");
		}

		private static void RemoveRetiredSettings(ConfigFile config)
		{
			// BepInEx saves unbound entries. Bind and remove retired keys so upgrades discard them too.
			bool saveOnConfigSet = config.SaveOnConfigSet;
			// Leave an existing deferred-save policy alone. StartupAccelerator cancels its queued
			// save whenever this setter is called, even when the value does not change.
			if (saveOnConfigSet) config.SaveOnConfigSet = false;
			try
			{
				RemoveSection("CharacterGuard", new[] { "Enabled", "NewCharacters", "NewCharacterAction", "ChangedAway",
					"ExemptAdmins", "FreshEvent", "NewCharacterMessage", "ChangedAwayMessage" });
				RemoveSection("ItemLedger", new[] { "Mode", "Items", "ExtraItems", "ExcludeItems", "LogAllMovements",
					"ExemptAdmins", "GraceHours", "Message", "AltarMessage" });
			}
			finally
			{
				if (saveOnConfigSet) config.SaveOnConfigSet = true;
			}

			void RemoveSection(string section, string[] keys)
			{
				foreach (string key in keys)
				{
					var definition = new ConfigDefinition(section, key);
					config.Remove(definition);
					config.Bind(definition, string.Empty);
					config.Remove(definition);
				}
			}
		}
	}
}
