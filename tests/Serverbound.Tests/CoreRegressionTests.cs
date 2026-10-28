using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Serverbound;
using Serverbound.FeatureModel;
using Serverbound.Features;
using Serverbound.Patching;
using Serverbound.Settings;
using UnityEngine;

// Real routing, ZDO writes, save selection and patch lifecycle. Only native scene
// access and zone generation are supplied by the fixture.
internal static class CoreRegressionTests
{
    private const string Owner = "org.serverbound.valheim.Core";
    private static readonly List<int> visits = new List<int>();
    private static Ship? ship;
    private static int identifier;

    internal static void Run(Action<string, Action> test)
    {
        string? selected = Environment.GetEnvironmentVariable("SERVERBOUND_REGRESSION");

        void Case(string name, Action<Scene> action)
        {
            if (!string.IsNullOrEmpty(selected) && selected != "all" && !name.StartsWith(selected, StringComparison.Ordinal)) return;

            test("Core regression " + name, () =>
            {
                using (var scene = new Scene()) action(scene);
            });
        }

        Case("batch installation verifies Core after StartupAccelerator flushes", scene =>
        {
            string path = System.IO.Directory.GetFiles(System.IO.Path.Combine(Environment.GetEnvironmentVariable("SERVERBOUND_MODS_PATH")!,
                "BepInEx/patchers"), "StartupAccelerator.dll", System.IO.SearchOption.AllDirectories).Single();
            Type vendor = Assembly.LoadFrom(path).GetType("StartupAccelerator.StartupAccelerator", true)!;
            Serverbound.Compatibility.Guard.Build(vendor.Assembly, "489297E61EFA1698DBC616068D50BA8E70C290F8695EB1B08E15E1759E163790");
            Type batch = vendor.GetNestedType("InterceptChainloader", BindingFlags.NonPublic)!;
            MethodInfo wrapper = (MethodInfo)AccessTools.Field(vendor, "harmonyPatcher").GetValue(null);
            var pending = (HashSet<MethodBase>)AccessTools.Field(batch, "methods").GetValue(null);
            bool previousPhase = (bool)AccessTools.Field(batch, "postChainloader").GetValue(null);
            bool previousSkip = (bool)AccessTools.Field(batch, "doNotSkipUpdate").GetValue(null);
            Check(pending.Count == 0, "vendor has pending wrappers before the case");

            try
            {
                Set(batch, "doNotSkipUpdate", false);
                scene.Fixture.Patch(wrapper, prefix: new HarmonyMethod(AccessTools.Method(batch, "SkipUpdates")));
                Check(PatchCore(scene.Plugin), "Core rejected queued rewrites before the vendor could execute them");
                Check(pending.Count != 0, "the fixture did not queue real Harmony wrapper generation");
                Set(batch, "postChainloader", true);
                AccessTools.Method(batch, "Postfix").Invoke(null, null);
                Set(typeof(Plugin), "installed", true);
                AccessTools.Method(typeof(Plugin), "Start").Invoke(scene.Plugin, null);
                Check(HasCoreHooks(), "Core did not survive final verification after the vendor flush");
            }
            finally
            {
                Set(batch, "doNotSkipUpdate", true);
                scene.Fixture.Unpatch(wrapper, AccessTools.Method(batch, "SkipUpdates"));
                pending.Clear();
                Set(batch, "postChainloader", previousPhase);
                Set(batch, "doNotSkipUpdate", previousSkip);
            }
        });

        Case("ingress binds control responses to the sending connection", scene =>
        {
            Check(PatchCore(scene.Plugin), "supported original Core did not install");
            long observed = 0;
            scene.Router.Register<bool>("RequestRespons", (sender, granted) => observed = sender);
            scene.Ingress(100);
            Check(observed == 0, "a client delivered a control response claiming the server's identity");
            scene.Ingress(200);
            Check(observed == 200, "an authentic control response did not reach its handler");
        });

        Case("grants require a ship, its current owner and a ready onboard recipient", scene =>
        {
            scene.Feature.CreateClassProcessor(typeof(Core.ZRoutedRpc_RouteRPC_Patch)).Patch();
            // Bridge the native component lookup without replacing grant validation.
            scene.Fixture.Patch(AccessTools.Method(typeof(Core.ZRoutedRpc_RouteRPC_Patch), "Prefix"),
                transpiler: Hook(nameof(Components)));

            ZDO target = scene.Zdo(1, 100);
            ZNetView view = Fake<ZNetView>();
            view.m_zdo = target;
            ZNetScene.s_instance.m_instances[target] = view;
            Player player = Fake<Player>();
            player.m_nview = Fake<ZNetView>();
            player.m_nview.m_zdo = scene.Zdo(2, 200);
            ship = Fake<Ship>();
            ship.m_nview = view;
            Set(ship, "m_players", new List<Player> { player });

            scene.Grant(target, 200, 200);
            Check(target.GetOwner() == 100, "a requester transferred a ZDO without its owner's approval");

            ZPackage payload = scene.Grant(target, 100, 200);
            Check(target.GetOwner() == 200, "the owner could not transfer a ship to its ready onboard driver");
            Check(payload.GetPos() == 0, "grant inspection consumed the forwarded payload");

            target.SetOwner(100);
            ship = null;
            scene.Grant(target, 100, 200);
            Check(target.GetOwner() == 100, "a non-ship response transferred ownership");

            ship = Fake<Ship>();
            ship.m_nview = view;
            Set(ship, "m_players", new List<Player>());
            scene.Grant(target, 100, 200);
            Check(target.GetOwner() == 100, "an off-ship recipient received ownership");

            Set(ship, "m_players", new List<Player> { player });
            target.Set("InUse", 1);
            scene.Grant(target, 100, 200);
            Check(target.GetOwner() == 100, "a helm response bypassed occupied-cargo protection");
            target.Set("InUse", 0);
            scene.Router.m_peers.Clear();
            scene.Grant(target, 100, 200);
            Check(target.GetOwner() == 100, "a disconnected recipient received ownership");
        });

        Case("portal renames and received updates survive incremental save selection", scene =>
        {
            foreach (Type patch in typeof(Fixes).GetNestedTypes().Where(t => t.IsDefined(typeof(HarmonyPatch), false)))
                scene.Feature.CreateClassProcessor(patch).Patch();

            ZDO portal = scene.Zdo(1, 100);
            portal.Persistent = true;
            portal.SetPrefab(12345);
            Game.instance.PortalPrefabHash.Add(12345);
            scene.Manager.AddIfPortal(portal, 12345);
            ZNetView view = Fake<ZNetView>();
            view.m_zdo = portal;
            TeleportWorld world = Fake<TeleportWorld>();
            world.m_nview = view;

            scene.Manager.m_dirtyPortalObjects[0] = false;
            Configuration.fixSaveClientChanges.Value = false;
            world.RPC_SetTag(200, "unique-local", "actor");
            Game.instance.ConnectPortals();
            Check(SavesPortal(scene.Manager), "a server-owned rename is excluded when VCP owns received-update fixes");

            scene.Manager.BeginSave();
            world.RPC_SetTag(200, "during-save", "actor");
            scene.Manager.EndSave(true);
            scene.Manager.UpdateSaveState();
            Check(SavesPortal(scene.Manager), "a rename during saving disappeared from the next save");

            scene.Manager.m_dirtyPortalObjects[0] = false;
            Configuration.fixSaveClientChanges.Value = true;
            ZDO remote = scene.Zdo(2, 200);
            remote.Persistent = true;
            remote.SetPrefab(12345);
            remote.Set(ZDOVars.s_tag, "unique-remote");
            var data = new ZPackage();
            remote.Serialize(data);
            data.SetPos(0);
            scene.Manager.m_dirtyPortalObjects[0] = false;
            portal.Deserialize(data);
            scene.Manager.AddIfPortal(portal, 12345);
            Game.instance.ConnectPortals();
            Check(portal.GetString(ZDOVars.s_tag) == "unique-remote" && SavesPortal(scene.Manager), "received portal data is excluded from saving");
        });

        Case("fair zone generation visits each ready peer once per pass", scene =>
        {
            scene.Fixture.Patch(AccessTools.Method(typeof(ZoneSystem), "CreateLocalZones"), prefix: Hook(nameof(Generate)));
            var zones = Fake<ZoneSystem>();
            var peers = new List<ZNetPeer>();

            for (int i = 0; i < 3; i++) peers.Add(new ZNetPeer(new Socket(), false) { m_uid = i + 1, m_refPos = new Vector3(i, 0, 0) });

            MethodInfo spawn = AccessTools.Method(typeof(Core.ZoneSystem_Update_Patch), "SpawnZones");
            FieldInfo cursor = AccessTools.Field(typeof(Core.ZoneSystem_Update_Patch), "s_nextPeer");

            foreach (int budget in new[] { 2, 0, 1 })
            {
                Configuration.maxZonesPerTick.Value = budget;
                cursor.SetValue(null, 0);
                visits.Clear();

                for (int tick = 0; tick < 3; tick++) spawn.Invoke(null, new object[] { zones, peers, false });

                Check(new[] { 0, 1, 2 }.All(visits.Contains), "a ready peer starved with budget " + budget);
                if (budget == 0) Check(visits.Count == 9 && visits.GroupBy(x => x).All(g => g.Count() == 3), "unlimited mode revisited a peer in one pass");
            }
        });

        Case("Core installation rejects a missing mandatory rewrite", scene =>
        {
            scene.Fixture.Patch(AccessTools.Method(typeof(Pickable), "RPC_Pick"), transpiler: new HarmonyMethod(typeof(CoreRegressionTests), nameof(ChangeLocalPlayer)) { priority = Priority.First });
            bool applied = PatchCore(scene.Plugin);
            Check(!applied && !HasCoreHooks(), "Core reported installation success without its pickup rewrite");
        });

        Case("Core withdraws authority after a later mandatory rewrite mismatch", scene =>
        {
            Check(PatchCore(scene.Plugin), "supported original Core did not install");
            Set(typeof(Plugin), "installed", true);
            scene.Fixture.Patch(AccessTools.Method(typeof(Pickable), "RPC_Pick"), transpiler: new HarmonyMethod(typeof(CoreRegressionTests), nameof(ChangeLocalPlayer)) { priority = Priority.First });
            AccessTools.Method(typeof(Plugin), "Start").Invoke(scene.Plugin, null);
            Check(!HasCoreHooks(), "a later rewrite mismatch left Core authority installed");
        });

        Case("Core runtime verification removes its hooks after Start", scene =>
        {
            Configuration.performanceStatsMinutes.Value = 0;
            Check(PatchCore(scene.Plugin), "supported original Core did not install");
            Set(typeof(Plugin), "installed", true);
            AccessTools.Method(typeof(Plugin), "Start").Invoke(scene.Plugin, null);
            Check(HasCoreHooks(), "healthy startup removed Core");
            MethodInfo target = AccessTools.Method(typeof(Pickable), "RPC_Pick");
            scene.Fixture.Patch(target, transpiler: new HarmonyMethod(typeof(CoreRegressionTests), nameof(ChangeLocalPlayer)) { priority = Priority.First });
            AccessTools.Method(typeof(Plugin), "Update").Invoke(scene.Plugin, null);
            Check(!HasCoreHooks(), "a runtime mismatch retained Core until another startup");
            Check(Harmony.GetPatchInfo(target)!.Transpilers.Any(p => p.owner == scene.Fixture.Id), "Core rollback removed the foreign transpiler");
        });
    }

