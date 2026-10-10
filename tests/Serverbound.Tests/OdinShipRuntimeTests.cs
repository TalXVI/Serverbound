using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Serverbound;
using Serverbound.Compatibility;
using UnityEngine;
using Object = UnityEngine.Object;

// Native scene access and network delivery are fixtures. Player IDs, ownership,
// RPC registration/serialization, vendor methods, and authorization run unchanged.
internal static class OdinShipRuntimeTests
{
    private const string FixtureOwner = "Serverbound.Tests.OdinShip.Scene";
    private const string ChangeRpc = "Serverbound.OdinShip.Change";
    private const string ResultRpc = "Serverbound.OdinShip.Result";
    private static Scene? current;
    private static int identifier;

    internal static void Run(Assembly vendor, Action<string, Action> test)
    {
        void Case(string name, Action<Scene> action) => test("OdinShip runtime " + name, () =>
        {
            using (var scene = new Scene(vendor)) action(scene);
        });

        Case("resolves peer ownership separately from profile and helm IDs", scene =>
        {
            Check(Player.GetPlayer(100) == scene.OtherPlayer, "fixture must distinguish profile IDs from peer IDs");
            Check(scene.Authorize(100, out Player? player, out _) && player == scene.Requester,
                "connected requester was looked up by profile ID instead of network ownership");
            scene.View.GetZDO().Set(ZDOVars.s_user, scene.Requester.GetPlayerID());
            Check(scene.Authorize(100, out _, out _), "matching helm profile ID was rejected");
            Check(!scene.Authorize(200, out _, out string helm) && helm.Contains("helm"), "different helm user was allowed");
            scene.View.GetZDO().Set(ZDOVars.s_user, 0L);
            scene.Peer(100);
            scene.View.GetZDO().SetOwnerInternal(100);
            Check(scene.Authorize(100, out _, out _), "local owner readiness used its peer ID as a profile ID");
        });

        Case("rejects disconnected, dead, distant, occupied-cargo and stale-owner requests", scene =>
        {
            Check(!scene.Authorize(999, out _, out string unknown) && unknown.Contains("connection"), "unknown peer accepted");
            scene.SetDead(true);
            Check(!scene.Authorize(100, out _, out string dead) && dead.Contains("alive"), "dead requester accepted");
            scene.SetDead(false);
            scene.Place(scene.Requester, new Vector3(9, 0, 0));
            Check(!scene.Authorize(100, out _, out string distant) && distant.Contains("closer"), "distant requester accepted");
            scene.Place(scene.Requester, Vector3.zero);
            scene.View.GetZDO().Set("InUse", 1);
            Check(!scene.Authorize(100, out _, out string cargo) && cargo.Contains("cargo"), "another peer's open cargo was ignored");
            scene.View.GetZDO().Set("InUse", 0);
            scene.View.GetZDO().SetOwnerInternal(777);
            Check(!scene.Authorize(100, out _, out string stale) && stale.Contains("ownership changed"), "stale owner accepted a write");
            scene.View.GetZDO().SetOwnerInternal(900);
            ZRoutedRpc.instance.m_peers.RemoveAll(peer => peer.m_uid == 100);
            Check(!scene.Authorize(100, out _, out string disconnected) && disconnected.Contains("connection"), "disconnected actor retained authority");
        });

        Case("client owners accept loaded requesters through the ready server connection", scene =>
        {
            scene.Peer(200);
            scene.View.GetZDO().SetOwnerInternal(200);
            ZRoutedRpc router = ZRoutedRpc.instance;
            router.m_server = false;
            router.m_peers.RemoveAll(peer => peer.m_uid != 900);
            router.m_peers.Single().m_server = true;
            Call("EnsureRpcRegistration", scene.View);
            scene.Deliver(100, ChangeRpc, 1, 2);
            Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 1
                && scene.Messages.Single(message => message.Name == ResultRpc).Target == 100,
                "client owner required a direct connection to the remote requester");
            Check(!scene.Authorize(999, out _, out _), "unloaded requester accepted through the server connection");
            Player.GetAllPlayers().Remove(scene.Requester);
            Check(!scene.Authorize(100, out _, out _), "removed requester retained authority on the client owner");
            Player.GetAllPlayers().Add(scene.Requester);
            router.m_peers.Single().m_uid = 0;
            Check(!scene.Authorize(100, out _, out _), "unready server connection allowed the request");
            router.m_peers.Clear();
            Check(!scene.Authorize(100, out _, out _), "disconnected client owner allowed the request");
        });

