#nullable disable
using BepInEx;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using Serverbound.Compatibility;
using System.Linq;
using System.Reflection;
using Serverbound.FeatureModel;
using HarmonyLib;
using Serverbound.Patching;
using Serverbound.Settings;
using Serverbound.Requirements;
using System;
using System.Collections.Generic;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace Serverbound
{

	[Harmony]
	[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
	[BepInDependency(ValheimPlusPluginId, BepInDependency.DependencyFlags.SoftDependency)]
	[BepInDependency("MidnightsFX.ImpactfulSkills", BepInDependency.DependencyFlags.SoftDependency)]
	[BepInDependency("dev.ontrigger.vpo", BepInDependency.DependencyFlags.SoftDependency)]
	[BepInDependency("MidnightsFX.ValheimCommunityPatch", BepInDependency.DependencyFlags.SoftDependency)]
	[BepInDependency("DeepNorthCompat", BepInDependency.DependencyFlags.SoftDependency)]
	[BepInIncompatibility(CompatibilityInstaller.LegacyGuid)]

	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGUID = "org.serverbound.valheim";
		public const string PluginName = "Serverbound";
		public const string PluginVersion = "0.1.0";

		private static Plugin context;
		public static Plugin instance => context;

		public static Configuration configuration;

		public static Harmony harmony;

		public const string ValheimPlusPluginId = "org.bepinex.plugins.valheim_plus";

		public static ManualLogSource logger;

		private void Awake()
		{
			context = this;
			logger = Logger;

			Configuration.Load(Config);
			CompatibilityInstaller.Info = message => Logger.LogInfo(message);
			CompatibilityInstaller.Warning = message => Logger.LogWarning(message);
			CompatibilityInstaller.Error = message => Logger.LogError(message);
			try
			{
				CompatibilityInstaller.CheckInstallation(Resolve, VersionOf, IsDedicated());
				Guard.Build(typeof(ZNet).Assembly, IsDedicated() ? ExpectedBuilds.Server : ExpectedBuilds.Valheim);
			}
			catch (Exception error)
			{
				Logger.LogError("Serverbound: REJECTED BUILD/INSTALLATION; no patches installed. " + error);
				return;
			}

			if (!ModIsEnabled())
			{
				Logger.LogInfo($"{PluginName} is disabled. (configuration)");
				return;
			}
			else if (!IsDedicated())
			{
				if (Config.Bind("Compatibility", "ClientSkills", true,
					"Publish ImpactfulSkills state only after a Serverbound server handshake. Dedicated simulation never runs on clients.").Value)
					CompatibilityInstaller.Install(Resolve, CompatibilityInstaller.Info, CompatibilityInstaller.Warning, CompatibilityInstaller.Error);
				Logger.LogInfo("Serverbound client compatibility loaded; dedicated simulation is inactive on this client.");
				return;
			}
			Logger.LogInfo($"Installing {PluginName}");

			// Independent of the patches, so they stay on even if Core fails to apply and the server runs vanilla.
			LimitJobWorkers(Configuration.unityJobWorkers.Value);
			LimitPhysicsCatchUp(Configuration.maxCatchUpMs.Value);
			if (Configuration.consoleCommandsEnabled.Value)
			{
				ServerConsole.Start();
				consoleStarted = true;
			}

			harmony = new Harmony(PluginGUID);

			AvailableFeatures availableFeatures = new AvailableFeatures();
			availableFeatures.AddFeature(new Features.Core());
			availableFeatures.AddFeature(new Features.MaxObjectsPerFrame());
			availableFeatures.AddFeature(new Features.Networking());
			availableFeatures.AddFeature(new Features.Performance());
			availableFeatures.AddFeature(new Features.Fixes());
			availableFeatures.AddFeature(new Features.Debugging());
			availableFeatures.AddFeature(new Features.Compat_ValheimCommunityPatch());
			availableFeatures.AddFeature(new Features.DungeonLoadGuard());
			availableFeatures.AddFeature(new Features.CharacterGuard());
			availableFeatures.AddFeature(new Features.ItemLedger());

			PatchRequirements patchRequirements = new PatchRequirements();
			patchRequirements.AddRequirement(new PatchRequirement.DebugBuild());

			if (!PatchFeatures(availableFeatures, new HarmonyFeaturesPatcher(patchRequirements)))
			{
				return;
			}

			VanillaDrift.Check(Logger);
			installed = true;
			CompatibilityInstaller.Install(Resolve, CompatibilityInstaller.Info, CompatibilityInstaller.Warning, CompatibilityInstaller.Error);
			Logger.LogInfo($"{PluginName} installed; deferred compatibility verification pending.");
		}

		private static Assembly Resolve(string guid) => Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info)
			? info.Instance?.GetType().Assembly : null;
		private static System.Version VersionOf(string guid) => Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info)
			? info.Metadata.Version : null;

		private void Start()
		{
			try
			{
				CompatibilityInstaller.CheckInstallation(Resolve, VersionOf, IsDedicated());
				CompatibilityInstaller.Verify();
			}
			catch (Exception error)
			{
				Logger.LogError("Serverbound: deferred compatibility gate failed. " + error);
				DisableSimulation();
			}
		}

		internal static void DisableSimulation()
		{
			installed = false;
			SimulationPatch.Deactivate();
			try
			{
				string[] owners = Harmony.GetAllPatchedMethods().SelectMany(SimulationPatch.All).Select(p => p.owner)
					.Where(IsSimulationOwner).Distinct().ToArray();
				foreach (string owner in owners) new Harmony(owner).UnpatchSelf();
				if (Harmony.GetAllPatchedMethods().SelectMany(SimulationPatch.All).Any(p => IsSimulationOwner(p.owner)))
					throw new InvalidOperationException("Serverbound hooks remain installed.");
				SimulationPatch.RestoreVendors();
				CompatibilityInstaller.Error("Simulation: SERVERBOUND DISABLED; resolve the rejected integration before admitting players.");
			}
			catch (Exception error)
			{
				CompatibilityInstaller.Error("Simulation: rollback could not disable Serverbound; stop the server before players join. " + error);
			}
		}
		private static bool IsSimulationOwner(string owner) => (owner == PluginGUID || owner.StartsWith(PluginGUID + ".", StringComparison.Ordinal))
			&& owner != PluginGUID + ".ServerConsole";

		private static bool consoleStarted;
		private static bool installed;

		private void Update()
		{
			if (consoleStarted)
			{
				ServerConsole.ProcessPending();
			}
			if (installed)
			{
				Features.PerformanceStats.Frame();
				Safely(Features.CharacterGuard.Tick, "Character guard");
				Safely(Features.ItemLedger.Tick, "Item ledger");
			}
		}

		// One feature's fault must not stop the other's tick, nor fill the log every frame.
		private int tickErrors;
		private float tickErrorsSince;

		private void Safely(System.Action tick, string feature)
		{
			try
			{
				tick();
			}
			catch (Exception e)
			{
				if (Time.realtimeSinceStartup - tickErrorsSince > 900f)
				{
					tickErrorsSince = Time.realtimeSinceStartup;
					tickErrors = 0;
				}
				if (tickErrors++ < 20)
				{
					Logger.LogWarning($"{feature}: {e}");
				}
			}
		}

		private void FixedUpdate()
		{
			if (installed)
			{
				Features.PerformanceStats.FixedStep();
			}
		}

		/*
			Unity starts a job worker thread per CPU core (63 on a 64-thread host) and the idle ones
			still spin. Measured on a 24-thread machine, an idle server used 108% of a core with the
			default 23 workers and 31% with 4. Only ever lowers the count.
		*/
		private void LimitJobWorkers(int limit)
		{
			int current = JobsUtility.JobWorkerCount;
			if (limit <= 0 || limit >= current)
			{
				return;
			}
			JobsUtility.JobWorkerCount = limit;
			Logger.LogInfo($"Unity job worker threads: {current} -> {JobsUtility.JobWorkerCount}");
		}

		/*
			After a slow frame Unity runs the fixed update -- physics and, in Valheim, every character,
			creature AI and synced object (MonoUpdaters.FixedUpdate) -- once per fixed step it fell
			behind, up to the maximum allowed timestep: Valheim ships 200 ms, 10 steps. On a server
			that already has little headroom, the catch-up makes the next frame slow as well. A lower
			limit ends that spiral; the price is game time running slightly slow during such frames.
		*/
		private void LimitPhysicsCatchUp(int milliseconds)
		{
			if (milliseconds <= 0)
			{
				return;
			}
			float before = Time.maximumDeltaTime;
			Time.maximumDeltaTime = Mathf.Max(milliseconds / 1000f, Time.fixedDeltaTime);
			Logger.LogInfo($"Physics catch-up: at most {Mathf.RoundToInt(Time.maximumDeltaTime / Time.fixedDeltaTime)} fixed steps per frame "
				+ $"(fixed step {1000 * Time.fixedDeltaTime:0} ms, longest frame counted {1000 * before:0} -> {1000 * Time.maximumDeltaTime:0} ms)");
		}

		/*
			Each feature is patched through its own Harmony instance so a failure can be undone
			cleanly. A patch that fails usually means the game changed under it. Half of Core is
			worse than none -- e.g. objects created around players while zones are not -- so a
			Core failure removes every patch and leaves the server vanilla. Any other feature is
			just switched off.
		*/
		private bool PatchFeatures(AvailableFeatures availableFeatures, HarmonyFeaturesPatcher patcher)
		{
			List<Harmony> applied = new List<Harmony>();
			foreach (IFeature feature in availableFeatures.EnabledFeatures())
			{
				string featureName = feature.GetType().Name;
				Harmony featureHarmony = new Harmony($"{PluginGUID}.{featureName}");
				try
				{
					patcher.PatchAll(feature.GetType().GetNestedTypes(), featureHarmony);
					applied.Add(featureHarmony);
				}
				catch (Exception e)
				{
					featureHarmony.UnpatchSelf();
					if (feature is Features.Core)
					{
						Logger.LogError($"Core patches failed to apply; {PluginName} is disabled and the server runs vanilla. {e}");
						foreach (Harmony instance in applied)
						{
							instance.UnpatchSelf();
						}
						harmony.UnpatchSelf();
						return false;
					}
					Logger.LogError($"Feature {featureName} failed to apply and is disabled. {e}");
				}
			}
			return true;
		}

		public bool ModIsEnabled()
		{
			return Configuration.modEnabled.Value;
		}

		public static bool IsDedicated()
		{
			return new ZNet().IsDedicated();
		}
	}

}