    private static bool PatchCore(Plugin plugin) => (bool)AccessTools.Method(typeof(Plugin), "PatchFeatures").Invoke(plugin, new object[]
    {
        new AvailableFeatures().AddFeature(new Core()), new HarmonyFeaturesPatcher(new PatchRequirements())
    });

    private static bool HasCoreHooks() => Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains(Owner));
    private static bool SavesPortal(ZDOMan manager) => manager.GetSaveClonePerChunk().Any(c => c.Item1.Equals(ZoneSystem.ChunkPortal));
    private static bool Skip() => false;
    private static bool Generate(Vector3 __0, ref bool __result) { visits.Add((int)__0.x); __result = true; return false; }
    private static Ship? ShipFor(Component _) => ship;

    private static IEnumerable<CodeInstruction> Components(IEnumerable<CodeInstruction> input) => input.Select(i =>
        i.operand is MethodInfo m && m.Name == "GetComponent" && m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(Ship)
            ? new CodeInstruction(i) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(CoreRegressionTests), nameof(ShipFor)) } : i);

    private static IEnumerable<CodeInstruction> ChangeLocalPlayer(IEnumerable<CodeInstruction> input)
    {
        var code = input.Select(i => new CodeInstruction(i)).ToList();
        FieldInfo local = AccessTools.Field(typeof(Player), "m_localPlayer");
        int site = code.FindIndex(i => i.LoadsField(local));
        Check(site >= 0, "conflicting fixture found no original local-player read");
        code[site].opcode = OpCodes.Call;
        code[site].operand = AccessTools.Method(typeof(CoreRegressionTests), nameof(NoPlayer));
        return code;
    }

    private static Player? NoPlayer() => null;
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(CoreRegressionTests), name);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Set(object target, string field, object? value) => AccessTools.Field(target is Type t ? t : target.GetType(), field).SetValue(target is Type ? null : target, value);

    private static T Fake<T>() where T : UnityEngine.Object
    {
        var instance = (T)FormatterServices.GetUninitializedObject(typeof(T));
        Set(instance, "m_CachedPtr", new IntPtr(++identifier));
        return instance;
    }

    private sealed class Scene : IDisposable
    {
        internal readonly Harmony Fixture = new Harmony("Serverbound.Tests.Core.Scene");
        internal readonly Harmony Feature = new Harmony(Owner);
        internal readonly ZDOMan Manager;
        internal readonly ZRoutedRpc Router;
        internal readonly Plugin Plugin;
        private readonly ZNet net = ZNet.m_instance;
        private readonly ZDOMan manager = ZDOMan.s_instance;
        private readonly ZNetScene scene = ZNetScene.s_instance;
        private readonly Game game = Game.instance;
        private readonly ZRoutedRpc router = ZRoutedRpc.s_instance;
        private readonly bool server = ZNet.m_isServer;
        private readonly Socket outbound = new Socket();
        private readonly ZNetPeer inbound;

        internal Scene()
        {
            ZDOExtraData.Init();
            Plugin.logger = new ManualLogSource("Core regression");
            Plugin.harmony = new Harmony(Plugin.PluginGUID);
            Plugin = Fake<Plugin>();
            Set(Plugin, "<Logger>k__BackingField", Serverbound.Plugin.logger);
            var config = new ConfigFile(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox/core-regression.cfg"), false) { SaveOnConfigSet = false };
            Configuration.Load(config);
            Fixture.Patch(AccessTools.Method(typeof(ZLog), "Log", new[] { typeof(object) }), prefix: Hook(nameof(Skip)));

            ZNet.m_instance = Fake<ZNet>();
            ZNet.m_isServer = true;
            Game instance = Fake<Game>();
            Set(typeof(Game), "<instance>k__BackingField", instance);
            Set(instance, "<PortalPrefabHash>k__BackingField", new List<int>());
            Set(instance, "m_currentlyConnectingPortals", Activator.CreateInstance(AccessTools.Field(typeof(Game), "m_currentlyConnectingPortals").FieldType));
            Manager = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
            ZDOMan.s_instance = Manager;
            Set(Manager, "m_sessionID", 100L);
            Set(Manager, "m_objectsByID", new Dictionary<ZDOID, ZDO>());
            Set(Manager, "m_dirtyPortalObjects", new bool[2]);
            Set(Manager, "m_dirtyChunks", new[] { new HashSet<ZoneSystem.ChunkIndex>(), new HashSet<ZoneSystem.ChunkIndex>() });
            Set(Manager, "m_objectsBySector", new List<ZDO>[512 * 512]);
            Set(Manager, "m_portalObjects", new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>());
            Set(Manager, "m_saveData", Activator.CreateInstance(AccessTools.Field(typeof(ZDOMan), "m_saveData").FieldType, true));
            ZNetScene.s_instance = Fake<ZNetScene>();
            Set(ZNetScene.s_instance, "m_instances", new Dictionary<ZDO, ZNetView>());
            Router = new ZRoutedRpc(true);
            Router.SetUID(100);
            var socket = new Socket();
            socket.Other = outbound;
            outbound.Other = socket;
            inbound = new ZNetPeer(socket, false) { m_uid = 200 };
            Router.AddPeer(inbound);
        }

        internal ZDO Zdo(uint id, long owner)
        {
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = new ZDOID(100, id);
            zdo.SetOwnerInternal(owner);
            Manager.m_objectsByID.Add(zdo.m_uid, zdo);
            return zdo;
        }

        internal ZPackage Grant(ZDO target, long sender, long recipient)
        {
            var rpc = new ZRoutedRpc.RoutedRPCData { m_senderPeerID = sender, m_targetPeerID = recipient,
                m_targetZDO = target.m_uid, m_methodHash = "RequestRespons".GetStableHashCode() };
            rpc.m_parameters.Write(true);
            rpc.m_parameters.SetPos(0);
            // Exercise the route boundary for local owner responses and forwarded grants.
            Router.RouteRPC(rpc);
            return rpc.m_parameters;
        }

        internal void Ingress(long sender)
        {
            var data = new ZRoutedRpc.RoutedRPCData { m_senderPeerID = sender, m_targetPeerID = 100,
                m_methodHash = "RequestRespons".GetStableHashCode() };
            data.m_parameters.Write(true);
            var packet = new ZPackage();
            data.Serialize(packet);
            new ZRpc(outbound).Invoke("RoutedRPC", packet);
            Check(inbound.m_rpc.Update(0) == ZRpc.ErrorCode.Success, "routed ingress failed");
        }

        public void Dispose()
        {
            Feature.UnpatchSelf();
            Fixture.UnpatchSelf();
            ZNet.m_instance = net;
            ZNet.m_isServer = server;
            ZDOMan.s_instance = manager;
            ZNetScene.s_instance = scene;
            Set(typeof(Game), "<instance>k__BackingField", game);
            ZRoutedRpc.s_instance = router;
            Set(typeof(Plugin), "installed", false);
            ship = null;
        }
    }

    private sealed class Socket : ISocket
    {
        internal readonly Queue<ZPackage> Incoming = new Queue<ZPackage>();
        internal Socket Other = null!;
        public bool IsConnected() => true;
        public void Send(ZPackage pkg) => Other.Incoming.Enqueue(new ZPackage(pkg.GetArray()));
        public ZPackage? Recv() => Incoming.Count == 0 ? null : Incoming.Dequeue();
        public int GetSendQueueSize() => 0;
        public int GetCurrentSendRate() => 0;
        public bool IsHost() => false;
        public void Dispose() { }
        public bool GotNewData() => Incoming.Count != 0;
        public void Close() { }
        public string GetEndPointString() => "fixture";
        public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
        public void GetConnectionQuality(out float local, out float remote, out int ping, out float sent, out float received)
        { local = remote = sent = received = 0; ping = 0; }
        public ISocket? Accept() => null;
        public int GetHostPort() => 0;
        public bool Flush() => true;
        public string GetHostName() => "fixture";
        public void VersionMatch() { }
    }
}