        Case("routes customization to the owner, authenticates results and reports timeouts", scene =>
        {
            scene.View.GetZDO().Set("odinship_sail_index", 2);
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            Check(!scene.View.IsOwner(), "fixture requester owns the ship: local=" + ZNet.GetUID() + ", session=" + ZDOMan.GetSessionID() + ", owner=" + scene.View.GetZDO().GetOwner());
            Check((bool)Call("CanUseCustomizationInputs", scene.View)!, "requester cannot reach customization input");
            scene.CycleSail();
            Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 2, "non-owner wrote the index");
            Check(scene.Messages.Count == 1, "request was not sent: " + string.Join("; ", scene.Notifications.Concat(scene.Warnings)));
            var request = scene.Messages.Single();
            Check(request.Target == 900 && request.Name == ChangeRpc && (int)request.Args[1] == 2, "request did not target the current ship owner");
            Check(scene.PendingChanges == 1, "request was not tracked");
            int registrations = scene.RegistrationCount;
            Call("EnsureRpcRegistration", scene.View);
            Call("EnsureRpcRegistration", scene.View);
            Check(scene.RegistrationCount == registrations, "ship RPC registration duplicated");

            scene.Messages.Clear();
            scene.Peer(900);
            Player.m_localPlayer = null;
            scene.Deliver(100, request.Name, request.Args);
            Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 0 && scene.View.GetZDO().GetOwner() == 900,
                "owner did not cycle its authoritative index without transferring ownership");
            Check(scene.Messages.Count(message => message.Name == "OnCustomizationChanged" && message.Target == ZNetView.Everybody) == 1,
                "customization refresh was not broadcast once");
            var reply = scene.Messages.Single(message => message.Name == ResultRpc);
            Check(reply.Target == 100 && (bool)reply.Args[1], "accepted result did not target the requester");

            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            int notifications = scene.Notifications.Count;
            scene.Deliver(999, reply.Name, reply.Args);
            Check(scene.PendingChanges == 1 && scene.Notifications.Count == notifications, "forged reply cleared the request");
            scene.Deliver(900, reply.Name, reply.Args);
            Check(scene.PendingChanges == 0 && scene.Notifications.Count == notifications + 1, "owner reply did not complete the request");
            scene.Deliver(900, reply.Name, reply.Args);
            Check(scene.Notifications.Count == notifications + 1, "duplicate reply displayed twice");
            scene.CycleSail();
            scene.Clock += 6;
            OdinShipOwnershipPatch.Update();
            Check(scene.PendingChanges == 0 && scene.Notifications.Last().Contains("did not respond"), "missing owner reply did not time out visibly");
        });

        Case("enforces each overlapping ward with the sender's profile ID at the receiver", scene =>
        {
            Call("EnsureRpcRegistration", scene.View);
            scene.AddWard(scene.Requester.GetPlayerID());
            PrivateArea deniedWard = scene.AddWard(55555);
            scene.Permit(deniedWard, 100); // A network peer ID is not ward permission.
            scene.Deliver(100, ChangeRpc, 1, 2);
            Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 0 && scene.Messages.Count == 1
                && !(bool)scene.Messages[0].Args[1] && ((string)scene.Messages[0].Args[2]).Contains("ward"),
                "an allowed overlapping ward or peer ID bypassed the denying ward");
            scene.Messages.Clear();
            scene.Deliver(100, "RPC_SetShipName", "blocked");
            Check(scene.View.GetZDO().GetString("shipName") == string.Empty && scene.Messages.Count == 1
                && !(bool)scene.Messages[0].Args[1], "ward-denied rename wrote or refreshed visuals");
            scene.Messages.Clear();
            scene.Permit(deniedWard, scene.Requester.GetPlayerID());
            scene.Deliver(100, ChangeRpc, 2, 2);
            Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 1
                && scene.Messages.Single(message => message.Name == ResultRpc).Args[1].Equals(true),
                "explicit profile permission did not authorize the receiver");
        });

        Case("routes turret mode and rejects invalid actions and ownership handoff", scene =>
        {
            scene.View.GetZDO().Set("odinship_turret_mode", 2);
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            Check(!(bool)Call("TryRouteTurretMode", scene.View)!, "non-owner entered the vendor's direct-write branch");
            Check(scene.View.GetZDO().GetInt("odinship_turret_mode") == 2, "requesting peer wrote the turret mode");
            var request = scene.Messages.Single();
            Check((int)request.Args[1] == 5, "wrong turret action");
            scene.Peer(900);
            Player.m_localPlayer = null;
            scene.Messages.Clear();
            scene.Deliver(100, ChangeRpc, request.Args);
            Check(scene.View.GetZDO().GetInt("odinship_turret_mode") == 0 && (bool)scene.Messages.Single().Args[1], "owner did not wrap turret mode");
            scene.Messages.Clear();
            scene.Deliver(100, ChangeRpc, 88, 999);
            Check(!(bool)scene.Messages.Single().Args[1] && scene.View.GetZDO().GetInt("odinship_turret_mode") == 0, "unsupported action changed state");
            scene.Messages.Clear();
            scene.View.GetZDO().SetOwnerInternal(777);
            scene.Deliver(100, ChangeRpc, 89, 5);
            Check(!(bool)scene.Messages.Single().Args[1] && scene.View.GetZDO().GetInt("odinship_turret_mode") == 0, "previous owner wrote after handoff");
        });

        Case("validates the vendor rename RPC without adding a visual broadcast", scene =>
        {
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            scene.SubmitName("  " + new string('A', 35) + "  ");
            var request = scene.Messages.Single();
            Check(request.Target == 900 && request.Name == "RPC_SetShipName", "rename did not retain the vendor RPC path");
            Check(scene.PendingNames == 1, "rename was not tracked");
            scene.Peer(900);
            Player.m_localPlayer = null;
            scene.Messages.Clear();
            scene.Deliver(100, request.Name, request.Args);
            Check(scene.View.GetZDO().GetString("shipName") == new string('A', 30), "vendor name sanitization was changed");
            Check(scene.Messages.Count(message => message.Name == "OnCustomizationChanged") == 1
                && scene.Messages.Single(message => message.Name == "OnCustomizationChanged").Target == 900,
                "Serverbound added a rename visual broadcast instead of leaving it to DeepNorthCompat");
            Check(scene.Messages.Count(message => message.Name == ResultRpc && message.Target == 100 && (bool)message.Args[1]) == 1,
                "accepted rename did not return one result");
            var reply = scene.Messages.Single(message => message.Name == ResultRpc);
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            int notifications = scene.Notifications.Count;
            scene.Deliver(999, reply.Name, reply.Args);
            Check(scene.PendingNames == 1 && scene.Notifications.Count == notifications, "forged name result consumed the request");
            scene.Deliver(900, reply.Name, reply.Args);
            scene.Deliver(900, reply.Name, reply.Args);
            Check(scene.PendingNames == 0 && scene.Notifications.Count == notifications + 1, "name result was not consumed once");
            scene.Peer(900);
            Player.m_localPlayer = null;
            scene.Messages.Clear();
            scene.View.GetZDO().Set(ZDOVars.s_user, scene.Requester.GetPlayerID());
            scene.Deliver(200, "RPC_SetShipName", "unauthorized");
            Check(scene.View.GetZDO().GetString("shipName") == new string('A', 30)
                && scene.Messages.Count == 1 && !(bool)scene.Messages[0].Args[1], "unauthorized rename wrote or refreshed visuals");
        });

        Case("uses BigCargoShip controls for nearby requests and immediate owner rename results", scene =>
        {
            Vector3 controls = new Vector3(0.62f, 2.486492f, -8.108612f);
            Vector3 attach = new Vector3(0.666f, 2.171648f, -7.930571f);
            scene.Place(scene.Ship.m_shipControlls, controls);
            foreach (Vector3 position in new[] { controls, attach })
            {
                scene.Place(scene.Requester, position);
                Check(scene.Authorize(100, out _, out _), "measured BigCargoShip helm position was rejected");
                scene.Peer(100);
                Player.m_localPlayer = scene.Requester;
                Check((bool)Call("CanUseCustomizationInputs", scene.View)!, "local helm input used the ship root");
                scene.Peer(900);
            }
            scene.Peer(100);
            scene.View.GetZDO().SetOwnerInternal(100);
            Player.m_localPlayer = scene.Requester;
            scene.SynchronousLocalDispatch = true;
            Check(scene.RegistrationCount == 1, "fixture already registered the result handler");
            scene.SubmitName("  Cargo name  ");
            Check(scene.NamesAtDispatch == 1 && scene.PendingNames == 0, "owner result arrived before tracking or registration");
            Check(scene.View.GetZDO().GetString("shipName") == "Cargo name"
                && scene.Notifications.Count(message => message == "Ship name updated.") == 1, "owner rename lacked a write or one completion");
            int registrations = scene.RegistrationCount;
            scene.SubmitName("second name");
            Check(scene.RegistrationCount == registrations && scene.PendingNames == 0, "owner rename registered twice");
            scene.AddWard(55555);
            scene.SubmitName("blocked name");
            Check(scene.View.GetZDO().GetString("shipName") == "second name" && scene.PendingNames == 0
                && scene.Notifications.Last().Contains("ward"), "owner rejection was silent or wrote the name");
        });

        Case("uses WarShip turret geometry independently of the controls", scene =>
        {
            Vector3 helm = new Vector3(-0.491f, 1.229f, -7.343f);
            Vector3 turret = new Vector3(0, 1.753f, 6.179f);
            scene.Place(scene.Ship.m_shipControlls, helm);
            scene.Place(scene.turrets[0], turret);
            scene.Place(scene.Requester, turret);
            Check(scene.Authorize(100, out _, out _, turret: true), "turret mode used the controls anchor");
            Check(!scene.Authorize(100, out _, out _), "controls action used the turret anchor");
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            Call("TryRouteTurretMode", scene.View);
            var request = scene.Messages.Single();
            scene.Messages.Clear();
            scene.Peer(900);
            Player.m_localPlayer = null;
            scene.Deliver(100, request.Name, request.Args);
            Check(scene.View.GetZDO().GetInt("odinship_turret_mode") == 1 && (bool)scene.Messages.Single().Args[1], "turret request was rejected");
            scene.Place(scene.Requester, helm);
            Check(!scene.Authorize(100, out _, out _, turret: true), "turret mode fell back to the root or controls");
            Check(scene.Authorize(100, out _, out _), "helm customization was rejected");
        });

        Case("checks the eight-metre 3D boundary after ship translation and rotation", scene =>
        {
            Vector3 origin = new Vector3(100, 20, -50);
            Quaternion rotation = new Quaternion(0, 0, 1, 0);
            Vector3 anchor = origin + rotation * new Vector3(2, 3, -9);
            scene.Place(scene.View, origin);
            scene.Place(scene.Ship.m_shipControlls, anchor);
            scene.Place(scene.Requester, anchor + Vector3.up * 8);
            Check(scene.Authorize(100, out _, out _), "exactly eight metres was rejected");
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            Check((bool)Call("CanUseCustomizationInputs", scene.View)!, "local and receiver range disagree");
            scene.Place(scene.Requester, anchor + Vector3.up * 8.01f);
            Check(!(bool)Call("CanUseCustomizationInputs", scene.View)!, "local check ignored the vertical boundary");
            scene.Peer(900);
            Check(!scene.Authorize(100, out _, out _), "receiver ignored the vertical boundary");
        });

        Case("rejects missing and foreign interaction components without a root fallback", scene =>
        {
            ShipControlls controls = scene.Ship.m_shipControlls;
            scene.Ship.m_shipControlls = null!;
            Check(!scene.Authorize(100, out _, out _), "missing controls used the ship root");
            scene.Ship.m_shipControlls = controls;
            controls.m_ship = scene.Fake<Ship>();
            Check(!scene.Authorize(100, out _, out _), "foreign controls ship accepted");
            controls.m_ship = scene.Ship;
            scene.parentViews[controls] = scene.NewView(900);
            Check(!scene.Authorize(100, out _, out _), "foreign controls view accepted");
            scene.parentViews[controls] = scene.View;
            AccessTools.Field(scene.customizationType, "m_nview").SetValue(scene.customization, scene.NewView(900));
            Check(!scene.Authorize(100, out _, out _), "foreign customization view accepted");
            AccessTools.Field(scene.customizationType, "m_nview").SetValue(scene.customization, scene.View);
            scene.parentViews[scene.turrets[0]] = scene.NewView(900);
            Check(!scene.Authorize(100, out _, out _, turret: true), "nested foreign turret view accepted");
            Call("EnsureRpcRegistration", scene.View);
            scene.Deliver(100, ChangeRpc, 123, 5);
            Check(!(bool)scene.Messages.Single().Args[1] && scene.View.GetZDO().GetInt("odinship_turret_mode") == 0, "foreign turret request wrote");
            scene.parentViews[scene.turrets[0]] = scene.View;
            scene.parentShips[scene.turrets[0]] = scene.Fake<Ship>();
            Check(!scene.Authorize(100, out _, out _, turret: true), "foreign turret ship accepted");
            scene.parentShips[scene.turrets[0]] = scene.Ship;
            scene.names[scene.turrets[0]] = "unrelated turret";
            Check(!scene.Authorize(100, out _, out _, turret: true), "unrecognized turret accepted");
        });

        Case("selects a nearby ship turret and validates the local hovered component", scene =>
        {
            scene.Place(scene.turrets[0], new Vector3(30, 0, 0));
            Turret nearby = scene.Fake<Turret>();
            scene.names[nearby] = "warshipturret";
            scene.parentViews[nearby] = scene.View;
            scene.parentShips[nearby] = scene.Ship;
            scene.Place(nearby, Vector3.zero);
            scene.turrets = new[] { scene.turrets[0], nearby };
            Check(scene.Authorize(100, out _, out _, turret: true), "first distant turret hid a nearby valid turret");
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            scene.HoverTurret = nearby;
            Call("TryRouteTurretMode", scene.View);
            Check(scene.Messages.Count == 1 && (int)scene.Messages[0].Args[1] == 5, "valid hovered turret did not route");
            scene.Messages.Clear();
            scene.parentViews[nearby] = scene.NewView(900);
            Call("TryRouteTurretMode", scene.View);
            Check(scene.Messages.Count == 0 && scene.Notifications.Last().Contains("unavailable"), "foreign hovered turret routed");
        });

        Case("preserves vendor owner customization and turret permissions", scene =>
        {
            scene.Peer(100);
            scene.View.GetZDO().SetOwnerInternal(100);
            Player.m_localPlayer = scene.Requester;
            scene.Place(scene.Requester, new Vector3(50, 0, 0));
            scene.View.GetZDO().Set(ZDOVars.s_user, scene.OtherPlayer.GetPlayerID());
            scene.View.GetZDO().Set("InUse", 1);
            scene.AddWard(55555);
            Check((bool)Call("CanUseCustomizationInputs", scene.View)! && (bool)Call("TryRouteTurretMode", scene.View)!, "owner acquired new range or access checks");
            scene.CycleSail();
            Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 1 && scene.PendingChanges == 0
                && scene.Messages.Count(message => message.Name == "OnCustomizationChanged") == 1
                && scene.Notifications.Last() == "Sail: fixture", "owner vendor write was routed or blocked");
            scene.Place(scene.Requester, Vector3.zero);
            scene.SynchronousLocalDispatch = true;
            scene.SubmitName("helm-denied");
            Check(scene.View.GetZDO().GetString("shipName") == string.Empty && scene.PendingNames == 0
                && scene.Notifications.Last().Contains("helm"), "rename did not retain owner access validation");
        });

        test("OdinShip runtime registers rename results after setup preceded activation", () =>
        {
            using (var scene = new Scene(vendor, verify: false))
            {
                Call("RegisterShipRpcs", scene.customization);
                Check(scene.RegistrationCount == 1, "pre-enable setup registered callbacks");
                OdinShipOwnershipPatch.Verify();
                scene.Peer(100);
                scene.View.GetZDO().SetOwnerInternal(100);
                Player.m_localPlayer = scene.Requester;
                scene.SynchronousLocalDispatch = true;
                scene.SubmitName("early setup");
                Check(scene.NamesAtDispatch == 1 && scene.PendingNames == 0 && scene.RegistrationCount == 3
                    && scene.Notifications.Last() == "Ship name updated.", "early setup lost the immediate owner result");
            }
        });

        Case("clears a failed rename submission while preserving its exception", scene =>
        {
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            scene.FailNameSend = true;
            try { scene.SubmitName("failed"); throw new Exception("send failure did not propagate"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
            Check(scene.PendingNames == 0 && scene.Warnings.Single().Contains("fixture rename send failure")
                && scene.Notifications.Last().Contains("could not complete"), "failed rename retained pending state or lacked feedback");
        });

        Case("preserves hooks callbacks and pending requests across simulation rollback", scene =>
        {
            scene.Peer(100);
            Player.m_localPlayer = scene.Requester;
            scene.CycleSail();
            var change = scene.Messages.Single();
            scene.Messages.Clear();
            scene.SubmitName("after rollback");
            var name = scene.Messages.Single();
            Check(scene.PendingChanges == 1 && scene.PendingNames == 1, "fixture did not create both pending requests");
            MethodInfo target = AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects");
            var core = new Harmony(SimulationPatch.CoreOwner);
            var vendorHooks = new Harmony("Serverbound.Tests.OdinShip.RestoredVendor");
            int hooks = Harmony.GetAllPatchedMethods().Count(method => Harmony.GetPatchInfo(method)!.Owners.Contains(OdinShipOwnershipPatch.Owner));
            SimulationPatch.Prepare(_ => null);
            try
            {
                core.Patch(target, prefix: TestHook(nameof(Skip)));
                vendorHooks.Patch(target, postfix: TestHook(nameof(VendorHook)));
                Patch removed = Harmony.GetPatchInfo(target)!.Postfixes.Single(patch => patch.owner == vendorHooks.Id);
                SimulationPatch.RememberRemoved((target, removed, HarmonyPatchType.Postfix));
                vendorHooks.Unpatch(target, HarmonyPatchType.Postfix, vendorHooks.Id);
                Plugin.DisableSimulation();
                Plugin.DisableSimulation();
                Check(!SimulationPatch.Active && !Harmony.GetPatchInfo(target)!.Owners.Contains(SimulationPatch.CoreOwner), "Core survived rollback");
                Check(Harmony.GetPatchInfo(target)!.Postfixes.Count(patch => patch.owner == vendorHooks.Id) == 1, "vendor restoration duplicated or failed");
                Check(OdinShipOwnershipPatch.IsInstalled && hooks == Harmony.GetAllPatchedMethods().Count(method => Harmony.GetPatchInfo(method)!.Owners.Contains(OdinShipOwnershipPatch.Owner)), "OdinShip status or hooks changed");
                Check(scene.PendingChanges == 1 && scene.PendingNames == 1 && scene.Errors.All(error => error.StartsWith("Simulation: DISABLED")), "rollback cleared pending state or reported inconsistent status");
                scene.Peer(900);
                Player.m_localPlayer = null;
                scene.Messages.Clear();
                scene.Deliver(100, change.Name, change.Args);
                scene.Deliver(100, name.Name, name.Args);
                var results = scene.Messages.Where(message => message.Name == ResultRpc).ToArray();
                Check(scene.View.GetZDO().GetInt("odinship_sail_index") == 1 && scene.View.GetZDO().GetString("shipName") == "after rollback"
                    && results.Length == 2 && results.All(result => (bool)result.Args[1]), "registered callbacks stopped after rollback");
                scene.Peer(100);
                Player.m_localPlayer = scene.Requester;
                foreach (var result in results) scene.Deliver(900, result.Name, result.Args);
                Check(scene.PendingChanges == 0 && scene.PendingNames == 0, "post-rollback results did not complete");
                scene.CycleSail();
                scene.SubmitName("timeout");
                scene.Clock += 6;
                OdinShipOwnershipPatch.Update();
                Check(scene.PendingChanges == 0 && scene.PendingNames == 0
                    && scene.Notifications.Count(message => message.Contains("did not respond")) == 2, "pending timeouts stopped after rollback");
            }
            finally { core.UnpatchSelf(); vendorHooks.UnpatchSelf(); }
        });

        test("OdinShip runtime simulation rollback preserves a group awaiting verification", () =>
        {
            using (var scene = new Scene(vendor, verify: false))
            {
                Plugin.DisableSimulation();
                Check(!OdinShipOwnershipPatch.IsInstalled, "rollback enabled an unverified group");
                OdinShipOwnershipPatch.Verify();
                Check(OdinShipOwnershipPatch.IsInstalled, "rollback removed the pending group");
            }
        });
    }

    private sealed class Scene : IDisposable
    {
        private readonly Harmony fixture = new Harmony(FixtureOwner);
        private readonly List<IntPtr> pointers = new List<IntPtr>();
        private readonly List<ZDO> zdos = new List<ZDO>();
        internal readonly Dictionary<Component, Transform> transforms = new Dictionary<Component, Transform>();
        internal readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
        internal readonly Dictionary<Object, string> names = new Dictionary<Object, string>();
        internal readonly Dictionary<Component, ZNetView> parentViews = new Dictionary<Component, ZNetView>();
        internal readonly Dictionary<Component, Ship> parentShips = new Dictionary<Component, Ship>();
        private readonly object? previousPlayers = AccessTools.Field(typeof(Player), "s_players").GetValue(null);
        private readonly object? previousNet = AccessTools.Field(typeof(ZNet), "m_instance").GetValue(null);
        private readonly object? previousRouted = AccessTools.Field(typeof(ZRoutedRpc), "s_instance").GetValue(null);
        private readonly object? previousWards = AccessTools.Field(typeof(PrivateArea), "m_allAreas").GetValue(null);
        private readonly Player? previousLocal = Player.m_localPlayer;
        private readonly Action<string> previousWarning = CompatibilityInstaller.Warning;
        private readonly Action<string> previousError = CompatibilityInstaller.Error;
        internal readonly Type customizationType;
        internal readonly Component customization;
        internal Turret[] turrets;
        internal GameObject HoverObject = null!;
        internal Turret? HoverTurret;
        internal bool SynchronousLocalDispatch;
        internal bool FailNameSend;
        internal int NamesAtDispatch;
        private readonly MethodInfo cycleSail;
        private readonly MethodInfo setText;
        private readonly List<Material> sailMaterials = new List<Material>();
        internal long LocalPeer = 900;
        internal float Clock = 10;
        internal readonly List<(long Target, string Name, object[] Args)> Messages = new List<(long, string, object[])>();
        internal readonly List<string> Notifications = new List<string>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Errors = new List<string>();
        internal ZNetView View = null!;
        internal Ship Ship = null!;
        internal Player Requester = null!;
        internal Player OtherPlayer = null!;
        internal int PendingChanges => ((IDictionary)AccessTools.Field(typeof(OdinShipOwnershipPatch), "pendingChanges").GetValue(null)).Count;
        internal int PendingNames => ((IDictionary)AccessTools.Field(typeof(OdinShipOwnershipPatch), "pendingNames").GetValue(null)).Count;
        internal int RegistrationCount => ((IDictionary)AccessTools.Field(typeof(ZNetView), "m_functions").GetValue(View)).Count;

        internal Scene(Assembly vendor, bool verify = true)
        {
            current = this;
            customizationType = vendor.GetType("OdinShip.ShipCustomization", true)!;
            customization = null!;
            turrets = Array.Empty<Turret>();
            cycleSail = AccessTools.Method(vendor.GetType("OdinShip.ShipCustomizationPatch", true), "CycleMaterial");
            setText = AccessTools.Method(customizationType, "SetText");
            try
            {
                Hook(typeof(ZNet), "GetUID", nameof(PeerId));
                CompatibilityInstaller.Warning = Warnings.Add;
                CompatibilityInstaller.Error = Errors.Add;
                Hook(typeof(ZDOMan), "GetSessionID", nameof(PeerId));
                Hook(typeof(ZDO), "IncreaseDataRevision", nameof(Skip));
                Hook(typeof(Component), "get_transform", nameof(Transform));
                Hook(typeof(Transform), "get_position", nameof(Position));
                Hook(typeof(Object), "get_name", nameof(Name));
                fixture.Patch(AccessTools.Method(typeof(PrivateArea), "IsInside"), transpiler: TestHook(nameof(SceneAccess)));
                fixture.Patch(AccessTools.Method(typeof(Player), "Message", new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) }),
                    prefix: TestHook(nameof(Notify)));
                fixture.Patch(AccessTools.Method(typeof(ZRoutedRpc), "InvokeRoutedRPC", new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) }),
                    prefix: TestHook(nameof(Queue)));
                foreach (string method in new[] { "TryAuthorize", "HandleChangeRequest", "TryGetInteractionAnchor", "BelongsToShip",
                    "TryRouteTurretMode", "StartRequest", "TrackNameRequest", "Update" })
                    fixture.Patch(AccessTools.Method(typeof(OdinShipOwnershipPatch), method), transpiler: TestHook(nameof(SceneAccess)));
                AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, Fake<ZNet>());
                AccessTools.Field(typeof(Player), "s_players").SetValue(null, new List<Player>());
                AccessTools.Field(typeof(PrivateArea), "m_allAreas").SetValue(null, new List<PrivateArea>());
                new ZRoutedRpc(true).SetUID(LocalPeer);
                foreach (long id in new[] { 100L, 200L, 900L })
                {
                    var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
                    peer.m_uid = id;
                    ZRoutedRpc.instance.m_peers.Add(peer);
                }
                Requester = NewPlayer(100, 10001);
                OtherPlayer = NewPlayer(200, 100);
                Player.m_localPlayer = null;
                View = NewView(900);
                Ship = Fake<Ship>();
                AccessTools.Field(typeof(Ship), "m_players").SetValue(Ship, new List<Player> { Requester, OtherPlayer });
                var controls = Fake<ShipControlls>();
                AccessTools.Field(typeof(ShipControlls), "m_ship").SetValue(controls, Ship);
                AccessTools.Field(typeof(ShipControlls), "m_nview").SetValue(controls, View);
                Ship.m_shipControlls = controls;
                parentViews[controls] = View;
                parentShips[controls] = Ship;
                customization = (Component)Fake(customizationType);
                AccessTools.Field(customizationType, "m_nview").SetValue(customization, View);
                sailMaterials.AddRange(new[] { Fake<Material>(), Fake<Material>(), Fake<Material>() });
                AccessTools.Field(customizationType, "SailMaterials").SetValue(customization, sailMaterials);
                turrets = new[] { Fake<Turret>() };
                names[turrets[0]] = "warshipturret";
                parentViews[turrets[0]] = View;
                parentShips[turrets[0]] = Ship;
                HoverTurret = turrets[0];
                HoverObject = Fake<GameObject>();
                AccessTools.Field(typeof(Player), "m_hovering").SetValue(Requester, HoverObject);
                Place(View, Vector3.zero);
                Place(controls, Vector3.zero);
                Place(turrets[0], Vector3.zero);
                Place(Requester, Vector3.zero);
                Place(OtherPlayer, Vector3.zero);
                MethodInfo rename = AccessTools.Method(customizationType, "RPC_SetShipName");
                View.Register<string>("RPC_SetShipName", (sender, name) => rename.Invoke(customization, new object[] { sender, name }));
                OdinShipOwnershipPatch.ResetForTests();
                OdinShipOwnershipPatch.Prepare(vendor);
                if (verify)
                {
                    OdinShipOwnershipPatch.Verify();
                    Check(OdinShipOwnershipPatch.IsInstalled, "runtime integration did not pass verification");
                }
            }
            catch { Dispose(); throw; }
        }

        internal void Peer(long peer)
        {
            LocalPeer = peer;
            ZRoutedRpc.instance.SetUID(peer);
            // Each real peer has its own ZDO copies and cached local-owner bit.
            foreach (ZDO zdo in zdos) zdo.SetOwnerInternal(zdo.GetOwner());
        }

        internal bool Authorize(long sender, out Player? player, out string rejection, bool turret = false)
        {
            Type interaction = typeof(OdinShipOwnershipPatch).GetNestedType("InteractionKind", BindingFlags.NonPublic)!;
            object?[] args = { View, sender, Enum.Parse(interaction, turret ? "Turret" : "Controls"), null, null };
            bool accepted = (bool)AccessTools.Method(typeof(OdinShipOwnershipPatch), "TryAuthorize").Invoke(null, args)!;
            player = (Player?)args[3];
            rejection = (string)args[4]!;
            return accepted;
        }

        internal void Deliver(long sender, string method, params object[] args)
        {
            var package = new ZPackage();
            ZRpc.Serialize(args, ref package);
            package.SetPos(0);
            View.HandleRoutedRPC(new ZRoutedRpc.RoutedRPCData { m_senderPeerID = sender, m_methodHash = method.GetStableHashCode(), m_parameters = package });
        }

        internal void CycleSail() => cycleSail.Invoke(null, new object[] { View, sailMaterials, "odinship_sail_index", "Sail" });
        internal void SubmitName(string text) => setText.Invoke(customization, new object[] { text });
        internal void SetDead(bool dead) => ((ZNetView)AccessTools.Field(typeof(Player), "m_nview").GetValue(Requester)).GetZDO().Set(ZDOVars.s_dead, dead);

        internal PrivateArea AddWard(long creator)
        {
            PrivateArea ward = Fake<PrivateArea>();
            ZNetView view = NewView(LocalPeer);
            view.GetZDO().Set(ZDOVars.s_enabled, true);
            AccessTools.Field(typeof(PrivateArea), "m_nview").SetValue(ward, view);
            AccessTools.Field(typeof(PrivateArea), "m_radius").SetValue(ward, 20f);
            Piece piece = Fake<Piece>();
            AccessTools.Field(typeof(Piece), "m_creator").SetValue(piece, creator);
            AccessTools.Field(typeof(PrivateArea), "m_piece").SetValue(ward, piece);
            Place(ward, Vector3.zero);
            ((List<PrivateArea>)AccessTools.Field(typeof(PrivateArea), "m_allAreas").GetValue(null)).Add(ward);
            return ward;
        }

        internal void Permit(PrivateArea ward, long profileId)
        {
            var permitted = (List<KeyValuePair<long, string>>)AccessTools.Method(typeof(PrivateArea), "GetPermittedPlayers").Invoke(ward, null);
            permitted.Add(new KeyValuePair<long, string>(profileId, "fixture"));
            AccessTools.Method(typeof(PrivateArea), "SetPermittedPlayers").Invoke(ward, new object[] { permitted });
        }

        internal void Place(Component component, Vector3 position)
        {
            if (!transforms.TryGetValue(component, out Transform transform)) transforms[component] = transform = Fake<Transform>();
            positions[transform] = position;
        }

        private Player NewPlayer(long peer, long profile)
        {
            Player player = Fake<Player>();
            ZNetView view = NewView(peer);
            AccessTools.Field(typeof(Character), "m_nview").SetValue(player, view);
            AccessTools.Field(typeof(Player), "m_nview").SetValue(player, view);
            view.GetZDO().Set(ZDOVars.s_playerID, profile);
            Player.GetAllPlayers().Add(player);
            return player;
        }

        internal ZNetView NewView(long owner)
        {
            ZNetView view = Fake<ZNetView>();
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = new ZDOID(owner, (uint)++identifier);
            zdo.SetOwnerInternal(owner);
            zdos.Add(zdo);
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
            FieldInfo functions = AccessTools.Field(typeof(ZNetView), "m_functions");
            functions.SetValue(view, Activator.CreateInstance(functions.FieldType));
            return view;
        }

        private Object Fake(Type type)
        {
            var value = (Object)FormatterServices.GetUninitializedObject(type);
            IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
            pointers.Add(pointer);
            Marshal.WriteInt32(pointer, ++identifier);
            AccessTools.Field(typeof(Object), "m_CachedPtr").SetValue(value, pointer);
            return value;
        }

        internal T Fake<T>() where T : Object => (T)Fake(typeof(T));
        private void Hook(Type type, string method, string prefix) => fixture.Patch(AccessTools.Method(type, method), prefix: TestHook(prefix));

        public void Dispose()
        {
            OdinShipOwnershipPatch.ResetForTests();
            fixture.UnpatchSelf();
            AccessTools.Field(typeof(Player), "s_players").SetValue(null, previousPlayers);
            AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, previousNet);
            AccessTools.Field(typeof(ZRoutedRpc), "s_instance").SetValue(null, previousRouted);
            AccessTools.Field(typeof(PrivateArea), "m_allAreas").SetValue(null, previousWards);
            Player.m_localPlayer = previousLocal;
            CompatibilityInstaller.Warning = previousWarning;
            CompatibilityInstaller.Error = previousError;
            current = null;
            foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer);
            pointers.Clear();
        }
    }

    private static object? Call(string method, params object[] args) => AccessTools.Method(typeof(OdinShipOwnershipPatch), method).Invoke(null, args);
    private static HarmonyMethod TestHook(string name) => new HarmonyMethod(AccessTools.Method(typeof(OdinShipRuntimeTests), name));
    private static bool Skip() => false;
    private static void VendorHook() { }
    private static bool PeerId(ref long __result) { __result = current!.LocalPeer; return false; }
    private static bool Transform(Component __instance, ref Transform __result)
    {
        Scene scene = current!;
        if (!scene.transforms.ContainsKey(__instance)) scene.Place(__instance, Vector3.zero);
        __result = scene.transforms[__instance];
        return false;
    }
    private static bool Position(Transform __instance, ref Vector3 __result) { __result = current!.positions[__instance]; return false; }
    private static bool Name(Object __instance, ref string __result) { __result = current!.names.TryGetValue(__instance, out string name) ? name : "fixture"; return false; }
    private static bool Notify(string __1) { current!.Notifications.Add(__1); return false; }
    private static bool Queue(long __0, ZDOID __1, string __2, object[] __3)
    {
        Scene scene = current!;
        if (__2 == "RPC_SetShipName")
        {
            scene.NamesAtDispatch = scene.PendingNames;
            if (scene.FailNameSend) throw new InvalidOperationException("fixture rename send failure");
        }
        if (scene.SynchronousLocalDispatch && __0 == scene.LocalPeer && (__2 == "RPC_SetShipName" || __2 == ResultRpc || __2 == ChangeRpc))
            scene.Deliver(scene.LocalPeer, __2, __3);
        else scene.Messages.Add((__0, __2, __3));
        return false;
    }
    private static Ship? ShipFor(Component target) => ReferenceEquals(target, current!.View) ? current!.Ship : null;
    private static Component? ComponentFor(Component target, Type type) => ReferenceEquals(target, current!.View) && type == current!.customizationType ? current!.customization : null;
    private static Turret[] TurretsFor(Component target, bool includeInactive) => current!.turrets;
    private static ZNetView? ParentViewFor(Component target) => current!.parentViews.TryGetValue(target, out ZNetView view) ? view : null;
    private static Ship? ParentShipFor(Component target) => current!.parentShips.TryGetValue(target, out Ship ship) ? ship : null;
    private static Turret? HoverTurretFor(GameObject target) => ReferenceEquals(target, current!.HoverObject) ? current.HoverTurret : null;
    private static float Now() => current!.Clock;
    private static float DistanceXZ(Vector3 first, Vector3 second)
    {
        float x = first.x - second.x, z = first.z - second.z;
        return (float)Math.Sqrt(x * x + z * z);
    }
    private static IEnumerable<CodeInstruction> SceneAccess(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction source in instructions)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method)
            {
                string? helper = method.DeclaringType == typeof(Utils) && method.Name == "DistanceXZ" ? nameof(DistanceXZ)
                    : method.Name == "get_unscaledTime" ? nameof(Now)
                    : method.Name == "GetComponent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Ship) ? nameof(ShipFor)
                    : method.Name == "GetComponent" && !method.IsGenericMethod && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Type) ? nameof(ComponentFor)
                    : method.Name == "GetComponentInParent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(ZNetView) ? nameof(ParentViewFor)
                    : method.Name == "GetComponentInParent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Ship) ? nameof(ParentShipFor)
                    : method.Name == "GetComponentInParent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Turret) ? nameof(HoverTurretFor)
                    : method.Name == "GetComponentsInChildren" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Turret) ? nameof(TurretsFor) : null;
                if (helper != null)
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(OdinShipRuntimeTests), helper);
                }
            }
            yield return code;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
